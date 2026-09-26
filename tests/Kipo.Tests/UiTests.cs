using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Kipo.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Kipo.Tests;

/// <summary>
/// Drives the real app with the real mouse and keyboard.
/// </summary>
/// <remarks>
/// Needs an unlocked desktop and a built app
/// (<c>dotnet build src/Kipo/Kipo.csproj -p:Platform=x64</c>); without the exe the tests
/// are Inconclusive. Each test launches Kipo with its own temp settings folder.
/// </remarks>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed in TestCleanup after each test.")]
public sealed class UiTests
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Long  = TimeSpan.FromMinutes(5);

    private string _dir = "";
    private string _dataDir = "";
    private Application? _app;
    private UIA3Automation? _automation;
    private Window _window = null!;

    private static string ExePath
    {
        get
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

            return Path.Combine(root, "src", "Kipo", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "Kipo.exe");
        }
    }

    [TestInitialize]
    public void Setup()
    {
        _dir     = TestVideos.NewTempDir();
        _dataDir = TestVideos.NewTempDir();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _app?.Close();
        _app?.Dispose();
        _automation?.Dispose();
        Directory.Delete(_dir, true);
        Directory.Delete(_dataDir, true);
    }

    private void Launch(AppSettings settings)
    {
        if (!File.Exists(ExePath))
        {
            Assert.Inconclusive($"Build the app first: {ExePath}");
        }

        new SettingsStore(Path.Combine(_dataDir, "settings.json")).Save(settings);

        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        start.Environment["KIPO_DATA_DIR"] = _dataDir;

        _automation = new UIA3Automation();
        _app        = Application.Launch(start);
        _window     = _app.GetMainWindow(_automation, Short) ?? throw new AssertFailedException("Kipo window didn't open");
    }

    private AutomationElement ById(string id) =>
        Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByAutomationId(id)), Short).Result
        ?? throw new AssertFailedException($"Missing element {id}");

    private AutomationElement[] Rows() =>
        ById("QueueList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));

    private void WaitForRows(int count, string status, TimeSpan timeout)
    {
        var ok = Retry.WhileFalse(() =>
        {
            var rows = Rows();
            return rows.Length == count && rows.All(r => r.FindAllDescendants().Any(t => t.Name.StartsWith(status, StringComparison.Ordinal)));
        }, timeout, TimeSpan.FromMilliseconds(250)).Result;

        var seen = string.Join(" | ", Rows().SelectMany(r => r.FindAllDescendants()).Select(t => t.Name).Where(n => n.Length > 0));

        Assert.IsTrue(ok, $"Expected {count} rows with status '{status}', saw {Rows().Length}: {seen}");
    }

    private Window OpenExplorer(string selectPath)
    {
        Process.Start("explorer.exe", $"/select,\"{selectPath}\"")?.Dispose();

        var folderName = Path.GetFileName(Path.GetDirectoryName(selectPath))!;
        var explorer   = Retry.WhileNull(() => _automation!.GetDesktop()
            .FindAllChildren(cf => cf.ByClassName("CabinetWClass"))
            .FirstOrDefault(w => w.Name.Contains(folderName, StringComparison.OrdinalIgnoreCase))?.AsWindow(), Short).Result;

        return explorer ?? throw new AssertFailedException("Explorer window didn't open");
    }

    private static void SlowDrag(System.Drawing.Point from, System.Drawing.Point to)
    {
        Mouse.MoveTo(from);
        Mouse.Down(MouseButton.Left);

        for (var step = 1; step <= 20; step++)
        {
            Mouse.MoveTo(from.X + (to.X - from.X) * step / 20, from.Y + (to.Y - from.Y) * step / 20);
            Thread.Sleep(30);
        }

        Mouse.Up(MouseButton.Left);
    }

    // The queue is hidden until the first file, so click the always-present drop zone
    private void FocusQueueAndPaste()
    {
        // Click a corner of the drop zone; the center holds the Browse button
        var zone = ById("DropZone").BoundingRectangle;

        _window.Focus();
        Mouse.Click(new System.Drawing.Point(zone.X + 20, zone.Y + 20));
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
    }

    [STATestMethod]
    [Timeout(600_000)]
    public void DragFromExplorer_Compresses()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        Launch(new AppSettings { UseCustom = true, CustomMb = 1 });

        var explorer = OpenExplorer(source);

        try
        {
            var file = Retry.WhileNull(() => explorer.FindFirstDescendant(cf => cf.ByName(Path.GetFileName(source)).And(cf.ByControlType(ControlType.ListItem))), Short).Result
                ?? throw new AssertFailedException("File not visible in Explorer");

            SlowDrag(file.GetClickablePoint(), ById("DropZone").GetClickablePoint());
            WaitForRows(1, "Done", Long);
        }
        finally
        {
            explorer.Close();
        }

        Assert.IsTrue(File.Exists(Path.Combine(_dir, Path.GetFileNameWithoutExtension(source) + "-compressed.mp4")));
    }

    [STATestMethod]
    [Timeout(300_000)]
    public void CopyInExplorer_CtrlV_Queues()
    {
        var sources = TestVideos.Batch.Take(2).Select(name => TestVideos.CopyToTemp(_dir, name)).ToArray();
        Launch(new AppSettings { PresetMb = 10 });

        var explorer = OpenExplorer(sources[0]);

        try
        {
            // Click the first file, Ctrl+click the second, Ctrl+C
            var items = sources
                .Select(s => Retry.WhileNull(() => explorer.FindFirstDescendant(cf => cf.ByName(Path.GetFileName(s)).And(cf.ByControlType(ControlType.ListItem))), Short).Result
                    ?? throw new AssertFailedException($"{Path.GetFileName(s)} not visible in Explorer"))
                .ToArray();

            explorer.SetForeground();
            items[0].Click();

            using (Keyboard.Pressing(VirtualKeyShort.CONTROL))
            {
                items[1].Click();
            }

            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
            Thread.Sleep(500);
        }
        finally
        {
            explorer.Close();
        }

        FocusQueueAndPaste();
        WaitForRows(2, "Already under target", Short);
    }

    [STATestMethod]
    [Timeout(300_000)]
    public void Browse_PickTwoFiles_Queues()
    {
        var sources = TestVideos.Batch.Take(2).Select(name => TestVideos.CopyToTemp(_dir, name)).ToArray();
        Launch(new AppSettings { PresetMb = 10 });

        ById("BrowseButton").Click();

        var dialog = Retry.WhileNull(() => _app!.GetAllTopLevelWindows(_automation!)
            .SelectMany(w => w.ModalWindows.Append(w))
            .FirstOrDefault(w => w.ClassName == "#32770"), Short).Result
            ?? throw new AssertFailedException("File picker didn't open");

        // Type like a person: Alt+N jumps to "File name", go to the folder, then pick both files by name
        TypeInFileNameBox(dialog, _dir);
        Thread.Sleep(1500);
        TypeInFileNameBox(dialog, string.Join(" ", sources.Select(s => $"\"{Path.GetFileName(s)}\"")));

        WaitForRows(2, "Already under target", Short);
    }

    [STATestMethod]
    [Timeout(600_000)]
    public void CustomSize_TypedWithoutEnter_UsedForNextDrop()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        Launch(new AppSettings { UseCustom = true, CustomMb = 20 });

        var explorer = OpenExplorer(source);

        try
        {
            var file = Retry.WhileNull(() => explorer.FindFirstDescendant(cf => cf.ByName(Path.GetFileName(source)).And(cf.ByControlType(ControlType.ListItem))), Short).Result
                ?? throw new AssertFailedException("File not visible in Explorer");

            // Type 1 MB but don't press Enter or leave the box, then drop straight away
            var input = ById("CustomSizeBox").FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))
                ?? throw new AssertFailedException("Custom size input not found");

            // Explorer can grab focus late after opening; retry until the "1" lands
            var typed = Retry.WhileFalse(() =>
            {
                _window.SetForeground();
                input.Click();
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                Keyboard.Type("1");
                Thread.Sleep(200);
                return input.AsTextBox().Text == "1";
            }, Short, TimeSpan.FromMilliseconds(500)).Result;

            Assert.IsTrue(typed, "Typing didn't reach the custom size box");

            SlowDrag(file.GetClickablePoint(), ById("DropZone").GetClickablePoint());

            // 1.2 MB source: 20 MB would say "Already under target"; 1 MB compresses
            WaitForRows(1, "Done", TimeSpan.FromSeconds(60));
        }
        finally
        {
            explorer.Close();
        }
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Browse_Cancel_ShowsNoError()
    {
        Launch(new AppSettings { PresetMb = 10 });

        ById("BrowseButton").Click();

        var dialog = Retry.WhileNull(() => _app!.GetAllTopLevelWindows(_automation!)
            .SelectMany(w => w.ModalWindows.Append(w))
            .FirstOrDefault(w => w.ClassName == "#32770"), Short).Result
            ?? throw new AssertFailedException("File picker didn't open");

        dialog.SetForeground();
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Thread.Sleep(2000);

        Assert.IsNull(_window.FindFirstDescendant(cf => cf.ByName("Couldn't open the file picker.")), "Error shown after cancel");
        Assert.IsNull(_window.FindFirstDescendant(cf => cf.ByAutomationId("QueueList")), "Queue shown after cancel");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Queue_HiddenUntilFileAdded()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);
        Launch(new AppSettings { PresetMb = 10 });

        Assert.IsNull(_window.FindFirstDescendant(cf => cf.ByAutomationId("QueueList")), "Queue shown while empty");
        Assert.IsNull(_window.FindFirstDescendant(cf => cf.ByAutomationId("CancelAllLink")), "Cancel all shown while empty");

        PasteText(source);

        WaitForRows(1, "Already under target", Short);
        Assert.IsNotNull(ById("CancelAllLink"));
        Assert.AreEqual(ControlType.Hyperlink, ById("CancelAllLink").ControlType);
        Assert.IsNotNull(Rows()[0].FindFirstDescendant(cf => cf.ByAutomationId("RowIcon").And(cf.ByName("Done"))), "No checkmark on finished row");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void DropZone_FillsMostOfTheWidth()
    {
        Launch(new AppSettings { PresetMb = 10 });

        var zone   = ById("DropZone").BoundingRectangle;
        var window = _window.BoundingRectangle;
        var share  = zone.Width / (double)window.Width;

        Assert.IsTrue(share is > 0.8 and < 0.9, $"Drop zone is {share:P0} of the window width");
        Assert.IsTrue(zone.Height > zone.Width * 0.4, $"Drop zone too short: {zone.Height}px");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Settings_OpensOwnViewAndBackReturns()
    {
        Launch(new AppSettings { PresetMb = 10 });

        ById("SettingsButton").Click();
        Assert.IsNotNull(ById("SettingsView"));
        Assert.IsNotNull(ById("CopyToClipboardToggle"));

        GoBack();

        Assert.IsNotNull(ById("DropZone"));
    }

    // The back arrow slides in; click it until the settings view is gone
    private void GoBack()
    {
        // Let the arrow finish sliding in; a click mid-animation lands on the drag area
        Thread.Sleep(1500);

        var closed = Retry.WhileFalse(() =>
        {
            _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Back")))?.Click();
            Thread.Sleep(500);
            return _window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsView")) is null;
        }, Short, TimeSpan.FromMilliseconds(250)).Result;

        Assert.IsTrue(closed, "Settings view still open");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void SecondLaunch_ActivatesExistingWindow()
    {
        Launch(new AppSettings { PresetMb = 10 });

        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        start.Environment["KIPO_DATA_DIR"] = _dataDir;

        using var second = Process.Start(start) ?? throw new AssertFailedException("Second launch failed");

        Assert.IsTrue(second.WaitForExit(15_000), "Second instance stayed open");
        Assert.AreEqual(1, Process.GetProcessesByName("Kipo").Count(p => string.Equals(p.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase)), "More than one Kipo running");
        Assert.IsTrue(Retry.WhileFalse(() => _window.IsAvailable && _window.Properties.NativeWindowHandle.Value == GetForegroundWindow(), Short).Result, "Existing window not brought to front");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [STATestMethod]
    [Timeout(120_000)]
    public void SizeCombo_Custom_TogglesNumberBox()
    {
        Launch(new AppSettings { PresetMb = 10 });

        var combo = ById("SizeCombo").AsComboBox();

        Assert.IsNull(_window.FindFirstDescendant(cf => cf.ByAutomationId("CustomSizeBox")));

        combo.Select("Custom");
        Assert.IsNotNull(ById("CustomSizeBox"));

        combo.Select("25 MB");
        Assert.IsTrue(Retry.WhileFalse(() => _window.FindFirstDescendant(cf => cf.ByAutomationId("CustomSizeBox")) is null, Short).Result);
    }

    [STATestMethod]
    [Timeout(900_000)]
    public void ClipboardToggle_ControlsResultCopy()
    {
        var first = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        Launch(new AppSettings { UseCustom = true, CustomMb = 1, CopyToClipboard = true });

        // On: result lands on clipboard
        PasteText(first);
        WaitForRows(1, "Done", Long);
        Assert.IsTrue(Retry.WhileFalse(ClipboardHasFile, Short).Result, "Result not on clipboard");
        Assert.IsNotNull(Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByName("Copied to clipboard")), Short).Result, "No copied alert");

        // Off: clipboard keeps the pasted text
        ById("SettingsButton").Click();
        ById("CopyToClipboardToggle").AsToggleButton().Toggle();
        GoBack();

        PasteText(first);
        WaitForRows(2, "Done", Long);
        Thread.Sleep(1000);
        Assert.IsFalse(ClipboardHasFile(), "Clipboard changed with setting off");
    }

    // Retries until the text really sits in the dialog's "File name" box, then presses Enter
    private static void TypeInFileNameBox(Window dialog, string text)
    {
        var landed = Retry.WhileFalse(() =>
        {
            dialog.SetForeground();
            Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.KEY_N);
            Thread.Sleep(200);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(text);
            Thread.Sleep(200);

            var box = dialog.FindFirstDescendant(cf => cf.ByAutomationId("1148"));
            var edit = box?.ControlType == ControlType.Edit ? box : box?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));

            return edit?.AsTextBox().Text == text;
        }, Short, TimeSpan.FromMilliseconds(500)).Result;

        Assert.IsTrue(landed, $"Couldn't type into the file picker: {text}");
        Keyboard.Type(VirtualKeyShort.ENTER);
    }

    private void PasteText(string path)
    {
        var package = new DataPackage();
        package.SetText(path);

        // Clipboard managers (Raycast, clipboard history) can hold the clipboard briefly
        Assert.IsTrue(Retry.WhileFalse(() =>
        {
            try
            {
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        }, Short, TimeSpan.FromMilliseconds(200)).Result, "Couldn't set the clipboard");

        FocusQueueAndPaste();
    }

    private static bool ClipboardHasFile()
    {
        try
        {
            return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
