using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class LaunchRequestTests
{
    [TestMethod]
    public void Parse_NoArgs_IsMain() => Assert.AreEqual(LaunchAction.Main, LaunchRequest.Parse([]).Action);

    [TestMethod]
    [DataRow("8", 8)]
    [DataRow("10", 10)]
    [DataRow("25", 25)]
    [DataRow("50", 50)]
    [DataRow("100", 100)]
    public void Parse_Preset_IsCompressWithSize(string size, int mb)
    {
        var request = LaunchRequest.Parse(["--compress", size, @"C:\v\a.mp4"]);

        Assert.AreEqual(LaunchAction.Compress, request.Action);
        Assert.AreEqual(mb, request.SizeMb);
        Assert.IsFalse(request.Custom);
        CollectionAssert.AreEqual(new[] { @"C:\v\a.mp4" }, request.Paths.ToArray());
    }

    [TestMethod]
    public void Parse_Custom_HasNoSize()
    {
        var request = LaunchRequest.Parse(["--compress", "custom", @"C:\v\a.mp4", @"C:\v\b.mov"]);

        Assert.AreEqual(LaunchAction.Compress, request.Action);
        Assert.IsTrue(request.Custom);
        Assert.IsNull(request.SizeMb);
        Assert.AreEqual(2, request.Paths.Count);
    }

    [TestMethod]
    [DataRow("7")]
    [DataRow("big")]
    [DataRow("")]
    public void Parse_UnknownSize_IsMain(string size) =>
        Assert.AreEqual(LaunchAction.Main, LaunchRequest.Parse(["--compress", size, @"C:\v\a.mp4"]).Action);

    [TestMethod]
    public void Parse_NoPaths_IsMain() => Assert.AreEqual(LaunchAction.Main, LaunchRequest.Parse(["--compress", "25"]).Action);

    [TestMethod]
    public void Parse_UnknownFlag_IsMain() => Assert.AreEqual(LaunchAction.Main, LaunchRequest.Parse(["--wat"]).Action);

    [TestMethod]
    public void Parse_UnregisterShell() => Assert.AreEqual(LaunchAction.UnregisterShell, LaunchRequest.Parse(["--unregister-shell"]).Action);

    [TestMethod]
    public void Parse_KeepsNetworkPathsForValidatorToReject()
    {
        // Validation happens when queued (Failed: unsupported path), not here
        Assert.AreEqual(@"\\server\s\a.mp4", LaunchRequest.Parse(["--compress", "25", @"\\server\s\a.mp4"]).Paths[0]);
    }

    [TestMethod]
    public void Split_QuotesAndSpaces()
    {
        CollectionAssert.AreEqual(
            new[] { "--compress", "25", @"C:\my videos\a b.mp4", @"C:\c.mkv" },
            CommandLine.Split("--compress 25 \"C:\\my videos\\a b.mp4\" C:\\c.mkv").ToArray());
    }

    [TestMethod]
    public void Split_Unicode() =>
        Assert.AreEqual(@"C:\vidéos\日本.mp4", CommandLine.Split("\"C:\\vidéos\\日本.mp4\"")[0]);

    [TestMethod]
    public void Split_EmptyAndWhitespace_IsEmpty() => Assert.AreEqual(0, CommandLine.Split("   ").Count);

    [TestMethod]
    public void ParseCommandLine_DropsExeToken()
    {
        var request = LaunchRequest.ParseCommandLine("\"C:\\Program Files\\Kipo\\Kipo.exe\" --compress 8 \"C:\\v\\a.mp4\"");

        Assert.AreEqual(8, request.SizeMb);
        Assert.AreEqual(@"C:\v\a.mp4", request.Paths[0]);
    }

    [TestMethod]
    public void ParseCommandLine_WithoutExeToken()
    {
        Assert.AreEqual(50, LaunchRequest.ParseCommandLine("--compress 50 \"C:\\v\\a.mp4\"").SizeMb);
    }

    [TestMethod]
    public void ParseCommandLine_Null_IsMain() => Assert.AreEqual(LaunchAction.Main, LaunchRequest.ParseCommandLine(null).Action);
}
