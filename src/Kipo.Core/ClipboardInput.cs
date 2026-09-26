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
/// <see cref="CompressQueue.AddPaths"/> does that.
/// </remarks>
public static class ClipboardInput
{
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
            var items = await view.GetStorageItemsAsync();

            return items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToList();
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            return PathText.Parse(await view.GetTextAsync());
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
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
