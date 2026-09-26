using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class FileActionsTests
{
    [TestMethod]
    public void ShowFile_OpensWithDefaultApp()
    {
        var start = FileActions.ShowFile(@"C:\v\clip-compressed.mp4");

        Assert.AreEqual(@"C:\v\clip-compressed.mp4", start.FileName);
        Assert.IsTrue(start.UseShellExecute);
    }

    [TestMethod]
    public void OpenInFolder_SelectsFileInExplorer()
    {
        var start = FileActions.OpenInFolder(@"C:\my videos\clip-compressed.mp4");

        Assert.AreEqual("explorer.exe", start.FileName);
        Assert.AreEqual("/select,\"C:\\my videos\\clip-compressed.mp4\"", start.Arguments);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("clip.mp4")]
    [DataRow(@"C:\v\bad"".mp4")]
    public void Actions_RejectUnsafePaths(string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => FileActions.ShowFile(path));
        Assert.ThrowsExactly<ArgumentException>(() => FileActions.OpenInFolder(path));
    }
}
