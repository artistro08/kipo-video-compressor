using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class PathValidatorTests
{
    private static string _dir = "";

    [ClassInitialize]
    public static void Setup(TestContext _)
    {
        _dir = Directory.CreateTempSubdirectory("kipo-validator-").FullName;
        File.WriteAllBytes(Path.Combine(_dir, "clip.mp4"), [0]);
        File.WriteAllBytes(Path.Combine(_dir, "CLIP2.MOV"), [0]);
        File.WriteAllBytes(Path.Combine(_dir, "notes.txt"), [0]);
    }

    [ClassCleanup]
    public static void Cleanup() => Directory.Delete(_dir, true);

    [TestMethod]
    public void Validate_ExistingVideo_ReturnsNull()
    {
        Assert.IsNull(PathValidator.Validate(Path.Combine(_dir, "clip.mp4")));
    }

    [TestMethod]
    public void Validate_UppercaseExtension_ReturnsNull()
    {
        Assert.IsNull(PathValidator.Validate(Path.Combine(_dir, "CLIP2.MOV")));
    }

    [TestMethod]
    public void Validate_MissingVideo_ReturnsNotFound()
    {
        Assert.AreEqual(InputErrors.NotFound, PathValidator.Validate(Path.Combine(_dir, "gone.mp4")));
    }

    [TestMethod]
    public void Validate_NonVideo_ReturnsNotAVideo()
    {
        Assert.AreEqual(InputErrors.NotAVideo, PathValidator.Validate(Path.Combine(_dir, "notes.txt")));
    }

    [TestMethod]
    public void Validate_Folder_ReturnsNotAVideo()
    {
        Assert.AreEqual(InputErrors.NotAVideo, PathValidator.Validate(_dir));
    }

    [TestMethod]
    [DataRow(@"clip.mp4")]
    [DataRow(@"..\clip.mp4")]
    [DataRow(@"\clip.mp4")]
    [DataRow(@"C:clip.mp4")]
    [DataRow(@"\\server\share\clip.mp4")]
    [DataRow(@"//server/share/clip.mp4")]
    [DataRow(@"\\?\C:\clip.mp4")]
    [DataRow(@"\\.\C:\clip.mp4")]
    [DataRow(@"https://example.com/clip.mp4")]
    [DataRow(@"file:///C:/clip.mp4")]
    [DataRow(@"C:\clip.mp4:hidden.mp4")]
    [DataRow("C:\\cl\0ip.mp4")]
    [DataRow("")]
    public void Validate_UnsupportedPath_ReturnsUnsupported(string path)
    {
        Assert.AreEqual(InputErrors.UnsupportedPath, PathValidator.Validate(path));
    }
}
