using Kipo.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace Kipo;

/// <summary>
/// The small window the right-click menu opens.
/// </summary>
/// <remarks>
/// A preset starts compressing straight away. "Custom…" first asks for a size (pre-filled
/// with the saved custom size, which it also updates), then drills into the file list.
/// Launches that arrive while the dialog is open (the classic menu starts Kipo once per
/// file) add their files through <see cref="AddPaths"/>. Closing while something is still
/// compressing asks first; stopping cancels and removes the unfinished file.
/// </remarks>
public sealed partial class CompressDialog : Window
{
    private const int FormWidth    = 420;
    private const int ListWidth    = 580;
    private const int ShownFiles   = 3;

    private static readonly TimeSpan CopiedAlertDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DrillDuration       = TimeSpan.FromMilliseconds(300);

    private readonly SettingsStore _settingsStore = new(KipoPaths.SettingsFile);
    private readonly CompressQueue _queue;
    private readonly WindowFitter _fitter;
    private readonly List<string> _pending = [];
    private AppSettings _settings;
    private long _targetBytes;
    private bool _closeConfirmed;
    private bool _prompting;

    /// <summary>Opens the dialog for a right-click request.</summary>
    /// <param name="request">A <see cref="LaunchAction.Compress"/> request.</param>
    public CompressDialog(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        InitializeComponent();
        WindowChrome.Apply(this, AppTitleBar);

        _settings = _settingsStore.Load();
        _queue    = new CompressQueue((item, progress, token) => Compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));
        _fitter   = new WindowFitter(this, request.Custom ? FormWidth : ListWidth, () => false);

        // Close button only; the window sizes itself
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsResizable   = false;
        }

        _queue.BatchCompleted   += OnBatchCompleted;
        QueueView.LayoutChanged += (_, _) => _fitter.FitLater();
        QueueView.Error         += (_, message) => Log.Error(message);
        QueueView.Copied        += (_, _) => WindowChrome.ShowBriefly(CopiedBar, CopiedAlertDuration);
        QueueView.Attach(_queue);
        CopiedBar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) => _fitter.FitLater());
        AppWindow.Closing       += OnClosing;

        if (Content is FrameworkElement page)
        {
            page.Loaded += (_, _) => _fitter.FitFirst();
        }

        CustomDialogSizeBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnSizeKeyDown), handledEventsToo: true);

        if (request.Custom)
        {
            _pending.AddRange(request.Paths);
            ShowPendingFiles();
            CustomDialogSizeBox.Value = _settings.CustomMb;
            CustomForm.Visibility     = Visibility.Visible;
            QueueView.Visibility      = Visibility.Collapsed;
            Activated                += FocusSizeOnce;
        }
        else
        {
            _targetBytes = request.SizeMb!.Value * AppSettings.BytesPerMb;
            _queue.AddPaths(request.Paths, _targetBytes);
        }
    }

    /// <summary>Adds files from a launch that arrived while this dialog was open.</summary>
    /// <param name="paths">More files.</param>
    public void AddPaths(IReadOnlyList<string> paths)
    {
        if (CustomForm.Visibility == Visibility.Visible)
        {
            _pending.AddRange(paths);
            ShowPendingFiles();
            _fitter.FitLater();
            return;
        }

        _queue.AddPaths(paths, _targetBytes);
    }

    // =========================================================================
    // CUSTOM SIZE
    // =========================================================================

    private void FocusSizeOnce(object sender, WindowActivatedEventArgs args)
    {
        Activated -= FocusSizeOnce;
        CustomDialogSizeBox.Focus(FocusState.Programmatic);
    }

    private void OnSizeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            StartCustom();
        }
    }

    private void OnCompressClick(object sender, RoutedEventArgs e) => StartCustom();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    // Lists the first few selected files, then "and N more"
    private void ShowPendingFiles()
    {
        CustomFileList.ItemsSource = _pending.Take(ShownFiles).Select(Path.GetFileName).ToList();

        var more = _pending.Count - ShownFiles;

        CustomMoreFiles.Text       = more > 0 ? $"and {more} more" : "";
        CustomMoreFiles.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Saves the size, drills from the form into the list, and starts the pending files
    private void StartCustom()
    {
        if (CustomForm.Visibility != Visibility.Visible)
        {
            return;
        }

        var mb = TypedSize() ?? _settings.CustomMb;

        _settings    = (_settings with { CustomMb = mb }).Normalize();
        _targetBytes = _settings.CustomMb * AppSettings.BytesPerMb;

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't save settings", ex);
        }

        _fitter.SetWidth(ListWidth);
        DrillIntoList();
        _queue.AddPaths(_pending.ToList(), _targetBytes);
        _pending.Clear();
    }

    // What's in the box right now; the NumberBox may not have committed it to Value yet
    private int? TypedSize()
    {
        var text = FindDescendant<TextBox>(CustomDialogSizeBox)?.Text;

        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var typed))
        {
            return typed;
        }

        return double.IsNaN(CustomDialogSizeBox.Value) ? null : (int)Math.Round(CustomDialogSizeBox.Value);
    }

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);

            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void DrillIntoList()
    {
        QueueView.Visibility = Visibility.Visible;

        var story = new Storyboard();

        Fade(story, QueueView, 0, 1, 0.94, 1);
        Fade(story, CustomForm, 1, 0, 1, 1.04);
        story.Completed += (_, _) =>
        {
            CustomForm.Visibility = Visibility.Collapsed;
            _fitter.FitLater();
        };
        story.Begin();
    }

    private static void Fade(Storyboard story, UIElement target, double fromOpacity, double toOpacity, double fromScale, double toScale)
    {
        Add(story, target, nameof(UIElement.Opacity), fromOpacity, toOpacity);
        Add(story, target, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", fromScale, toScale);
        Add(story, target, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", fromScale, toScale);
    }

    private static void Add(Storyboard story, DependencyObject target, string property, double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From           = from,
            To             = to,
            Duration       = DrillDuration,
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 },
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }

    // =========================================================================
    // RESULTS
    // =========================================================================

    private async void OnBatchCompleted(object? sender, IReadOnlyList<QueueItem> batch)
    {
        var outputPath = CompressQueue.ShouldCopyToClipboard(batch, _settings.CopyToClipboard);

        if (outputPath is null)
        {
            return;
        }

        try
        {
            await ClipboardInput.CopyFileAsync(outputPath);
            WindowChrome.ShowBriefly(CopiedBar, CopiedAlertDuration);
        }
        catch (Exception ex) when (ex.HResult < 0 || ex is IOException)
        {
            Log.Error("Copy to clipboard failed", ex);
        }
    }

    // =========================================================================
    // CLOSING
    // =========================================================================

    // Closing mid-compress asks first; Stop cancels (removing .part files) then closes
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !_queue.CanCancelAny)
        {
            return;
        }

        args.Cancel = true;

        if (_prompting || Content.XamlRoot is null)
        {
            return;
        }

        _prompting = true;

        try
        {
            var prompt = new ContentDialog
            {
                Title             = "Stop compressing and close?",
                PrimaryButtonText = "Stop",
                CloseButtonText   = "Keep going",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = Content.XamlRoot,
            };

            if (await prompt.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _queue.CancelAll();
            await _queue.WhenIdle();

            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            Log.Error("Close prompt failed", ex);
        }
        finally
        {
            _prompting = false;
        }
    }
}
