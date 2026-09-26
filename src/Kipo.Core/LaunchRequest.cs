using System.Globalization;
using System.Text;

namespace Kipo.Core;

/// <summary>What a Kipo launch should do.</summary>
public enum LaunchAction
{
    Main,
    Compress,
    UnregisterShell,
}

/// <summary>
/// A parsed Kipo command line.
/// </summary>
/// <remarks>
/// The right-click menu runs <c>Kipo.exe --compress &lt;8|10|25|50|100|custom&gt; "path" …</c>;
/// the MSI uninstaller runs <c>Kipo.exe --unregister-shell</c>. Anything else, including a
/// size that isn't a preset or a compress request with no paths, falls back to opening the
/// main window. Paths aren't validated here; the queue does that per row.
/// </remarks>
/// <param name="Action">What to do.</param>
/// <param name="SizeMb">Preset size for a compress request; null for custom or non-compress.</param>
/// <param name="Custom">True when the user picked "Custom…".</param>
/// <param name="Paths">Files to compress, in order.</param>
public sealed record LaunchRequest(LaunchAction Action, int? SizeMb, bool Custom, IReadOnlyList<string> Paths)
{
    private static readonly LaunchRequest MainWindow = new(LaunchAction.Main, null, false, []);

    /// <summary>
    /// Parses arguments (without the exe).
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The request; <see cref="LaunchAction.Main"/> when nothing valid was asked for.</returns>
    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 1 && args[0] == "--unregister-shell")
        {
            return new LaunchRequest(LaunchAction.UnregisterShell, null, false, []);
        }

        if (args.Count < 3 || args[0] != "--compress")
        {
            return MainWindow;
        }

        var paths = args.Skip(2).ToList();

        if (args[1] == "custom")
        {
            return new LaunchRequest(LaunchAction.Compress, null, true, paths);
        }

        return int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mb) && AppSettings.Presets.Contains(mb)
            ? new LaunchRequest(LaunchAction.Compress, mb, false, paths)
            : MainWindow;
    }

    /// <summary>
    /// Parses a whole command line, as handed to a running Kipo by a second launch.
    /// </summary>
    /// <param name="commandLine">Command line, with or without the exe path first.</param>
    /// <returns>The request.</returns>
    public static LaunchRequest ParseCommandLine(string? commandLine)
    {
        var tokens = CommandLine.Split(commandLine ?? "").ToList();

        if (tokens.Count > 0 && tokens[0].EndsWith("Kipo.exe", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveAt(0);
        }

        return Parse(tokens);
    }
}

/// <summary>
/// Splits a Windows command line into arguments.
/// </summary>
/// <remarks>
/// Whitespace separates arguments outside double quotes; quotes group and are removed;
/// <c>\"</c> inside quotes is a literal quote. Windows paths can't contain quotes, so this
/// covers everything the right-click menu produces.
/// </remarks>
public static class CommandLine
{
    /// <summary>Splits a command line.</summary>
    /// <param name="commandLine">Raw command line.</param>
    /// <returns>Arguments in order.</returns>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        var tokens  = new List<string>();
        var current = new StringBuilder();
        var quoted  = false;
        var started = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (c == '\\' && quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                quoted  = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
