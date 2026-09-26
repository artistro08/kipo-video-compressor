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
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private RequestBatcher<CompressDialog>? _batcher;

    private static readonly Lock EarlyGate = new();
    private static readonly List<LaunchRequest> EarlyRequests = [];
    private static App? _ready;

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

    /// <summary>
    /// Called when a second launch is handed to this running copy: a right-click request
    /// opens or joins a compress dialog, anything else brings the main window forward.
    /// </summary>
    /// <param name="args">The second launch's activation.</param>
    public static void OnRedirected(Microsoft.Windows.AppLifecycle.AppActivationArguments args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var commandLine = args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : null;
        var request     = LaunchRequest.ParseCommandLine(commandLine);

        lock (EarlyGate)
        {
            // Still starting up: hold it until OnLaunched has set up routing
            if (_ready is not { _dispatcher: { } dispatcher } app)
            {
                EarlyRequests.Add(request);
                return;
            }

            dispatcher.TryEnqueue(() => app.Route(request));
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _batcher    = new RequestBatcher<CompressDialog>(
            () => DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(500),
            request =>
            {
                var dialog = new CompressDialog(request);
                dialog.Closed += (_, _) => _batcher?.Forget(dialog);   // later files open a new window
                dialog.Activate();
                return dialog;
            },
            (dialog, paths) => dialog.AddPaths(paths));

        var request = LaunchRequest.Parse(Environment.GetCommandLineArgs().Skip(1).ToList());

        Route(request);
        ShellRegistration.EnsureInBackground();

        // Launches handed over while this copy was still starting; after this, hand-offs route directly
        lock (EarlyGate)
        {
            _ready = this;

            foreach (var early in EarlyRequests)
            {
                Route(early);
            }

            EarlyRequests.Clear();
        }
    }

    // Compress requests go to a dialog; everything else shows the main window
    private void Route(LaunchRequest request)
    {
        switch (request.Action)
        {
            case LaunchAction.Compress:
                _batcher!.Submit(request);
                break;

            default:
                ShowMainWindow();
                break;
        }
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            _window         = new MainWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Activate();
            return;
        }

        _window.BringToFront();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        _window?.ShowError("Something went wrong. Details were saved to the log.");
    }
}
