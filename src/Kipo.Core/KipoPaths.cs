namespace Kipo.Core;

/// <summary>
/// Where Kipo keeps its settings and log.
/// </summary>
/// <remarks>
/// Defaults to <c>%LOCALAPPDATA%\Kipo</c>, which works in both the MSIX and MSI builds.
/// The <c>KIPO_DATA_DIR</c> environment variable overrides it so UI tests can run
/// without touching your real settings.
/// </remarks>
public static class KipoPaths
{
    /// <summary>Folder holding settings and log.</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("KIPO_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kipo");

    /// <summary>Settings JSON file.</summary>
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>Log file.</summary>
    public static string LogFile => Path.Combine(DataDir, "log.txt");
}
