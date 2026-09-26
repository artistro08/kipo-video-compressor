namespace Kipo.Core;

/// <summary>
/// Turns pasted text into a list of candidate file paths.
/// </summary>
/// <remarks>
/// Pasted text can hold one path or many, one per line, often wrapped in quotes
/// (Explorer's "Copy as path" adds them). Blank lines are dropped. No validation
/// happens here; see <see cref="PathValidator"/>.
/// </remarks>
public static class PathText
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>
    /// Splits text into trimmed, unquoted, non-empty lines.
    /// </summary>
    /// <param name="text">Raw clipboard text. May be null.</param>
    /// <returns>Candidate paths in the order they appeared.</returns>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('"').Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
