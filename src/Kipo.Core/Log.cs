namespace Kipo.Core;

/// <summary>
/// Writes failures to Kipo's log file.
/// </summary>
/// <remarks>
/// Plain text, one entry per call, timestamped. When the file passes
/// <see cref="MaxBytes"/> it's renamed to <c>log.old.txt</c> (replacing any older one)
/// and a fresh file starts, so the log never grows past about 2 MB total. Logging
/// must never take the app down, so every file error is swallowed.
/// </remarks>
public static class Log
{
    private static readonly Lock Gate = new();

    /// <summary>Log file path. Tests point this at a temp folder.</summary>
    public static string FilePath { get; set; } = KipoPaths.LogFile;

    /// <summary>Size at which the log rolls over.</summary>
    public static long MaxBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Logs an error.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="exception">Optional exception; its full text is written.</param>
    public static void Error(string message, Exception? exception = null)
    {
        var entry = $"{DateTimeOffset.Now:u} ERROR {message}{Environment.NewLine}"
                  + (exception is null ? "" : exception + Environment.NewLine);

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RollIfNeeded();
                File.AppendAllText(FilePath, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Nowhere left to report this; drop it
            }
        }
    }

    private static void RollIfNeeded()
    {
        var file = new FileInfo(FilePath);

        if (file.Exists && file.Length > MaxBytes)
        {
            File.Move(FilePath, Path.Combine(file.DirectoryName!, "log.old.txt"), overwrite: true);
        }
    }
}
