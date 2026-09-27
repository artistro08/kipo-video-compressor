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
    [Timeout(120_000)]
    public void UnregisterShell_WhileKipoIsOpen_CleansUpBeforeExiting()
    {
        const string key = @"Software\Classes\SystemFileAssociations\.mp4\shell\Kipo";

        Launch(new AppSettings { PresetMb = 10 });

        using (var planted = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key))
        {
            planted.SetValue("LegacyDisable", "");
        }

        try
        {
            using var uninstall = Process.Start(KipoStart(["--unregister-shell"]))!;

            Assert.IsTrue(uninstall.WaitForExit(15_000), "--unregister-shell didn't exit");
            Assert.IsNull(Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key), "Override still there when the uninstall step finished");
            Assert.IsFalse(_app!.HasExited, "The open Kipo window was closed");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }

    [STATestMethod]
    [Timeout(600_000)]
    [DataRow(false)]
    [DataRow(true)]
    public void FinishedRow_Copy_PutsFileOnClipboard(bool inDialog)
    {
        var source   = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        var settings = new AppSettings { UseCustom = true, CustomMb = 1, CopyToClipboard = false };

        if (inDialog)
        {
            LaunchCompress(settings, "8", source);
        }
        else
        {
            Launch(settings);
            PasteText(source);
        }

        WaitForRows(1, inDialog ? "Already under target" : "Done", Long);
        SettledHeight();

        var row    = Rows()[0];
        var copy   = row.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Copy"))) ?? throw new AssertFailedException("No Copy button");
        var window = _window.BoundingRectangle;

        Assert.IsTrue(copy.BoundingRectangle.Right <= window.Right, "Copy button cut off");

        copy.Click();

        var copied = Retry.WhileFalse(() =>
        {
            try
            {
                var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                return view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        }, Short, TimeSpan.FromMilliseconds(200)).Result;

        Assert.IsTrue(copied, "File not on the clipboard");
        Assert.IsNotNull(Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByName("Copied to clipboard")), Short).Result, "No copied banner");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void CustomForm_Layout()
    {
        var sources = TestVideos.Batch.Select(name => TestVideos.CopyToTemp(_dir, name)).ToArray();

        LaunchCompress(new AppSettings { CustomMb = 20 }, "custom", sources);

        var box      = ById("CustomDialogSizeBox").BoundingRectangle;
        var compress = ById("CustomDialogCompressButton").BoundingRectangle;
        var cancel   = ById("CustomDialogCancelButton").BoundingRectangle;
        var window   = _window.BoundingRectangle;
        var scale    = box.Height / 32.0;

        // Box, Compress and Cancel share one row
        Assert.IsTrue(Math.Abs(box.Y + box.Height / 2 - (compress.Y + compress.Height / 2)) <= 3, "Compress isn't on the size row");
        Assert.IsTrue(Math.Abs(box.Y + box.Height / 2 - (cancel.Y + cancel.Height / 2)) <= 3, "Cancel isn't on the size row");
        Assert.IsTrue(compress.X > box.Right && cancel.X > compress.Right, "Expected box, then Compress, then Cancel");

        // The selected files are listed
        foreach (var source in sources)
        {
            Assert.IsNotNull(_window.FindFirstDescendant(cf => cf.ByName(Path.GetFileName(source))), $"{Path.GetFileName(source)} not shown");
        }

        // Only a close button, and no wider than it needs to be
        // Windows still lists hidden caption buttons to accessibility tools; they must not be usable
        foreach (var name in new[] { "Minimize", "Maximize" })
        {
            var button = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)));
            var closeRect = _window.FindFirstDescendant(cf => cf.ByName("Close"))?.BoundingRectangle;
            Assert.IsTrue(button is null || button.BoundingRectangle.Width == 0 || button.Properties.IsOffscreen.ValueOrDefault || !button.IsEnabled, $"{name} is shown: {button?.BoundingRectangle} close {closeRect}");
        }

        Assert.IsTrue(window.Width <= 440 * scale, $"Form window too wide: {window.Width}px");

        // Title sits in the middle of a title bar as tall as the close button
        var close = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Close")))!.BoundingRectangle;
        var title = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("Kipo Video Compressor")))!.BoundingRectangle;

        Assert.IsTrue(Math.Abs(close.Y + close.Height / 2 - (title.Y + title.Height / 2)) <= 3 * scale, $"Title not centered with the close button (title {title}, close {close})");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void CustomForm_Cancel_ClosesWithoutCompressing()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Small);

        LaunchCompress(new AppSettings { CustomMb = 20 }, "custom", source);

        ById("CustomDialogCancelButton").Click();

        Assert.IsTrue(Retry.WhileFalse(() => _app!.HasExited, Short).Result, "Dialog didn't close");
        Assert.AreEqual(1, Directory.GetFiles(_dir).Length, "Something was written");
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

        // Same message the title bar's X sends
        using (var kipo = Process.GetProcessById(_app!.ProcessId))
        {
            kipo.CloseMainWindow();
        }

        Assert.IsNotNull(Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByName("Stop compressing and close?")), Short).Result, "No close prompt");

        var stop = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Stop"))) ?? throw new AssertFailedException("No Stop button");
        stop.AsButton().Invoke();

        Assert.IsTrue(Retry.WhileFalse(() => _app!.HasExited, Short).Result, "Window didn't close");
        Assert.AreEqual(0, Directory.GetFiles(_dir, "*.part").Length, "Unfinished .part left behind");
    }

    [STATestMethod]
    [Timeout(600_000)]
    public void MainWindow_CloseWhileCompressing_AsksThenStops()
    {
        // Two large files, so something is still running when the window closes (hardware encoding is quick)
        var first  = TestVideos.CopyToTemp(_dir, TestVideos.Large);
        var second = Path.Combine(_dir, "second copy.mp4");
        File.Copy(first, second);

        Launch(new AppSettings { UseCustom = true, CustomMb = 8, CopyToClipboard = false });
        PasteText(first + Environment.NewLine + second);

        Assert.IsNotNull(Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByAutomationId("RowSpinner")), Short).Result, "Not compressing");

        // Same message the title bar's X sends
        using (var kipo = Process.GetProcessById(_app!.ProcessId))
        {
            kipo.CloseMainWindow();
        }

        Assert.IsNotNull(Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByName("Stop compressing and close?")), Short).Result, "No close prompt");

        // The prompt's title can show before its buttons are in the tree
        var stop = Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Stop"))), Short).Result ?? throw new AssertFailedException("No Stop button");
        stop.AsButton().Invoke();

        Assert.IsTrue(Retry.WhileFalse(() => _app!.HasExited, Short).Result, "Window didn't close");
        Assert.AreEqual(0, Directory.GetFiles(_dir, "*.part").Length, "Unfinished .part left behind");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Compress_FinishedRow_HasFileButtons()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);

        LaunchCompress(new AppSettings { PresetMb = 10 }, "25", source);
        WaitForRows(1, "Already under target", Short);

        AssertOpenFileInOverflow(Rows()[0]);
        Assert.IsNotNull(Rows()[0].FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Open in folder"))));
    }
}
