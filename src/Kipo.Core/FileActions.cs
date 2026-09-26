using System.Diagnostics;

namespace Kipo.Core;

/// <summary>
/// Launch settings for the finished-row buttons.
/// </summary>
/// <remarks>
/// "Open file" opens the compressed video in the default app; "Open in folder" opens
/// Explorer with the file selected. Only full local paths without quotes are accepted,
/// so nothing can break out of the Explorer argument.
/// </remarks>
public static class FileActions
{
    /// <summary>Opens the file with its default app.</summary>
    /// <param name="path">Full path of the compressed video.</param>
    /// <returns>Start info to pass to <see cref="Process.Start(ProcessStartInfo)"/>.</returns>
    /// <exception cref="ArgumentException">When the path isn't a full local path or contains a quote.</exception>
    public static ProcessStartInfo OpenFile(string path)
    {
        EnsureSafe(path);

        return new ProcessStartInfo(path) { UseShellExecute = true };
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    /// <param name="path">Full path of the compressed video.</param>
    /// <returns>Start info to pass to <see cref="Process.Start(ProcessStartInfo)"/>.</returns>
    /// <exception cref="ArgumentException">When the path isn't a full local path or contains a quote.</exception>
    public static ProcessStartInfo OpenInFolder(string path)
    {
        EnsureSafe(path);

        // Full path: a bare "explorer.exe" would also be looked up in the current folder
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        return new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false };
    }

    private static void EnsureSafe(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || path.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Expected a full local file path.", nameof(path));
        }
    }
}
