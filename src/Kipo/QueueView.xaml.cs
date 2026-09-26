using Kipo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Kipo;

/// <summary>
/// The file list shared by the main window and the compress dialog.
/// </summary>
/// <remarks>
/// Shows one row per file (icon/spinner/checkmark, status, Copy, Open file, Open in folder,
/// cancel) plus the separator above and the Cancel all link below. The list and separator
/// appear once there are files; Cancel all only while something can still be cancelled.
/// Raises <see cref="LayoutChanged"/> whenever its height changes so the window can resize,
/// <see cref="Copied"/> after a Copy, and <see cref="Error"/> with a plain-language message when a file action fails.
/// </remarks>
public sealed partial class QueueView : UserControl
{
    private CompressQueue? _queue;

    public QueueView()
    {
        InitializeComponent();
    }

    /// <summary>Fires when the list's height may have changed.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Fires with a message when Copy, Open file or Open in folder fails.</summary>
    public event EventHandler<string>? Error;

    /// <summary>Fires after a row's Copy button put its file on the clipboard.</summary>
    public event EventHandler? Copied;

    /// <summary>
    /// Connects the view to a queue. Call once.
    /// </summary>
    /// <param name="queue">The queue to show.</param>
    public void Attach(CompressQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        _queue                        = queue;
        QueueList.ItemsSource         = queue.Items;
        queue.Items.CollectionChanged += (_, _) => UpdateQueueVisibility();
        queue.PropertyChanged         += (_, _) => UpdateCancelAll();

        UpdateQueueVisibility();
        UpdateCancelAll();
    }

    // List and separator only show once there are files
    private void UpdateQueueVisibility()
    {
        var hasItems = _queue!.Items.Count > 0;

        QueueList.Visibility      = hasItems ? Visibility.Visible : Visibility.Collapsed;
        QueueSeparator.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    // Cancel all only shows while a row is waiting or compressing
    private void UpdateCancelAll()
    {
        var visible = _queue!.CanCancelAny ? Visibility.Visible : Visibility.Collapsed;

        if (CancelAllLink.Visibility != visible)
        {
            CancelAllLink.Visibility = visible;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCancelItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueueItem item)
        {
            item.Cancel();
        }
    }

    private void OnCancelAllClick(object sender, RoutedEventArgs e) => _queue?.CancelAll();

    // Puts the row's result (compressed file, or the original when under target) on the clipboard
    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QueueItem { ResultPath: { } path })
        {
            return;
        }

        try
        {
            await ClipboardInput.CopyFileAsync(path);
            Copied?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex.HResult < 0 || ex is IOException)
        {
            Log.Error("Copy to clipboard failed", ex);
            Error?.Invoke(this, "Couldn't copy the file.");
        }
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e) => RunFileAction(sender, FileActions.OpenFile, "Couldn't open the file.");

    private void OnOpenInFolderClick(object sender, RoutedEventArgs e) => RunFileAction(sender, FileActions.OpenInFolder, "Couldn't open the folder.");

    // Starts the row's action for its result (compressed file, or the original when under target); failures raise Error
    private void RunFileAction(object sender, Func<string, System.Diagnostics.ProcessStartInfo> action, string failure)
    {
        if ((sender as FrameworkElement)?.DataContext is not QueueItem { ResultPath: { } path })
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(action(path));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            Log.Error(failure, ex);
            Error?.Invoke(this, failure);
        }
    }
}
