using Kipo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Kipo;

/// <summary>
/// Kipo's only window: drop zone, size picker, queue, and settings.
/// </summary>
/// <remarks>
/// All real work is in Kipo.Core. This class turns clicks, pastes and drops into
/// <see cref="CompressQueue.AddPaths"/> calls, keeps the settings file in sync with
/// the controls, and copies a single-file result to the clipboard when enabled.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private const int    WindowWidth        = 520;
    private const double DropZoneWidthShare = 0.85;
    private const double BackButtonSize     = 32;

    private static readonly TimeSpan CopiedAlertDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ViewAnimation       = TimeSpan.FromMilliseconds(300);

    private readonly SettingsStore _settingsStore = new(KipoPaths.SettingsFile);
    private readonly CompressQueue _queue;
    private AppSettings _settings;
    private bool _loadingControls;
    private bool _dialogOpen;
    private readonly WindowFitter _fitter;
    private Storyboard? _viewStory;

    public MainWindow()
    {
        InitializeComponent();

        // Title Bar
        WindowChrome.Apply(this, AppTitleBar);

        _settings = _settingsStore.Load();
        _queue    = new CompressQueue((item, progress, token) => Compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));

        _fitter = new WindowFitter(this, WindowWidth, () => SettingsView.Visibility == Visibility.Visible);

        _queue.BatchCompleted   += OnBatchCompleted;
        Root.Loaded             += (_, _) => _fitter.FitFirst();
        QueueView.LayoutChanged += (_, _) => _fitter.FitLater();
        QueueView.Error         += (_, message) => ShowError(message);
        QueueView.Attach(_queue);
        CopiedBar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) => _fitter.FitLater());

        LoadControls();
    }

    /// <summary>Shows the window and brings it in front of other apps.</summary>
    public void BringToFront()
    {
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>Shows a simple error dialog.</summary>
    /// <remarks>
    /// WinUI allows one dialog at a time, and a failed ShowAsync would land back in the
    /// app's crash handler, which calls this again. So a second error while a dialog is
    /// open is only logged, and ShowAsync failures never escape.
    /// </remarks>
    /// <param name="message">Plain-language message.</param>
    public async void ShowError(string message)
    {
        if (_dialogOpen || Content.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;

        try
        {
            var dialog = new ContentDialog
            {
                Title           = "Kipo",
                Content         = message,
                CloseButtonText = "OK",
                XamlRoot        = Content.XamlRoot,
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            Log.Error("Couldn't show error dialog", ex);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // =========================================================================
    // INPUT
    // =========================================================================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();

        try
        {
            AddPaths(await ClipboardInput.ReadPathsAsync(e.DataView));
        }
        catch (Exception ex) when (ex.HResult < 0 || ex is IOException)
        {
            Log.Error("Drop failed", ex);
            ShowError("Couldn't read the dropped files.");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnPasteInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Let the custom size box handle its own paste
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox)
        {
            return;
        }

        args.Handled = true;

        try
        {
            AddPaths(await ClipboardInput.ReadPathsAsync(Clipboard.GetContent()));
        }
        catch (Exception ex) when (ex.HResult < 0 || ex is IOException)
        {
            Log.Error("Paste failed", ex);
            ShowError("Couldn't read the clipboard.");
        }
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);

            foreach (var extension in PathValidator.VideoExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            var picked = await picker.PickMultipleFilesAsync();

            AddPaths(picked.Select(result => result.Path).ToList());
        }
        catch (Exception ex) when (ex.HResult < 0 || ex is IOException)
        {
            Log.Error("File picker failed", ex);
            ShowError("Couldn't open the file picker.");
        }
    }

    private void AddPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        _queue.AddPaths(paths, _settings.TargetBytes);
    }

    // =========================================================================
    // QUEUE
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
    // SETTINGS
    // =========================================================================

    private void LoadControls()
    {
        _loadingControls = true;

        foreach (var mb in AppSettings.Presets)
        {
            SizeCombo.Items.Add($"{mb} MB");
        }

        SizeCombo.Items.Add("Custom");

        SizeCombo.SelectedIndex    = _settings.UseCustom ? AppSettings.Presets.Count : IndexOfPreset(_settings.PresetMb);
        CustomSizeBox.Value        = _settings.CustomMb;
        CustomSizeBox.Visibility   = _settings.UseCustom ? Visibility.Visible : Visibility.Collapsed;
        CopyToClipboardToggle.IsOn = _settings.CopyToClipboard;
        CopyToClipboardState.Text  = _settings.CopyToClipboard ? "On" : "Off";

        _loadingControls = false;
    }

    private void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        var useCustom = SizeCombo.SelectedIndex == AppSettings.Presets.Count;

        CustomSizeBox.Visibility = useCustom ? Visibility.Visible : Visibility.Collapsed;

        SaveSettings(useCustom
            ? _settings with { UseCustom = true }
            : _settings with { UseCustom = false, PresetMb = AppSettings.Presets[Math.Max(0, SizeCombo.SelectedIndex)] });
    }

    private void OnCustomSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            return;
        }

        SaveSettings(_settings with { CustomMb = (int)Math.Round(args.NewValue) });
    }

    private void OnCopyToggled(object sender, RoutedEventArgs e)
    {
        CopyToClipboardState.Text = CopyToClipboardToggle.IsOn ? "On" : "Off";
        SaveSettings(_settings with { CopyToClipboard = CopyToClipboardToggle.IsOn });
    }

    private void SaveSettings(AppSettings settings)
    {
        if (_loadingControls)
        {
            return;
        }

        _settings = settings.Normalize();

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't save settings", ex);
        }
    }

    private static int IndexOfPreset(int mb)
    {
        for (var i = 0; i < AppSettings.Presets.Count; i++)
        {
            if (AppSettings.Presets[i] == mb)
            {
                return i;
            }
        }

        return 1;
    }

    // =========================================================================
    // WINDOW
    // =========================================================================

    // Drop zone keeps a margin from the edges but grows and shrinks with the window
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) =>
        DropZone.Width = e.NewSize.Width * DropZoneWidthShare;


    // =========================================================================
    // SETTINGS VIEW
    // =========================================================================

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings(true);

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowSettings(false);

    // Settings replace the main view with a drill-in; the title bar back arrow drills out.
    // Drill in: settings grow from 94% while the main view grows to 104% and fades. Drill out: the reverse.
    private void ShowSettings(bool show)
    {
        FrameworkElement incoming = show ? SettingsView : MainView;
        FrameworkElement outgoing = show ? MainView : SettingsView;

        _viewStory?.Stop();

        if (show)
        {
            CopiedBar.IsOpen      = false;
            BackButton.Visibility = Visibility.Visible;
        }

        incoming.Visibility = Visibility.Visible;

        var story = new Storyboard();

        AddViewAnimation(story, incoming, 0, 1, show ? 0.94 : 1.04, 1);
        AddViewAnimation(story, outgoing, 1, 0, 1, show ? 1.04 : 0.94);

        // Back arrow slides open and fades in, or the reverse
        AddAnimation(story, BackSlot, nameof(FrameworkElement.Width), show ? 0 : BackButtonSize, show ? BackButtonSize : 0, dependent: true);
        AddAnimation(story, BackButton, nameof(UIElement.Opacity), show ? 0 : 1, show ? 1 : 0);

        story.Completed += (_, _) =>
        {
            outgoing.Visibility = Visibility.Collapsed;

            if (!show)
            {
                BackButton.Visibility = Visibility.Collapsed;
                _fitter.FitLater();
            }
        };

        _viewStory = story;
        story.Begin();
    }

    private static void AddViewAnimation(Storyboard story, UIElement view, double fromOpacity, double toOpacity, double fromScale, double toScale)
    {
        AddAnimation(story, view, nameof(UIElement.Opacity), fromOpacity, toOpacity);
        AddAnimation(story, view, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", fromScale, toScale);
        AddAnimation(story, view, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", fromScale, toScale);
    }

    private static void AddAnimation(Storyboard story, DependencyObject target, string property, double from, double to, bool dependent = false)
    {
        var animation = new DoubleAnimation
        {
            From                     = from,
            To                       = to,
            Duration                 = ViewAnimation,
            EnableDependentAnimation = dependent,
            EasingFunction           = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 },
        };

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        story.Children.Add(animation);
    }
}
