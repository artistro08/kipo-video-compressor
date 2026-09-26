using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LogTests
{
    private string _dir = "";
    private string _originalPath = "";
    private long _originalMax;

    [TestInitialize]
    public void Setup()
    {
        _dir          = Directory.CreateTempSubdirectory("kipo-log-").FullName;
        _originalPath = Log.FilePath;
        _originalMax  = Log.MaxBytes;
        Log.FilePath  = Path.Combine(_dir, "nested", "log.txt");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Log.FilePath = _originalPath;
        Log.MaxBytes = _originalMax;
        Directory.Delete(_dir, true);
    }

    [TestMethod]
    public void Error_WritesMessageAndException()
    {
        Log.Error("boom happened", new InvalidOperationException("inner detail"));

        var text = File.ReadAllText(Log.FilePath);

        StringAssert.Contains(text, "boom happened");
        StringAssert.Contains(text, "inner detail");
    }

    [TestMethod]
    public void Error_OverMaxBytes_RollsToOld()
    {
        Log.MaxBytes = 100;

        Log.Error(new string('a', 200));
        Log.Error("fresh line");

        var oldPath = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "log.old.txt");

        Assert.IsTrue(File.Exists(oldPath));
        StringAssert.Contains(File.ReadAllText(Log.FilePath), "fresh line");
        Assert.IsFalse(File.ReadAllText(Log.FilePath).Contains(new string('a', 200), StringComparison.Ordinal));
    }

    [TestMethod]
    public void Error_UnwritablePath_DoesNotThrow()
    {
        Log.FilePath = "Z:\\\0bad\\log.txt";

        Log.Error("ignored");
    }
}
