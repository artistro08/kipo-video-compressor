using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class RequestBatcherTests
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly List<List<string>> _dialogs = [];

    private RequestBatcher<List<string>> Batcher() => new(
        () => _now,
        TimeSpan.FromMilliseconds(500),
        request =>
        {
            var dialog = new List<string>(request.Paths);
            _dialogs.Add(dialog);
            return dialog;
        },
        (dialog, paths) => dialog.AddRange(paths));

    private static LaunchRequest Req(string size, string path) => LaunchRequest.Parse(["--compress", size, path]);

    [TestMethod]
    public void WithinWindow_SameSize_OneDialog()
    {
        var batcher = Batcher();

        batcher.Submit(Req("25", @"C:\a.mp4"));
        _now += TimeSpan.FromMilliseconds(200);
        batcher.Submit(Req("25", @"C:\b.mp4"));
        _now += TimeSpan.FromMilliseconds(400);   // 400 after the last one: still joins
        batcher.Submit(Req("25", @"C:\c.mp4"));

        Assert.AreEqual(1, _dialogs.Count);
        CollectionAssert.AreEqual(new[] { @"C:\a.mp4", @"C:\b.mp4", @"C:\c.mp4" }, _dialogs[0]);
    }

    [TestMethod]
    public void AfterWindow_NewDialog()
    {
        var batcher = Batcher();

        batcher.Submit(Req("25", @"C:\a.mp4"));
        _now += TimeSpan.FromMilliseconds(600);
        batcher.Submit(Req("25", @"C:\b.mp4"));

        Assert.AreEqual(2, _dialogs.Count);
    }

    [TestMethod]
    public void DifferentSize_NewDialog()
    {
        var batcher = Batcher();

        batcher.Submit(Req("25", @"C:\a.mp4"));
        batcher.Submit(Req("custom", @"C:\b.mp4"));
        batcher.Submit(Req("8", @"C:\c.mp4"));

        Assert.AreEqual(3, _dialogs.Count);
    }

    [TestMethod]
    public void CustomRequests_Group()
    {
        var batcher = Batcher();

        batcher.Submit(Req("custom", @"C:\a.mp4"));
        batcher.Submit(Req("custom", @"C:\b.mp4"));

        Assert.AreEqual(1, _dialogs.Count);
        Assert.AreEqual(2, _dialogs[0].Count);
    }

    [TestMethod]
    public void ClosedDialog_IsForgotten_NextRequestOpensNew()
    {
        var batcher = Batcher();

        batcher.Submit(Req("25", @"C:\a.mp4"));
        batcher.Forget(_dialogs[0]);   // user closed it
        batcher.Submit(Req("25", @"C:\b.mp4"));

        Assert.AreEqual(2, _dialogs.Count);
        Assert.AreEqual(1, _dialogs[0].Count, "File went into the closed dialog");
    }

    [TestMethod]
    public void ForgettingAnOlderDialog_KeepsTheCurrentOne()
    {
        var batcher = Batcher();

        batcher.Submit(Req("25", @"C:\a.mp4"));
        batcher.Forget([]);
        batcher.Submit(Req("25", @"C:\b.mp4"));

        Assert.AreEqual(1, _dialogs.Count);
    }

    [TestMethod]
    public void NonCompressRequest_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Batcher().Submit(LaunchRequest.Parse([])));
    }
}
