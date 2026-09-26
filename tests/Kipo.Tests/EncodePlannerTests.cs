using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class EncodePlannerTests
{
    private const long MB = 1_048_576;

    private static VideoInfo Info(double seconds, int width = 1920, int height = 1080, double fps = 30, bool audio = true) =>
        new(TimeSpan.FromSeconds(seconds), width, height, fps, audio);

    [TestMethod]
    public void Plan_SourceUnderTarget_Skips()
    {
        Assert.AreEqual(PlanKind.Skip, EncodePlanner.Plan(Info(60), 5 * MB, 10 * MB).Kind);
    }

    [TestMethod]
    public void Plan_SourceEqualsTarget_Skips()
    {
        Assert.AreEqual(PlanKind.Skip, EncodePlanner.Plan(Info(60), 10 * MB, 10 * MB).Kind);
    }

    [TestMethod]
    public void Plan_ZeroTarget_FailsInvalidTarget()
    {
        Assert.AreEqual(PlanErrors.InvalidTarget, EncodePlanner.Plan(Info(60), 5 * MB, 0).Error);
    }

    [TestMethod]
    public void Plan_ZeroDuration_FailsCantRead()
    {
        Assert.AreEqual(PlanErrors.CantRead, EncodePlanner.Plan(Info(0), 50 * MB, 10 * MB).Error);
    }

    [TestMethod]
    public void Plan_ZeroWidth_FailsCantRead()
    {
        Assert.AreEqual(PlanErrors.CantRead, EncodePlanner.Plan(Info(60, width: 0), 50 * MB, 10 * MB).Error);
    }

    [TestMethod]
    public void Plan_1080p60sTo10MB_DownscalesTo720p()
    {
        // Budget 79,691,776 bits / 60 s = 1,328,196 bps, minus 128k audio = 1,200,196
        var plan = EncodePlanner.Plan(Info(60), 100 * MB, 10 * MB);

        Assert.AreEqual(PlanKind.Encode, plan.Kind);
        Assert.AreEqual((1280, 720), (plan.Width, plan.Height));
        Assert.AreEqual(1_200_196u, plan.VideoBitrate);
        Assert.AreEqual(128_000u, plan.AudioBitrate);
    }

    [TestMethod]
    public void Plan_PlentyOfBudget_KeepsSourceSize()
    {
        var plan = EncodePlanner.Plan(Info(10), 100 * MB, 50 * MB);

        Assert.AreEqual((1920, 1080), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_TightBudget_DropsAudioTo64k()
    {
        // 381,300 bps total: 253,300 at 128k is under the 360p tier, 317,300 at 64k clears it
        var plan = EncodePlanner.Plan(Info(209), 100 * MB, 10 * MB);

        Assert.AreEqual(PlanKind.Encode, plan.Kind);
        Assert.AreEqual(64_000u, plan.AudioBitrate);
        Assert.AreEqual((640, 360), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_Under360pTierButAboveMinimum_EncodesAt360p()
    {
        var plan = EncodePlanner.Plan(Info(372), 100 * MB, 10 * MB);

        Assert.AreEqual(PlanKind.Encode, plan.Kind);
        Assert.AreEqual((640, 360), (plan.Width, plan.Height));
        Assert.AreEqual(64_000u, plan.AudioBitrate);
    }

    [TestMethod]
    public void Plan_HourLongTo8MB_FailsTooSmall()
    {
        var plan = EncodePlanner.Plan(Info(3600), 500 * MB, 8 * MB);

        Assert.AreEqual(PlanKind.Fail, plan.Kind);
        Assert.AreEqual(PlanErrors.TooSmall, plan.Error);
    }

    [TestMethod]
    public void Plan_NoAudio_UsesWholeBudgetForVideo()
    {
        var plan = EncodePlanner.Plan(Info(60, audio: false), 100 * MB, 10 * MB);

        Assert.AreEqual(0u, plan.AudioBitrate);
        Assert.AreEqual(1_328_196u, plan.VideoBitrate);
        Assert.AreEqual((1280, 720), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_Portrait_ScalesShortSide()
    {
        var plan = EncodePlanner.Plan(Info(60, width: 1080, height: 1920), 100 * MB, 10 * MB);

        Assert.AreEqual((720, 1280), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_UnknownFrameRate_Assumes30()
    {
        var plan = EncodePlanner.Plan(Info(60, fps: 0), 100 * MB, 10 * MB);

        Assert.AreEqual((1280, 720), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_OddSourceSize_RoundsToEven()
    {
        var plan = EncodePlanner.Plan(Info(10, width: 1919, height: 1079), 100 * MB, 90 * MB);

        Assert.AreEqual((1918, 1078), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_SmallSource_NeverUpscales()
    {
        var plan = EncodePlanner.Plan(Info(10, width: 320, height: 240), 100 * MB, 50 * MB);

        Assert.AreEqual((320, 240), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void ScaleToShortSide_1366x768To480_KeepsAspectAndEven()
    {
        // 1366 × 0.625 = 853.75, rounds to 854 (already even)
        Assert.AreEqual((854, 480), EncodePlanner.ScaleToShortSide(1366, 768, 480));
    }

    [TestMethod]
    public void ScaleToShortSide_1080pTo720_Is1280x720()
    {
        Assert.AreEqual((1280, 720), EncodePlanner.ScaleToShortSide(1920, 1080, 720));
    }

    [TestMethod]
    public void ScaleToShortSide_AlreadySmaller_ReturnsEvenSource()
    {
        Assert.AreEqual((640, 358), EncodePlanner.ScaleToShortSide(641, 359, 480));
    }

    [TestMethod]
    public void RetryBitrate_ScalesByTargetOverActual()
    {
        Assert.AreEqual(833_333u, EncodePlanner.RetryBitrate(1_000_000, 10 * MB, 12 * MB));
    }

    [TestMethod]
    public void RetryBitrate_NeverZero()
    {
        Assert.AreEqual(1u, EncodePlanner.RetryBitrate(1, 1, long.MaxValue));
    }
}
