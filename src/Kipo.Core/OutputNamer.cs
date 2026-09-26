namespace Kipo.Core;

/// <summary>
/// Picks file names for compressed output next to the source.
/// </summary>
/// <remarks>
/// Output is always MP4: <c>name-compressed.mp4</c>, then <c>name-compressed (2).mp4</c>
/// and up when a name is taken. While encoding, Kipo writes to a uniquely named
/// <c>name-compressed.&lt;id&gt;.part</c> and renames it at the end, so a half-written file never
/// looks finished and two windows compressing same-named files never share a temp file.
/// </remarks>
public static class OutputNamer
{
    /// <summary>
    /// Returns the first free output path for a source file.
    /// </summary>
    /// <param name="sourcePath">Full path of the source video.</param>
    /// <param name="exists">Returns true when a path is already taken. Pass <see cref="File.Exists"/> in real code.</param>
    /// <returns>Full output path.</returns>
    public static string GetOutputPath(string sourcePath, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var (folder, name) = Split(sourcePath);
        var candidate      = Path.Combine(folder, $"{name}-compressed.mp4");

        for (var number = 2; exists(candidate); number++)
        {
            candidate = Path.Combine(folder, $"{name}-compressed ({number}).mp4");
        }

        return candidate;
    }

    /// <summary>
    /// Returns a new, unique temp path for one encode.
    /// </summary>
    /// <param name="sourcePath">Full path of the source video.</param>
    /// <returns>Full <c>.part</c> path; different on every call.</returns>
    public static string GetPartPath(string sourcePath)
    {
        var (folder, name) = Split(sourcePath);

        return Path.Combine(folder, $"{name}-compressed.{Guid.NewGuid():N}.part");
    }

    private static (string Folder, string Name) Split(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        var folder = Path.GetDirectoryName(sourcePath) ?? throw new ArgumentException("Path has no folder.", nameof(sourcePath));

        return (folder, Path.GetFileNameWithoutExtension(sourcePath));
    }
}
