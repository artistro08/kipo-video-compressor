namespace Kipo.Core;

/// <summary>
/// Reasons an input path is rejected. Shown after "Failed: " in the queue.
/// </summary>
public static class InputErrors
{
    public const string UnsupportedPath = "unsupported path";
    public const string NotAVideo       = "not a video";
    public const string NotFound        = "not found";
}

/// <summary>
/// Checks that a path from outside the app is a local video file we're willing to open.
/// </summary>
/// <remarks>
/// Every path from the clipboard, a drop, or the picker passes through here before
/// Kipo touches the file. Only plain drive-letter paths are allowed. URLs, relative
/// paths, network (UNC) paths, device paths (<c>\\?\</c>, <c>\\.\</c>) and NTFS
/// alternate data streams (<c>file.mp4:stream</c>) are rejected.
/// </remarks>
public static class PathValidator
{
    /// <summary>File extensions Kipo accepts, lower case with dot.</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } = [".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v"];

    /// <summary>
    /// Validates one path.
    /// </summary>
    /// <param name="path">Candidate path.</param>
    /// <returns>Null when the path is usable, otherwise an <see cref="InputErrors"/> value.</returns>
    public static string? Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!IsPlainLocalPath(path))
        {
            return InputErrors.UnsupportedPath;
        }

        if (!VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            return InputErrors.NotAVideo;
        }

        return File.Exists(path) ? null : InputErrors.NotFound;
    }

    // Drive letter, colon, slash; no other colons; no invalid characters
    private static bool IsPlainLocalPath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
        {
            return false;
        }

        if (path.IndexOf(':', 2) >= 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        return Path.IsPathFullyQualified(path);
    }
}
