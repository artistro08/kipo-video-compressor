using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class PathTextTests
{
    [TestMethod]
    public void Parse_Null_ReturnsEmpty()
    {
        Assert.AreEqual(0, PathText.Parse(null).Count);
    }

    [TestMethod]
    public void Parse_Whitespace_ReturnsEmpty()
    {
        Assert.AreEqual(0, PathText.Parse("  \r\n \n").Count);
    }

    [TestMethod]
    public void Parse_SinglePath_ReturnsIt()
    {
        CollectionAssert.AreEqual(new[] { @"C:\a.mp4" }, PathText.Parse(@"C:\a.mp4").ToArray());
    }

    [TestMethod]
    public void Parse_QuotedPath_StripsQuotes()
    {
        CollectionAssert.AreEqual(new[] { @"C:\my videos\a.mp4" }, PathText.Parse("\"C:\\my videos\\a.mp4\"").ToArray());
    }

    [TestMethod]
    public void Parse_MultipleLinesAndBlanks_ReturnsEachPath()
    {
        var text = "C:\\a.mp4\r\n\r\n  \"C:\\b.mov\"  \nC:\\c.mkv\n";

        CollectionAssert.AreEqual(new[] { @"C:\a.mp4", @"C:\b.mov", @"C:\c.mkv" }, PathText.Parse(text).ToArray());
    }

    [TestMethod]
    public void Parse_OnlyQuotes_Skipped()
    {
        Assert.AreEqual(0, PathText.Parse("\"\"").Count);
    }
}
