using Kipo.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Kipo.Tests;

/// <summary>
/// Paste, drop and picker input against real Windows data objects.
/// </summary>
/// <remarks>
/// Clipboard data this process owns is served back through COM on the STA thread, so
/// that thread must pump messages while it waits. A blocking wait on an STA thread
/// pumps; an await on MSTest's STA context doesn't and can deadlock. So reads here
/// block with <see cref="Pump{T}"/> instead of awaiting.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ClipboardInputTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup() => _dir = TestVideos.NewTempDir();

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    // Blocking wait; on an STA thread the CLR pumps COM messages while waiting
    private static T Pump<T>(Task<T> task) => task.GetAwaiter().GetResult();

    // Sets the clipboard and waits until Windows reports the new formats; the previous
    // content can linger for a moment after Flush
    private static void SetClipboard(DataPackage package, string expectedFormat, string goneFormat)
    {
        Clipboard.SetContent(package);
        Clipboard.Flush();
        WaitForClipboard(expectedFormat, goneFormat);
    }

    private static void WaitForClipboard(string expectedFormat, string goneFormat)
    {
        for (var tries = 0; tries < 50; tries++)
        {
            var view = Clipboard.GetContent();

            if (view.Contains(expectedFormat) && !view.Contains(goneFormat))
            {
                return;
            }

            Thread.Sleep(100);
        }

        Assert.Fail($"Clipboard never showed {expectedFormat} without {goneFormat}");
    }

    private StorageFile[] SampleFiles(params string[] names) =>
        names.Select(name => Pump(StorageFile.GetFileFromPathAsync(TestVideos.CopyToTemp(_dir, name)).AsTask())).ToArray();

    // Drag-drop: Windows hands the app a DataPackageView; this builds the same thing
    [STATestMethod]
    public void ReadPaths_DroppedFiles_ReturnsPaths()
    {
        var files   = SampleFiles(TestVideos.Batch);
        var package = new DataPackage();
        package.SetStorageItems(files);

        var paths = Pump(ClipboardInput.ReadPathsAsync(package.GetView()));

        CollectionAssert.AreEqual(files.Select(f => f.Path).ToArray(), paths.ToArray());
    }

    [STATestMethod]
    public void ReadPaths_ClipboardFiles_ReturnsPaths()
    {
        var files   = SampleFiles(TestVideos.Tiny, TestVideos.Small);
        var package = new DataPackage();
        package.SetStorageItems(files);
        SetClipboard(package, StandardDataFormats.StorageItems, StandardDataFormats.Html);

        var paths = Pump(ClipboardInput.ReadPathsAsync(Clipboard.GetContent()));

        CollectionAssert.AreEqual(files.Select(f => f.Path).ToArray(), paths.ToArray());
    }

    [STATestMethod]
    public void ReadPaths_ClipboardText_ParsesPaths()
    {
        var a       = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);
        var b       = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        var package = new DataPackage();
        package.SetText($"\"{a}\"\r\n{b}\r\n");
        SetClipboard(package, StandardDataFormats.Text, StandardDataFormats.StorageItems);

        var paths = Pump(ClipboardInput.ReadPathsAsync(Clipboard.GetContent()));

        CollectionAssert.AreEqual(new[] { a, b }, paths.ToArray());
    }

    [STATestMethod]
    public void ReadPaths_HtmlOnly_ReturnsEmpty()
    {
        var package = new DataPackage();
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<b>hi</b>"));

        Assert.AreEqual(0, Pump(ClipboardInput.ReadPathsAsync(package.GetView())).Count);
    }

    [STATestMethod]
    public void ReadPaths_EmptyText_ReturnsEmpty()
    {
        var package = new DataPackage();
        package.SetText("   ");

        Assert.AreEqual(0, Pump(ClipboardInput.ReadPathsAsync(package.GetView())).Count);
    }

    // CopyFileAsync sets the clipboard after an await, so it needs the STA context like the app's UI thread
    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task CopyFile_PutsFileOnClipboard()
    {
        var path = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);

        await ClipboardInput.CopyFileAsync(path);
        WaitForClipboard(StandardDataFormats.StorageItems, StandardDataFormats.Html);

        var items = Pump(Clipboard.GetContent().GetStorageItemsAsync().AsTask());

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual(path, items[0].Path);
    }

    [TestMethod]
    public async Task Retry_ClipboardBusyTwice_ThenSucceeds()
    {
        var calls = 0;

        var result = await ClipboardInput.RetryAsync(() =>
        {
            calls++;
            return calls < 3 ? throw new System.Runtime.InteropServices.COMException("busy") : Task.FromResult("ok");
        });

        Assert.AreEqual("ok", result);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task Retry_AlwaysBusy_ThrowsAfterLastTry()
    {
        var calls = 0;

        await Assert.ThrowsAsync<System.Runtime.InteropServices.COMException>(() =>
            ClipboardInput.RetryAsync<string>(() => { calls++; throw new System.Runtime.InteropServices.COMException("busy"); }));

        Assert.AreEqual(ClipboardInput.Attempts, calls);
    }

    // Picker: picked files flow into the same AddPaths the paste and drop paths use
    [TestMethod]
    public async Task PickedFiles_QueueThroughAddPaths()
    {
        var files = await Task.WhenAll(TestVideos.Batch.Select(name => StorageFile.GetFileFromPathAsync(TestVideos.CopyToTemp(_dir, name)).AsTask()));
        var ran   = new List<string>();
        var queue = new CompressQueue((item, _, _) => { ran.Add(item.Path); return Task.FromResult(new CompressResult(ItemStatus.Done)); });

        queue.AddPaths(files.Select(f => f.Path), 100);
        await queue.WhenIdle();

        CollectionAssert.AreEqual(files.Select(f => f.Path).ToArray(), ran);
    }
}
