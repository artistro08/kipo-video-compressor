namespace Kipo.Tests;

/// <summary>
/// Real screen recordings used by video and UI tests.
/// </summary>
/// <remarks>
/// Files are copied into a temp folder before use; the sample folder is never written to.
/// Set KIPO_TEST_VIDEOS to point somewhere else. Missing files make the test Inconclusive.
/// </remarks>
internal static class TestVideos
{
    public const string Tiny  = "Screen Recording 2026-09-25 195601.mp4";   // 0.37 MB
    public const string Small = "Screen Recording 2026-08-06 082223.mp4";   // 1.2 MB
    public const string Mid   = "Screen Recording 2026-09-26 081940.mp4";   // 18.9 MB
    public const string Large = "Screen Recording 2026-07-27 075812.mp4";   // 129 MB

    public static readonly string[] Batch =
    [
        "Screen Recording 2026-09-18 092650.mp4",   // 0.70 MB
        "Screen Recording 2026-09-18 092820.mp4",   // 0.66 MB
        "Screen Recording 2026-09-18 113937.mp4",   // 0.71 MB
    ];

    public static string Folder =>
        Environment.GetEnvironmentVariable("KIPO_TEST_VIDEOS") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");

    public static string NewTempDir() => Directory.CreateTempSubdirectory("kipo-test-").FullName;

    public static string CopyToTemp(string dir, string name)
    {
        var source = Path.Combine(Folder, name);

        if (!File.Exists(source))
        {
            Assert.Inconclusive($"Sample video missing: {source}");
        }

        var copy = Path.Combine(dir, name);
        File.Copy(source, copy, overwrite: true);

        return copy;
    }
}
