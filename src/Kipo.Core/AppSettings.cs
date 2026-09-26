using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kipo.Core;

/// <summary>
/// The user's saved choices.
/// </summary>
/// <remarks>
/// <see cref="PresetMb"/> is one of <see cref="Presets"/>. When <see cref="UseCustom"/> is on,
/// <see cref="CustomMb"/> (1–10000) is the target instead. <see cref="CopyToClipboard"/>
/// controls whether a single-file result lands on the clipboard.
/// </remarks>
public sealed record AppSettings
{
    public const int  MinCustomMb = 1;
    public const int  MaxCustomMb = 10_000;
    public const long BytesPerMb  = 1_048_576;

    /// <summary>Preset sizes in MB, in combo box order. "Custom" follows them.</summary>
    public static IReadOnlyList<int> Presets { get; } = [8, 10, 25, 50, 100];

    public int PresetMb { get; init; } = 10;

    public bool UseCustom { get; init; }

    public int CustomMb { get; init; } = 20;

    public bool CopyToClipboard { get; init; } = true;

    /// <summary>Target size in bytes for the current choice.</summary>
    [JsonIgnore]
    public long TargetBytes => (UseCustom ? CustomMb : PresetMb) * BytesPerMb;

    /// <summary>
    /// Returns a copy with out-of-range values fixed.
    /// </summary>
    /// <returns>Unknown presets reset to 10 MB; custom size clamped to 1–10000.</returns>
    public AppSettings Normalize() => this with
    {
        PresetMb = Presets.Contains(PresetMb) ? PresetMb : 10,
        CustomMb = Math.Clamp(CustomMb, MinCustomMb, MaxCustomMb),
    };
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
/// </summary>
/// <remarks>
/// Load never throws: a missing, unreadable, or corrupt file gives defaults. Save
/// writes a temp file and swaps it in, so a crash mid-save can't leave half a file.
/// </remarks>
/// <param name="filePath">Full path of the settings file.</param>
public sealed class SettingsStore(string filePath)
{
    /// <summary>Reads settings, falling back to defaults.</summary>
    /// <returns>Normalized settings.</returns>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize(File.ReadAllText(filePath), SettingsJson.Default.AppSettings);

            return (settings ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Error("Couldn't read settings, using defaults", ex);

            return new AppSettings();
        }
    }

    /// <summary>Writes settings to disk.</summary>
    /// <param name="settings">Settings to save.</param>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var tempPath = filePath + ".tmp";

        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings.Normalize(), SettingsJson.Default.AppSettings));
        File.Move(tempPath, filePath, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
