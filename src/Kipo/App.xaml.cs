using Kipo.Core;
using Microsoft.UI.Xaml;

namespace Kipo;

/// <summary>
/// Starts Kipo and catches anything that would otherwise crash it silently.
/// </summary>
/// <remarks>
/// UI-thread exceptions are logged and shown in a dialog, then marked handled so the
/// window stays open. Background exceptions are logged.
/// </remarks>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // Theme override for testing light and dark without changing the Windows theme
        RequestedTheme = Environment.GetEnvironmentVariable("KIPO_THEME") switch
        {
            "Light" => ApplicationTheme.Light,
            "Dark"  => ApplicationTheme.Dark,
            _       => RequestedTheme,
        };

        // Crash Handling
        UnhandledException                         += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException      += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Called when a second launch is redirected here; shows the existing window.</summary>
    public static void BringMainWindowToFront()
    {
        if (Current is App { _window: { } window })
        {
            window.DispatcherQueue.TryEnqueue(window.BringToFront);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        _window?.ShowError("Something went wrong. Details were saved to the log.");
    }
}
