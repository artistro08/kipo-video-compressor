using Kipo.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Kipo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ClipboardInputTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup() => _dir = TestVideos.NewTempDir();

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    private async Task<StorageFile[]> SampleFilesAsync(params string[] names)
    {
        var files = new List<StorageFile>();

        foreach (var name in names)
        {
            files.Add(await StorageFile.GetFileFromPathAsync(TestVideos.CopyToTemp(_dir, name)));
        }

        return files.ToArray();
    }

    // Drag-drop: Windows hands the app a DataPackageView; this builds the same thing
    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task ReadPaths_DroppedFiles_ReturnsPaths()
    {
        var files   = await SampleFilesAsync(TestVideos.Batch);
        var package = new DataPackage();
        package.SetStorageItems(files);

        var paths = await ClipboardInput.ReadPathsAsync(package.GetView());

        CollectionAssert.AreEqual(files.Select(f => f.Path).ToArray(), paths.ToArray());
    }

    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task ReadPaths_ClipboardFiles_ReturnsPaths()
    {
        var files   = await SampleFilesAsync(TestVideos.Tiny, TestVideos.Small);
        var package = new DataPackage();
        package.SetStorageItems(files);
        Clipboard.SetContent(package);
        Clipboard.Flush();

        var paths = await ClipboardInput.ReadPathsAsync(Clipboard.GetContent());

        CollectionAssert.AreEqual(files.Select(f => f.Path).ToArray(), paths.ToArray());
    }

    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task ReadPaths_ClipboardText_ParsesPaths()
    {
        var a       = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);
        var b       = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        var package = new DataPackage();
        package.SetText($"\"{a}\"\r\n{b}\r\n");
        Clipboard.SetContent(package);
        Clipboard.Flush();

        var paths = await ClipboardInput.ReadPathsAsync(Clipboard.GetContent());

        CollectionAssert.AreEqual(new[] { a, b }, paths.ToArray());
    }

    [TestMethod]
    public async Task ReadPaths_HtmlOnly_ReturnsEmpty()
    {
        var package = new DataPackage();
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<b>hi</b>"));

        Assert.AreEqual(0, (await ClipboardInput.ReadPathsAsync(package.GetView())).Count);
    }

    [TestMethod]
    public async Task ReadPaths_EmptyText_ReturnsEmpty()
    {
        var package = new DataPackage();
        package.SetText("   ");

        Assert.AreEqual(0, (await ClipboardInput.ReadPathsAsync(package.GetView())).Count);
    }

    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task CopyFile_PutsFileOnClipboard()
    {
        var path = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);

        await ClipboardInput.CopyFileAsync(path);

        var items = await Clipboard.GetContent().GetStorageItemsAsync();

        Assert.AreEqual(1, items.Count);
        Assert.AreEqual(path, items[0].Path);
    }

    // Picker: picked files flow into the same AddPaths the paste and drop paths use
    [TestMethod]
    public async Task PickedFiles_QueueThroughAddPaths()
    {
        var files = await SampleFilesAsync(TestVideos.Batch);
        var ran   = new List<string>();
        var queue = new CompressQueue((item, _, _) => { ran.Add(item.Path); return Task.FromResult(new CompressResult(ItemStatus.Done)); });

        queue.AddPaths(files.Select(f => f.Path), 100);
        await queue.WhenIdle();

        CollectionAssert.AreEqual(files.Select(f => f.Path).ToArray(), ran);
    }
}
