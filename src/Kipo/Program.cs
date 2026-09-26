using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Kipo;

/// <summary>
/// Entry point that keeps Kipo to a single running copy.
/// </summary>
/// <remarks>
/// The first launch registers itself under one key and starts the app. Any later
/// launch finds that copy, hands its activation over (which brings the existing
/// window to the front), and exits without opening a window of its own.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance"/>
public static class Program
{
    private const string InstanceKey = "Kipo";

    [STAThread]
    public static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // MSI uninstall: clean up and exit here, without the UI or handing off to a running copy
        if (Environment.GetCommandLineArgs() is [_, "--unregister-shell"])
        {
            ShellRegistration.Unregister();
            return 0;
        }

        if (RedirectToExistingInstance())
        {
            return 0;
        }

        Application.Start(callbackParams =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });

        return 0;
    }

    // True when another Kipo is already running and has been handed this launch
    private static bool RedirectToExistingInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (main.IsCurrent)
        {
            main.Activated += (_, args) => App.OnRedirected(args);
            return false;
        }

        // Redirect off the STA thread and block until done, as the docs require
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

        Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).GetAwaiter().GetResult();

        return true;
    }
}
