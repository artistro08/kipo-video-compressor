using Kipo.Core;
using Kipo.Shell;

namespace Kipo.Tests;

[TestClass]
public sealed class ShellMenuTests
{
    [TestMethod]
    public void Items_MatchAppPresets_ThenCustom()
    {
        var expected = AppSettings.Presets.Select(mb => $"{mb} MB").Append("Custom…").ToArray();

        CollectionAssert.AreEqual(expected, ShellMenu.Items.Select(i => i.Title).ToArray());
        Assert.IsTrue(ShellMenu.Items[^1].SeparatorBefore);
        Assert.IsTrue(ShellMenu.Items.Take(ShellMenu.Items.Count - 1).All(i => !i.SeparatorBefore));
    }

    [TestMethod]
    public void Items_SizesParseAsLaunchRequests()
    {
        foreach (var item in ShellMenu.Items)
        {
            Assert.AreEqual(LaunchAction.Compress, LaunchRequest.Parse(["--compress", item.Size, @"C:\a.mp4"]).Action, item.Title);
        }
    }

    [TestMethod]
    public void CommandLines_QuotesEveryPath()
    {
        var lines = ShellMenu.CommandLines("25", [@"C:\my videos\a b.mp4", @"C:\c.mkv"]);

        Assert.AreEqual(1, lines.Count);
        Assert.AreEqual("--compress 25 \"C:\\my videos\\a b.mp4\" \"C:\\c.mkv\"", lines[0]);
    }

    [TestMethod]
    public void CommandLines_SplitLongSelections()
    {
        var paths = Enumerable.Range(0, 2000).Select(i => $@"C:\videos\clip number {i:0000}.mp4").ToList();

        var lines = ShellMenu.CommandLines("custom", paths);

        Assert.IsTrue(lines.Count > 1);
        Assert.IsTrue(lines.All(l => l.Length <= 30_000 && l.StartsWith("--compress custom ", StringComparison.Ordinal)));
        CollectionAssert.AreEqual(paths, lines.SelectMany(l => LaunchRequest.ParseCommandLine(l).Paths).ToList());
    }

    [TestMethod]
    public void CommandLines_NoPaths_IsEmpty() => Assert.AreEqual(0, ShellMenu.CommandLines("25", []).Count);

    [TestMethod]
    public void CommandLines_RoundTripThroughLaunchParser()
    {
        var line = ShellMenu.CommandLines("custom", [@"C:\vidéos\日本 1.mp4"])[0];

        Assert.AreEqual(@"C:\vidéos\日本 1.mp4", LaunchRequest.ParseCommandLine(line).Paths[0]);
    }
}
