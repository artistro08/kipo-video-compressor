using System.Text;

namespace Kipo.Shell;

/// <summary>One entry in the "Compress with Kipo" submenu.</summary>
/// <param name="Title">Text shown in the menu.</param>
/// <param name="Size">Size argument passed to Kipo.exe: a preset in MB, or "custom".</param>
/// <param name="SeparatorBefore">True when a divider goes above this entry.</param>
internal sealed record ShellMenuItem(string Title, string Size, bool SeparatorBefore);

/// <summary>
/// What the right-click menu shows and the command lines it runs.
/// </summary>
/// <remarks>
/// Kept free of COM so it can be unit tested. The preset list mirrors
/// <c>Kipo.Core.AppSettings.Presets</c> (a test pins them together); it isn't referenced
/// directly so the native add-in doesn't pull in the app's Windows Runtime code.
/// Command lines are split so none gets near Windows' 32,767-character limit; each piece
/// starts Kipo separately and the running copy groups them into one dialog.
/// </remarks>
internal static class ShellMenu
{
    public const string RootTitle = "Compress with Kipo";

    public static IReadOnlyList<ShellMenuItem> Items { get; } =
    [
        new("8 MB", "8", false),
        new("10 MB", "10", false),
        new("25 MB", "25", false),
        new("50 MB", "50", false),
        new("100 MB", "100", false),
        new("Custom…", "custom", true),
    ];

    /// <summary>
    /// Builds Kipo.exe argument strings for the chosen size and files.
    /// </summary>
    /// <param name="size">Size argument from <see cref="ShellMenuItem.Size"/>.</param>
    /// <param name="paths">Selected file paths.</param>
    /// <param name="maxLength">Longest allowed argument string.</param>
    /// <returns>One or more argument strings; empty when there are no paths.</returns>
    public static IReadOnlyList<string> CommandLines(string size, IReadOnlyList<string> paths, int maxLength = 30_000)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var prefix = $"--compress {size}";
        var lines  = new List<string>();
        var line   = new StringBuilder();

        foreach (var path in paths)
        {
            var argument = $" \"{path}\"";

            if (line.Length > 0 && line.Length + argument.Length > maxLength)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length == 0)
            {
                line.Append(prefix);
            }

            line.Append(argument);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines;
    }
}
