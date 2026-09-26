using Kipo.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kipo;

/// <summary>
/// Asks before a window closes while its queue is still compressing.
/// </summary>
/// <remarks>
/// Shows "Stop compressing and close?" with Stop / Keep going. Stop cancels everything
/// (which deletes the unfinished temp files), waits for the queue to settle, then closes.
/// Used by the main window and the compress dialog.
/// </remarks>
public sealed class CloseGuard
{
    private readonly Window _window;
    private readonly CompressQueue _queue;
    private bool _confirmed;
    private bool _prompting;

    /// <summary>Watches <paramref name="window"/>'s close button.</summary>
    /// <param name="window">Window to guard.</param>
    /// <param name="queue">Its queue.</param>
    public CloseGuard(Window window, CompressQueue queue)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window                  = window;
        _queue                   = queue;
        window.AppWindow.Closing += OnClosing;
    }

    /// <summary>Fires when the user picks Stop, before the queue is canceled.</summary>
    public event EventHandler? StopConfirmed;

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_confirmed || !_queue.CanCancelAny)
        {
            return;
        }

        args.Cancel = true;

        if (_prompting || _window.Content.XamlRoot is null)
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
                XamlRoot          = _window.Content.XamlRoot,
            };

            if (await prompt.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            StopConfirmed?.Invoke(this, EventArgs.Empty);
            _queue.CancelAll();
            await _queue.WhenIdle();

            _confirmed = true;
            _window.Close();
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
