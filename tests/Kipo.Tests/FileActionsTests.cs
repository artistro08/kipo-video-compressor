using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class FileActionsTests
{
    [TestMethod]
    public void OpenFile_OpensWithDefaultApp()
    {
        var start = FileActions.OpenFile(@"C:\v\clip-compressed.mp4");

        Assert.AreEqual(@"C:\v\clip-compressed.mp4", start.FileName);
        Assert.IsTrue(start.UseShellExecute);
    }

    [TestMethod]
    public void OpenInFolder_SelectsFileInExplorer()
    {
        var start = FileActions.OpenInFolder(@"C:\my videos\clip-compressed.mp4");

        // Full path, so an explorer.exe planted in the working folder can't be picked up instead
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), start.FileName);
        Assert.AreEqual("/select,\"C:\\my videos\\clip-compressed.mp4\"", start.Arguments);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("clip.mp4")]
    [DataRow(@"C:\v\bad"".mp4")]
    public void Actions_RejectUnsafePaths(string path)
    {
        Assert.ThrowsExactly<ArgumentException>(() => FileActions.OpenFile(path));
        Assert.ThrowsExactly<ArgumentException>(() => FileActions.OpenInFolder(path));
    }
}
