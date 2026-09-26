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

        Assert.IsTrue(ok, $"Expected {count} rows with status '{status}', saw {Rows().Length}");
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

    private void FocusQueueAndPaste()
    {
        _window.Focus();
        ById("QueueList").Click();
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
        dialog.SetForeground();
        TypeInFileNameBox(_dir);
        Thread.Sleep(1500);
        TypeInFileNameBox(string.Join(" ", sources.Select(s => $"\"{Path.GetFileName(s)}\"")));

        WaitForRows(2, "Already under target", Short);
    }

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

        // Off: clipboard keeps the pasted text
        ById("SettingsButton").Click();
        ById("CopyToClipboardToggle").AsToggleButton().Toggle();
        Keyboard.Type(VirtualKeyShort.ESCAPE);

        PasteText(first);
        WaitForRows(2, "Done", Long);
        Thread.Sleep(1000);
        Assert.IsFalse(ClipboardHasFile(), "Clipboard changed with setting off");
    }

    private static void TypeInFileNameBox(string text)
    {
        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.KEY_N);
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(text);
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
