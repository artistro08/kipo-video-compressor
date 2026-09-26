using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Kipo.Core;

/// <summary>
/// Moves file paths between Kipo and the Windows clipboard or a drop.
/// </summary>
/// <remarks>
/// Paste and drag-drop both hand over a <see cref="DataPackageView"/>, so one reader
/// serves both. Copied files (from Explorer) win; otherwise text is parsed as paths.
/// Anything else (images, HTML) gives an empty list. Paths aren't validated here;
/// <see cref="CompressQueue.AddPaths"/> does that. Other apps (clipboard history,
/// clipboard managers) can hold the clipboard for a moment right after it changes,
/// so reads and writes retry briefly.
/// </remarks>
public static class ClipboardInput
{
    /// <summary>How many times a busy clipboard is tried before giving up.</summary>
    public const int Attempts = 5;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Reads candidate paths from pasted or dropped data.
    /// </summary>
    /// <param name="view">Clipboard content or drop data.</param>
    /// <returns>Paths in order; empty when there's nothing usable.</returns>
    public static async Task<IReadOnlyList<string>> ReadPathsAsync(DataPackageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var items = await RetryAsync(() => view.GetStorageItemsAsync().AsTask());

            return items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToList();
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            return PathText.Parse(await RetryAsync(() => view.GetTextAsync().AsTask()));
        }

        return [];
    }

    /// <summary>
    /// Puts a file on the clipboard, ready to paste into Explorer or a chat app.
    /// </summary>
    /// <param name="path">File to copy.</param>
    /// <returns>A task that completes once the clipboard is set.</returns>
    public static async Task CopyFileAsync(string path)
    {
        var file    = await StorageFile.GetFileFromPathAsync(path);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };

        package.SetStorageItems([file]);

        await RetryAsync(() =>
        {
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return Task.FromResult(true);
        });
    }

    /// <summary>
    /// Runs a clipboard call, retrying while the clipboard is busy.
    /// </summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="action">The clipboard call.</param>
    /// <returns>The call's result.</returns>
    /// <exception cref="COMException">When the clipboard is still busy after <see cref="Attempts"/> tries.</exception>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (COMException) when (attempt < Attempts)
            {
                await Task.Delay(RetryDelay);
            }
        }
    }
}
