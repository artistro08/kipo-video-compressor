using Kipo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
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
    private const int WindowWidth  = 420;
    private const int WindowHeight = 640;

    private const double DropZoneWidthShare = 0.85;

    private static readonly TimeSpan CopiedAlertDuration = TimeSpan.FromSeconds(3);

    private readonly SettingsStore _settingsStore = new(KipoPaths.SettingsFile);
    private readonly CompressQueue _queue;
    private AppSettings _settings;
    private bool _loadingControls;
    private bool _dialogOpen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _copiedTimer;

    public MainWindow()
    {
        InitializeComponent();

        // Title Bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _settings = _settingsStore.Load();
        _queue    = new CompressQueue((item, progress, token) => Compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));

        _queue.BatchCompleted += OnBatchCompleted;
        QueueList.ItemsSource  = _queue.Items;
        Root.Loaded           += (_, _) => ResizeForDpi();

        _queue.Items.CollectionChanged += (_, _) => UpdateQueueVisibility();

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

    private void OnCancelItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueueItem item)
        {
            item.Cancel();
        }
    }

    // Empty: drop section centered in the window. With files: drop section on top, queue and Cancel all below
    private void UpdateQueueVisibility()
    {
        var hasItems = _queue.Items.Count > 0;

        QueueList.Visibility      = hasItems ? Visibility.Visible : Visibility.Collapsed;
        QueueSeparator.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        CancelAllLink.Visibility  = hasItems ? Visibility.Visible : Visibility.Collapsed;
        DropRow.Height           = hasItems ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        QueueRow.Height          = hasItems ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }

    private void OnCancelAllClick(object sender, RoutedEventArgs e) => _queue.CancelAll();

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
            ShowCopiedAlert();
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

    private void OnCopyToggled(object sender, RoutedEventArgs e) =>
        SaveSettings(_settings with { CopyToClipboard = CopyToClipboardToggle.IsOn });

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

    // Copied alert hides itself after a few seconds
    private void ShowCopiedAlert()
    {
        CopiedBar.IsOpen = true;

        _copiedTimer ??= DispatcherQueue.CreateTimer();
        _copiedTimer.Interval    = CopiedAlertDuration;
        _copiedTimer.IsRepeating = false;
        _copiedTimer.Tick       -= OnCopiedTimerTick;
        _copiedTimer.Tick       += OnCopiedTimerTick;
        _copiedTimer.Start();
    }

    private void OnCopiedTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => CopiedBar.IsOpen = false;

    // =========================================================================
    // SETTINGS VIEW
    // =========================================================================

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings(true);

    private void OnBackRequested(TitleBar sender, object args) => ShowSettings(false);

    // Settings replace the main view; the title bar back arrow returns
    private void ShowSettings(bool show)
    {
        SettingsView.Visibility         = show ? Visibility.Visible : Visibility.Collapsed;
        Root.Visibility                 = show ? Visibility.Collapsed : Visibility.Visible;
        Footer.Visibility               = show ? Visibility.Collapsed : Visibility.Visible;
        AppTitleBar.IsBackButtonVisible = show;

        if (show)
        {
            CopiedBar.IsOpen = false;
        }
    }

    private void ResizeForDpi()
    {
        var scale = Content.XamlRoot?.RasterizationScale ?? 1.0;

        AppWindow.Resize(new SizeInt32((int)(WindowWidth * scale), (int)(WindowHeight * scale)));
    }
}
