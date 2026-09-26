using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class SettingsTests
{
    private string _dir = "";
    private string _file = "";

    [TestInitialize]
    public void Setup()
    {
        _dir  = Directory.CreateTempSubdirectory("kipo-settings-").FullName;
        _file = Path.Combine(_dir, "sub", "settings.json");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    [TestMethod]
    public void Load_MissingFile_ReturnsDefaults()
    {
        Assert.AreEqual(new AppSettings(), new SettingsStore(_file).Load());
    }

    [TestMethod]
    public void SaveThenLoad_RoundTrips()
    {
        var store    = new SettingsStore(_file);
        var settings = new AppSettings { PresetMb = 25, UseCustom = true, CustomMb = 7, CopyToClipboard = false };

        store.Save(settings);

        Assert.AreEqual(settings, store.Load());
    }

    [TestMethod]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ not json");

        Assert.AreEqual(new AppSettings(), new SettingsStore(_file).Load());
    }

    [TestMethod]
    public void Load_WrongTypes_ReturnsDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ \"PresetMb\": \"big\" }");

        Assert.AreEqual(new AppSettings(), new SettingsStore(_file).Load());
    }

    [TestMethod]
    public void Normalize_UnknownPreset_ResetsTo10()
    {
        Assert.AreEqual(10, new AppSettings { PresetMb = 33 }.Normalize().PresetMb);
    }

    [TestMethod]
    public void Normalize_CustomZero_ClampsTo1()
    {
        Assert.AreEqual(1, new AppSettings { CustomMb = 0 }.Normalize().CustomMb);
    }

    [TestMethod]
    public void Normalize_CustomHuge_ClampsTo10000()
    {
        Assert.AreEqual(10_000, new AppSettings { CustomMb = int.MaxValue }.Normalize().CustomMb);
    }

    [TestMethod]
    public void Load_OutOfRangeValues_AreNormalized()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ \"PresetMb\": 3, \"CustomMb\": -5 }");

        var settings = new SettingsStore(_file).Load();

        Assert.AreEqual(10, settings.PresetMb);
        Assert.AreEqual(1, settings.CustomMb);
    }

    [TestMethod]
    public void TargetBytes_Preset_UsesPreset()
    {
        Assert.AreEqual(25L * 1_048_576, new AppSettings { PresetMb = 25 }.TargetBytes);
    }

    [TestMethod]
    public void TargetBytes_Custom_UsesCustom()
    {
        Assert.AreEqual(7L * 1_048_576, new AppSettings { UseCustom = true, CustomMb = 7 }.TargetBytes);
    }

    [TestMethod]
    public void DataDir_EnvOverride_IsUsed()
    {
        var before = Environment.GetEnvironmentVariable("KIPO_DATA_DIR");

        try
        {
            Environment.SetEnvironmentVariable("KIPO_DATA_DIR", _dir);

            Assert.AreEqual(_dir, KipoPaths.DataDir);
            Assert.AreEqual(Path.Combine(_dir, "settings.json"), KipoPaths.SettingsFile);
            Assert.AreEqual(Path.Combine(_dir, "log.txt"), KipoPaths.LogFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIPO_DATA_DIR", before);
        }
    }
}
