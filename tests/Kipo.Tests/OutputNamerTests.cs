using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class OutputNamerTests
{
    [TestMethod]
    public void GetOutputPath_NoCollision_AddsCompressedSuffix()
    {
        Assert.AreEqual(@"C:\v\clip-compressed.mp4", OutputNamer.GetOutputPath(@"C:\v\clip.mov", _ => false));
    }

    [TestMethod]
    public void GetOutputPath_OneCollision_Adds2()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\v\clip-compressed.mp4" };

        Assert.AreEqual(@"C:\v\clip-compressed (2).mp4", OutputNamer.GetOutputPath(@"C:\v\clip.mp4", taken.Contains));
    }

    [TestMethod]
    public void GetOutputPath_TwoCollisions_Adds3()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\v\clip-compressed.mp4",
            @"C:\v\clip-compressed (2).mp4",
        };

        Assert.AreEqual(@"C:\v\clip-compressed (3).mp4", OutputNamer.GetOutputPath(@"C:\v\clip.mp4", taken.Contains));
    }

    [TestMethod]
    public void GetOutputPath_NameWithDots_KeepsInnerDots()
    {
        Assert.AreEqual(@"C:\v\a.b.c-compressed.mp4", OutputNamer.GetOutputPath(@"C:\v\a.b.c.mkv", _ => false));
    }

    [TestMethod]
    public void GetPartPath_UsesPartExtension()
    {
        Assert.AreEqual(@"C:\v\clip-compressed.part", OutputNamer.GetPartPath(@"C:\v\clip.mp4"));
    }
}
