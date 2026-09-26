using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Kipo.Core;

namespace Kipo.Tests;

// Right-click launches: Kipo.exe --compress <size> <paths…> opens the compress dialog
public sealed partial class UiTests
{
    // Start info for Kipo.exe with this test's settings folder
    private ProcessStartInfo KipoStart(IReadOnlyList<string> args)
    {
        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false };

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["KIPO_DATA_DIR"] = _dataDir;

        return start;
    }

    private void LaunchCompress(AppSettings settings, string size, params string[] paths) =>
        Launch(settings, args: ["--compress", size, .. paths]);

    [STATestMethod]
    [Timeout(120_000)]
    public void Compress_Preset_OpensDialogAndStarts()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);

        LaunchCompress(new AppSettings { PresetMb = 10 }, "25", source);

        Assert.AreEqual("Kipo Video Compressor", _window.Title);
        Assert.IsNull(_window.FindFirstDescendant(cf => cf.ByAutomationId("DropZone")), "Main window opened instead of the dialog");
        WaitForRows(1, "Already under target", Short);
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Compress_UnknownSize_OpensMainWindow()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);

        LaunchCompress(new AppSettings { PresetMb = 10 }, "1", source);

        Assert.IsNotNull(ById("DropZone"), "Main window didn't open");
    }

    [STATestMethod]
    [Timeout(600_000)]
    public void Compress_Custom_AsksForSizeThenCompresses()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Small);

        LaunchCompress(new AppSettings { CustomMb = 20, CopyToClipboard = false }, "custom", source);

        var box   = ById("CustomDialogSizeBox");
        var input = box.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) ?? throw new AssertFailedException("No size input");

        Assert.AreEqual("20", input.AsTextBox().Text, "Not pre-filled with the saved custom size");

        // Retry until the "1" really sits in the box, then press Enter
        var typed = Retry.WhileFalse(() =>
        {
            _window.SetForeground();
            input.Click();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type("1");
            Thread.Sleep(200);
            return input.AsTextBox().Text == "1";
        }, Short, TimeSpan.FromMilliseconds(500)).Result;

        Assert.IsTrue(typed, "Couldn't type the size");
        Keyboard.Type(VirtualKeyShort.ENTER);

        WaitForRows(1, "Done", Long);
        Assert.AreEqual(1, new SettingsStore(Path.Combine(_dataDir, "settings.json")).Load().CustomMb, "Custom size not saved");
    }

    [STATestMethod]
    [Timeout(180_000)]
    public void Compress_QuickLaunches_ShareOneDialog()
    {
        var sources = TestVideos.Batch.Select(name => TestVideos.CopyToTemp(_dir, name)).ToArray();

        new SettingsStore(Path.Combine(_dataDir, "settings.json")).Save(new AppSettings { PresetMb = 10 });

        // The classic menu starts Kipo once per file, all within a few milliseconds
        var first = Process.Start(KipoStart(["--compress", "25", sources[0]]))!;

        foreach (var source in sources.Skip(1))
        {
            Thread.Sleep(100);
            using var extra = Process.Start(KipoStart(["--compress", "25", source]))!;
        }

        _automation = new FlaUI.UIA3.UIA3Automation();
        _app        = FlaUI.Core.Application.Attach(first);
        _window     = _app.GetMainWindow(_automation, Short) ?? throw new AssertFailedException("No dialog");

        WaitForRows(3, "Already under target", Short);
        Assert.AreEqual(1, _app.GetAllTopLevelWindows(_automation).Length, "More than one Kipo window");
    }

    [STATestMethod]
    [Timeout(600_000)]
    public void Compress_CloseWhileCompressing_AsksThenStops()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Large);

        LaunchCompress(new AppSettings { CopyToClipboard = false }, "8", source);

        Assert.IsNotNull(Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByAutomationId("RowSpinner")), Short).Result, "Not compressing");

        _window.Close();

        var prompt = Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByName("Stop compressing and close?")), Short).Result;
        Assert.IsNotNull(prompt, "No close prompt");

        var stop = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Stop"))) ?? throw new AssertFailedException("No Stop button");
        stop.Click();

        Assert.IsTrue(Retry.WhileFalse(() => _app!.HasExited, Short).Result, "Dialog didn't close");
        Assert.AreEqual(0, Directory.GetFiles(_dir, "*.part").Length, "Unfinished .part left behind");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Compress_FinishedRow_HasFileButtons()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);

        LaunchCompress(new AppSettings { PresetMb = 10 }, "25", source);
        WaitForRows(1, "Already under target", Short);

        Assert.IsNotNull(Rows()[0].FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Open file"))));
        Assert.IsNotNull(Rows()[0].FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Open in folder"))));
    }
}
