using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class CompressQueueTests
{
    private static string _dir = "";

    [ClassInitialize]
    public static void Setup(TestContext _)
    {
        _dir = Directory.CreateTempSubdirectory("kipo-queue-").FullName;

        foreach (var name in new[] { "a.mp4", "b.mp4", "c.mp4" })
        {
            File.WriteAllBytes(Path.Combine(_dir, name), [0]);
        }
    }

    [ClassCleanup]
    public static void Cleanup() => Directory.Delete(_dir, true);

    private static string P(string name) => Path.Combine(_dir, name);

    private static Task<CompressResult> Done(QueueItem item) =>
        Task.FromResult(new CompressResult(ItemStatus.Done, item.Path + ".out"));

    [TestMethod]
    public async Task AddPaths_RunsInOrder()
    {
        var order = new List<string>();
        var queue = new CompressQueue((item, _, _) => { order.Add(item.FileName); return Done(item); });

        queue.AddPaths([P("a.mp4"), P("b.mp4"), P("c.mp4")], 100);
        await queue.WhenIdle();

        CollectionAssert.AreEqual(new[] { "a.mp4", "b.mp4", "c.mp4" }, order);
        Assert.IsTrue(queue.Items.All(i => i.Status == ItemStatus.Done));
    }

    [TestMethod]
    public async Task AddPaths_InvalidPath_AddsFailedRowWithoutRunning()
    {
        var ran   = 0;
        var queue = new CompressQueue((item, _, _) => { ran++; return Done(item); });

        queue.AddPaths([P("missing.mp4"), P("a.mp4")], 100);
        await queue.WhenIdle();

        Assert.AreEqual(1, ran);
        Assert.AreEqual(ItemStatus.Failed, queue.Items[0].Status);
        Assert.AreEqual("Failed: not found", queue.Items[0].StatusText);
    }

    [TestMethod]
    public async Task AddPaths_WhileRunning_JoinsSameRun()
    {
        var gate  = new TaskCompletionSource();
        var order = new List<string>();
        var queue = new CompressQueue(async (item, _, _) =>
        {
            order.Add(item.FileName);
            if (item.FileName == "a.mp4") await gate.Task;
            return new CompressResult(ItemStatus.Done);
        });

        queue.AddPaths([P("a.mp4")], 100);
        queue.AddPaths([P("b.mp4")], 100);
        gate.SetResult();
        await queue.WhenIdle();

        CollectionAssert.AreEqual(new[] { "a.mp4", "b.mp4" }, order);
    }

    [TestMethod]
    public async Task Cancel_WaitingItem_IsSkipped()
    {
        var gate  = new TaskCompletionSource();
        var queue = new CompressQueue(async (item, _, _) => { await gate.Task; return new CompressResult(ItemStatus.Done); });

        queue.AddPaths([P("a.mp4"), P("b.mp4")], 100);
        queue.Items[1].Cancel();
        gate.SetResult();
        await queue.WhenIdle();

        Assert.AreEqual(ItemStatus.Done, queue.Items[0].Status);
        Assert.AreEqual(ItemStatus.Canceled, queue.Items[1].Status);
    }

    [TestMethod]
    public async Task Cancel_RunningItem_MarksCanceledAndContinues()
    {
        var queue = new CompressQueue(async (item, _, token) =>
        {
            if (item.FileName == "a.mp4") await Task.Delay(Timeout.Infinite, token);
            return new CompressResult(ItemStatus.Done);
        });

        queue.AddPaths([P("a.mp4"), P("b.mp4")], 100);
        queue.Items[0].Cancel();
        await queue.WhenIdle();

        Assert.AreEqual(ItemStatus.Canceled, queue.Items[0].Status);
        Assert.AreEqual(ItemStatus.Done, queue.Items[1].Status);
    }

    [TestMethod]
    public async Task CancelAll_CancelsRunningAndWaiting()
    {
        var queue = new CompressQueue(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new CompressResult(ItemStatus.Done);
        });

        queue.AddPaths([P("a.mp4"), P("b.mp4"), P("c.mp4")], 100);
        queue.CancelAll();
        await queue.WhenIdle();

        Assert.IsTrue(queue.Items.All(i => i.Status == ItemStatus.Canceled));
    }

    [TestMethod]
    public async Task WorkerThrows_MarksFailedAndContinues()
    {
        var queue = new CompressQueue((item, _, _) =>
            item.FileName == "a.mp4" ? throw new IOException("disk gone") : Done(item));

        queue.AddPaths([P("a.mp4"), P("b.mp4")], 100);
        await queue.WhenIdle();

        Assert.AreEqual("Failed: disk gone", queue.Items[0].StatusText);
        Assert.AreEqual(ItemStatus.Done, queue.Items[1].Status);
    }

    [TestMethod]
    public async Task Progress_UpdatesItem()
    {
        var queue = new CompressQueue((item, progress, _) =>
        {
            progress.Report(42);
            return Task.FromResult(new CompressResult(ItemStatus.Done));
        });
        var seen = new TaskCompletionSource<double>();

        queue.AddPaths([P("a.mp4")], 100);
        queue.Items[0].PropertyChanged += (s, e) => { if (e.PropertyName == nameof(QueueItem.Progress) && ((QueueItem)s!).Progress > 0) seen.TrySetResult(((QueueItem)s!).Progress); };
        await queue.WhenIdle();

        // Progress<T> posts on the thread pool, so the value can arrive after the run ends
        Assert.AreEqual(42, await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task BatchCompleted_FiresOncePerRunWithAllItems()
    {
        var batches = new List<IReadOnlyList<QueueItem>>();
        var queue   = new CompressQueue((item, _, _) => Done(item));

        queue.BatchCompleted += (_, batch) => batches.Add(batch);
        queue.AddPaths([P("a.mp4"), P("b.mp4")], 100);
        await queue.WhenIdle();
        queue.AddPaths([P("c.mp4")], 100);
        await queue.WhenIdle();

        Assert.AreEqual(2, batches.Count);
        Assert.AreEqual(2, batches[0].Count);
        Assert.AreEqual(1, batches[1].Count);
    }

    [TestMethod]
    public async Task AddPaths_SameFileTwice_RunsBoth()
    {
        var ran   = 0;
        var queue = new CompressQueue((item, _, _) => { ran++; return Done(item); });

        queue.AddPaths([P("a.mp4"), P("a.mp4")], 100);
        await queue.WhenIdle();

        Assert.AreEqual(2, ran);
    }

    [TestMethod]
    public void StatusText_AllStatuses()
    {
        using var item = new QueueItem(P("a.mp4"), 100);

        Assert.AreEqual("Waiting", item.StatusText);

        item.Status = ItemStatus.Compressing;
        item.Progress = 41.6;
        Assert.AreEqual("Compressing 42%", item.StatusText);

        item.Status = ItemStatus.Done;
        Assert.AreEqual("Done", item.StatusText);

        item.Detail = "1.5";
        item.Status = ItemStatus.DoneOverTarget;
        Assert.AreEqual("Done (over target by 1.5 MB)", item.StatusText);

        item.Status = ItemStatus.Skipped;
        Assert.AreEqual("Already under target", item.StatusText);

        item.Detail = "not a video";
        item.Status = ItemStatus.Failed;
        Assert.AreEqual("Failed: not a video", item.StatusText);

        item.Status = ItemStatus.Canceled;
        Assert.AreEqual("Canceled", item.StatusText);
    }

    [TestMethod]
    [DataRow(ItemStatus.Done, true)]
    [DataRow(ItemStatus.DoneOverTarget, true)]
    [DataRow(ItemStatus.Skipped, true)]
    [DataRow(ItemStatus.Waiting, false)]
    [DataRow(ItemStatus.Compressing, false)]
    [DataRow(ItemStatus.Failed, false)]
    [DataRow(ItemStatus.Canceled, false)]
    public void IsDone_OnlyForFinishedFiles(ItemStatus status, bool expected)
    {
        using var item = new QueueItem(P("a.mp4"), 100) { Status = status };

        Assert.AreEqual(expected, item.IsDone);
    }

    [TestMethod]
    public void IsDone_RaisesChangeWhenStatusChanges()
    {
        using var item = new QueueItem(P("a.mp4"), 100);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Status = ItemStatus.Done;

        CollectionAssert.Contains(changed, nameof(QueueItem.IsDone));
    }

    [TestMethod]
    public void ShouldCopy_SingleDone_ReturnsOutput()
    {
        using var item = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.Done, OutputPath = @"C:\x-compressed.mp4" };

        Assert.AreEqual(@"C:\x-compressed.mp4", CompressQueue.ShouldCopyToClipboard([item], enabled: true));
    }

    [TestMethod]
    public void ShouldCopy_SingleOverTarget_ReturnsOutput()
    {
        using var item = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.DoneOverTarget, OutputPath = @"C:\x-compressed.mp4" };

        Assert.AreEqual(@"C:\x-compressed.mp4", CompressQueue.ShouldCopyToClipboard([item], enabled: true));
    }

    [TestMethod]
    public void ShouldCopy_Disabled_ReturnsNull()
    {
        using var item = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.Done, OutputPath = @"C:\x.mp4" };

        Assert.IsNull(CompressQueue.ShouldCopyToClipboard([item], enabled: false));
    }

    [TestMethod]
    public void ShouldCopy_TwoItems_ReturnsNull()
    {
        using var a = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.Done, OutputPath = @"C:\a.mp4" };
        using var b = new QueueItem(P("b.mp4"), 100) { Status = ItemStatus.Done, OutputPath = @"C:\b.mp4" };

        Assert.IsNull(CompressQueue.ShouldCopyToClipboard([a, b], enabled: true));
    }

    [TestMethod]
    public void ShouldCopy_SingleFailed_ReturnsNull()
    {
        using var item = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.Failed };

        Assert.IsNull(CompressQueue.ShouldCopyToClipboard([item], enabled: true));
    }
}
