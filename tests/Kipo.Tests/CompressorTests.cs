using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
[TestCategory("Video")]
[DoNotParallelize]
public sealed class CompressorTests
{
    private const long MB = 1_048_576;
    private string _dir = "";

    [TestInitialize]
    public void Setup() => _dir = TestVideos.NewTempDir();

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    private static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private void AssertNoPartFiles() => Assert.AreEqual(0, Directory.GetFiles(_dir, "*.part").Length);

    [TestMethod]
    [Timeout(600_000)]
    public async Task Compress_MidTo8MB_FitsAndLeavesSourceAlone()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Mid);
        var before = Hash(source);

        var result = await Compressor.CompressAsync(source, 8 * MB, null, CancellationToken.None);

        Assert.AreEqual(ItemStatus.Done, result.Status, result.Detail);
        Assert.IsTrue(new FileInfo(result.OutputPath!).Length <= 8 * MB);
        Assert.AreEqual(Path.Combine(_dir, Path.GetFileNameWithoutExtension(source) + "-compressed.mp4"), result.OutputPath);
        Assert.AreEqual(before, Hash(source));
        AssertNoPartFiles();
    }

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task Compress_LargeTo25MB_Fits()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Large);

        var result = await Compressor.CompressAsync(source, 25 * MB, null, CancellationToken.None);

        Assert.AreEqual(ItemStatus.Done, result.Status, result.Detail);
        Assert.IsTrue(new FileInfo(result.OutputPath!).Length <= 25 * MB);
        AssertNoPartFiles();
    }

    [TestMethod]
    public async Task Compress_UnderTarget_Skips()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);

        var result = await Compressor.CompressAsync(source, 10 * MB, null, CancellationToken.None);

        Assert.AreEqual(ItemStatus.Skipped, result.Status);
        Assert.AreEqual(1, Directory.GetFiles(_dir).Length);
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task Compress_ReportsProgress()
    {
        var source   = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        var reported = new List<double>();
        var progress = new SyncProgress(reported.Add);

        var result = await Compressor.CompressAsync(source, MB, progress, CancellationToken.None);

        Assert.AreEqual(ItemStatus.Done, result.Status, result.Detail);
        Assert.IsTrue(reported.Count > 0);
        Assert.IsTrue(reported.All(p => p is >= 0 and <= 100));
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task Compress_CancelMidEncode_LeavesNoFiles()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Large);
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p > 5) cancel.Cancel(); });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Compressor.CompressAsync(source, 25 * MB, progress, cancel.Token));

        Assert.AreEqual(1, Directory.GetFiles(_dir).Length);
    }

    [TestMethod]
    public async Task Compress_MissingSource_FailsNotFound()
    {
        var result = await Compressor.CompressAsync(Path.Combine(_dir, "gone.mp4"), MB, null, CancellationToken.None);

        Assert.AreEqual(ItemStatus.Failed, result.Status);
        Assert.AreEqual(InputErrors.NotFound, result.Detail);
        AssertNoPartFiles();
    }

    [TestMethod]
    public async Task Compress_NotReallyAVideo_Fails()
    {
        var source = Path.Combine(_dir, "fake.mp4");
        File.WriteAllText(source, new string('x', 2_000_000));

        var result = await Compressor.CompressAsync(source, MB, null, CancellationToken.None);

        Assert.AreEqual(ItemStatus.Failed, result.Status);
        AssertNoPartFiles();
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task Queue_SameFileTwice_SecondGetsNumberedName()
    {
        var source     = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        var queue      = new CompressQueue((item, progress, token) => Compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));

        queue.AddPaths([source, source], MB);
        await queue.WhenIdle();

        var stem = Path.GetFileNameWithoutExtension(source);
        Assert.AreEqual(Path.Combine(_dir, stem + "-compressed.mp4"), queue.Items[0].OutputPath);
        Assert.AreEqual(Path.Combine(_dir, stem + "-compressed (2).mp4"), queue.Items[1].OutputPath);
    }

    [TestMethod]
    [Timeout(900_000)]
    public async Task Queue_Batch_AllOutputsNextToSources()
    {
        var sources    = TestVideos.Batch.Select(name => TestVideos.CopyToTemp(_dir, name)).ToArray();
        var queue      = new CompressQueue((item, progress, token) => Compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));

        queue.AddPaths(sources, 524_288);
        await queue.WhenIdle();

        foreach (var item in queue.Items)
        {
            Assert.AreEqual(ItemStatus.Done, item.Status, item.Detail);
            Assert.AreEqual(_dir, Path.GetDirectoryName(item.OutputPath));
        }

        AssertNoPartFiles();
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task Queue_SourceDeletedAfterQueued_FailsNotFoundAndContinues()
    {
        var first      = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);
        var second     = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        var queue      = new CompressQueue((item, progress, token) => Compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));

        queue.AddPaths([first, second], MB);
        File.Delete(second);
        await queue.WhenIdle();

        Assert.AreEqual("Failed: not found", queue.Items[1].StatusText);
        AssertNoPartFiles();
    }

    [TestMethod]
    public async Task ReadInfo_ScreenRecording_HasSizeAndDuration()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Small);

        var info = await Compressor.ReadInfoAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(source));

        Assert.IsNotNull(info);
        Assert.IsTrue(info.Width > 0 && info.Height > 0);
        Assert.IsTrue(info.Duration > TimeSpan.Zero);
    }

    // Progress<T> posts to the thread pool; this reports inline so assertions see every value
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
