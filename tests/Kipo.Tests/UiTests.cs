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
public sealed partial class UiTests
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

    private void Launch(AppSettings settings, string? theme = null, IReadOnlyList<string>? args = null)
    {
        if (!File.Exists(ExePath))
        {
            Assert.Inconclusive($"Build the app first: {ExePath}");
        }

        new SettingsStore(Path.Combine(_dataDir, "settings.json")).Save(settings);

        var start = KipoStart(args ?? []);

        if (theme is not null)
        {
            start.Environment["KIPO_THEME"] = theme;
        }

        _automation = new UIA3Automation();
        _app        = Application.Launch(start);
        _window     = _app.GetMainWindow(_automation, Short) ?? throw new AssertFailedException("Kipo window didn't open");

        // The window sets its width and height right after opening; wait so clicks land where aimed
        var last = System.Drawing.Rectangle.Empty;
        Retry.WhileFalse(() =>
        {
            Thread.Sleep(150);
            var now    = _window.BoundingRectangle;
            var steady = now == last;
            last       = now;
            return steady;
        }, Short);
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
            return rows.Length == count && rows.All(r => r.FindAllDescendants().Any(t => (t.Properties.Name.ValueOrDefault ?? "").StartsWith(status, StringComparison.Ordinal)));
        }, timeout, TimeSpan.FromMilliseconds(250)).Result;

        var seen = string.Join(" | ", Rows().SelectMany(r => r.FindAllDescendants()).Select(t => t.Properties.Name.ValueOrDefault ?? "").Where(n => n.Length > 0));

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

        // Nothing left to cancel
        Assert.IsTrue(Retry.WhileFalse(() => _window.FindFirstDescendant(cf => cf.ByAutomationId("CancelAllLink")) is null, Short).Result, "Cancel all shown with nothing to cancel");
        Assert.IsNotNull(Rows()[0].FindFirstDescendant(cf => cf.ByAutomationId("RowIcon").And(cf.ByName("Completed"))), "No checkmark on finished row");
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
    public void Window_HeightFitsContent_WhenEmpty()
    {
        Launch(new AppSettings { PresetMb = 10 });
        Thread.Sleep(1000);

        var window = _window.BoundingRectangle;
        var zone   = ById("DropZone").BoundingRectangle;
        var footer = ById("SizeCombo").BoundingRectangle;

        Assert.IsTrue(window.Bottom - footer.Bottom < 40, $"Empty space under footer: {window.Bottom - footer.Bottom}px");
        Assert.IsTrue(footer.Top - zone.Bottom < 80, $"Empty space between drop zone and footer: {footer.Top - zone.Bottom}px");
    }

    [STATestMethod]
    [Timeout(300_000)]
    public void Window_GrowsPerRow_CapsAtSixRows()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);
        Launch(new AppSettings { PresetMb = 10 });

        var empty = SettledHeight();

        PasteText(source);
        WaitForRows(1, "Already under target", Short);
        var one = IdleHeight();

        PasteText(string.Join("\r\n", Enumerable.Repeat(source, 5)));
        WaitForRowCount(6);
        var six = IdleHeight();

        PasteText(string.Join("\r\n", Enumerable.Repeat(source, 2)));
        WaitForRowCount(8);
        var eight = IdleHeight();

        Assert.IsTrue(one > empty + 20, $"Didn't grow for first row: {empty} -> {one}");
        Assert.IsTrue(six > one + 100, $"Didn't grow to six rows: {one} -> {six}");
        Assert.IsTrue(Math.Abs(eight - six) <= 4, $"Grew past six rows: {six} -> {eight}");
    }

    [STATestMethod]
    [Timeout(300_000)]
    public void Window_DragStopsAtContentHeight()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);
        Launch(new AppSettings { PresetMb = 10 });

        PasteText(string.Join("\r\n", Enumerable.Repeat(source, 3)));
        WaitForRowCount(3);
        var content = SettledHeight();

        // Shorter: allowed (also proves the edge drag works)
        DragBottomEdge(-60);
        var shorter = SettledHeight();
        Assert.IsTrue(shorter < content - 30, $"Couldn't drag shorter: {content} -> {shorter}");

        // Taller than content: stops at the content height
        DragBottomEdge(250);
        var taller = SettledHeight();
        Assert.IsTrue(taller > shorter + 30, $"Couldn't drag back taller: {shorter} -> {taller}");
        Assert.IsTrue(taller <= content + 4, $"Dragged past content: {content} -> {taller}");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Window_AnimatesHeightChange()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);
        Launch(new AppSettings { PresetMb = 10 });
        SettledHeight();

        var heights = new HashSet<int>();
        PasteText(string.Join("\r\n", Enumerable.Repeat(source, 3)));

        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 1000)
        {
            heights.Add(_window.BoundingRectangle.Height);
            Thread.Sleep(10);
        }

        Assert.IsTrue(heights.Count >= 4, $"Height jumped instead of animating: {string.Join(", ", heights)}");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void BackButton_IsSquare_AndAnimatesIn()
    {
        Launch(new AppSettings { PresetMb = 10 });
        Thread.Sleep(800);

        var bar = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.TitleBar).Or(cf.ByClassName("TitleBar")))!.BoundingRectangle;

        ById("SettingsButton").Click();

        // The window title slides right as the arrow opens up
        var titleX = new HashSet<int>();
        var clock  = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < 800)
        {
            if (TitleText() is { } title)
            {
                titleX.Add(title.BoundingRectangle.X);
            }

            Thread.Sleep(10);
        }

        var back  = ById("BackButton").BoundingRectangle;
        var scale = back.Height / 32.0;
        var left  = back.X - bar.X;
        var top   = back.Y - bar.Y;

        Assert.IsTrue(titleX.Count >= 3, $"Back arrow didn't animate in: {string.Join(", ", titleX)}");
        Assert.IsTrue(Math.Abs(back.Width - back.Height) <= 1, $"Back arrow not square: {back.Width}x{back.Height}");
        Assert.IsTrue(Math.Abs(left - top) <= 2, $"Back arrow {left}px from the left, {top}px from the top");
        Assert.IsTrue(TitleText()!.BoundingRectangle.X - back.Right <= 40 * scale, $"Icon and title too far from the arrow: {TitleText()!.BoundingRectangle.X - back.Right}px");
    }

    [TestMethod]
    public void AppIcon_IsKipoPurple()
    {
        if (!File.Exists(ExePath))
        {
            Assert.Inconclusive($"Build the app first: {ExePath}");
        }

        using var icon   = System.Drawing.Icon.ExtractAssociatedIcon(ExePath)!;
        using var bitmap = icon.ToBitmap();
        var corner       = bitmap.GetPixel(bitmap.Width / 6, bitmap.Height / 6);
        var hue          = corner.GetHue();

        Assert.IsTrue(hue is > 230 and < 290 && corner.GetSaturation() > 0.4, $"Exe icon corner isn't Kipo purple: {corner}");
    }

    private AutomationElement? TitleText() =>
        _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("Kipo Video Compressor")));

    [STATestMethod]
    [Timeout(120_000)]
    public void Settings_HeaderHasSpaceBelowTitleBar()
    {
        Launch(new AppSettings { PresetMb = 10 });

        ById("SettingsButton").Click();
        Thread.Sleep(800);

        var header = ById("SettingsHeader").BoundingRectangle;
        var back   = ById("BackButton").BoundingRectangle;
        var scale  = back.Height / 32.0;

        Assert.IsTrue(header.Top - back.Bottom >= 16 * scale, $"Header only {header.Top - back.Bottom}px below the title bar");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Settings_DrillsInAndOut()
    {
        Launch(new AppSettings { PresetMb = 10 });

        ById("SettingsButton").Click();
        var inSizes = SampleWidths("SettingsView");

        // Click back directly (arrow has settled), then watch the settings view shrink away
        Thread.Sleep(1000);
        ById("BackButton").Click();
        var outSizes = SampleWidths("SettingsView");

        Assert.IsTrue(inSizes.Count >= 3, $"No drill-in: {string.Join(", ", inSizes)}");
        Assert.IsTrue(outSizes.Count >= 3, $"No drill-out: {string.Join(", ", outSizes)}");
    }

    // Collects the element's width every 10ms for half a second; a scale animation shows as several widths
    private HashSet<int> SampleWidths(string id)
    {
        var widths = new HashSet<int>();
        var clock  = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < 500)
        {
            if (_window.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { } element)
            {
                widths.Add(element.BoundingRectangle.Width);
            }

            Thread.Sleep(10);
        }

        return widths;
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void CaptionButtons_FillTitleBarHeight()
    {
        Launch(new AppSettings { PresetMb = 10 });
        Thread.Sleep(1000);

        // Drop zone is 240px tall, which gives the display scale
        var scale = ById("DropZone").BoundingRectangle.Height / 240.0;
        var close = Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Close"))), Short).Result
            ?? throw new AssertFailedException("Close button not found");

        // The title bar is 48px tall; the window buttons should match
        Assert.IsTrue(close.BoundingRectangle.Height >= 46 * scale, $"Close button {close.BoundingRectangle.Height}px, want {48 * scale:0}px");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void CustomSizeBox_MatchesDropdownHeight()
    {
        Launch(new AppSettings { UseCustom = true, CustomMb = 20 });
        Thread.Sleep(800);

        // Accessibility reports the dropdown taller than it draws, so measure drawn pixels
        using var shot = _window.Capture();
        var window     = _window.BoundingRectangle;
        var combo      = DrawnHeight(shot, window, ById("SizeCombo").BoundingRectangle);
        var box        = DrawnHeight(shot, window, ById("CustomSizeBox").BoundingRectangle);

        Assert.IsTrue(Math.Abs(combo.Height - box.Height) <= 1, $"Dropdown draws {combo.Top}+{combo.Height}px, custom box {box.Top}+{box.Height}px");
        Assert.IsTrue(Math.Abs(combo.Top - box.Top) <= 1, $"Not aligned: dropdown top {combo.Top}, box top {box.Top}");

        // "Custom" and "20" sit on the same rows
        var comboText = TextRows(shot, window, ById("SizeCombo").BoundingRectangle);
        var boxText   = TextRows(shot, window, ById("CustomSizeBox").BoundingRectangle);

        Assert.IsTrue(Math.Abs(comboText.Top - boxText.Top) <= 1 && Math.Abs(comboText.Bottom - boxText.Bottom) <= 1,
            $"Text not aligned: dropdown text rows {comboText.Top}-{comboText.Bottom}, box text rows {boxText.Top}-{boxText.Bottom}");
    }

    // Rows holding bright text pixels in the left part of a control (where the value text is)
    private static (int Top, int Bottom) TextRows(System.Drawing.Bitmap shot, System.Drawing.Rectangle window, System.Drawing.Rectangle control)
    {
        var rows = Enumerable.Range(control.Y - window.Y, control.Height)
            .Where(y => Enumerable.Range(control.X - window.X + 8, 30).Any(x => shot.GetPixel(x, y).GetBrightness() > 0.7))
            .ToList();

        return rows.Count == 0 ? (0, 0) : (rows.Min(), rows.Max());
    }

    // Rows where a column just inside the control's left edge differs from the footer background
    private static (int Top, int Height) DrawnHeight(System.Drawing.Bitmap shot, System.Drawing.Rectangle window, System.Drawing.Rectangle control)
    {
        var x          = control.X - window.X + 6;
        var background = shot.GetPixel(control.X - window.X - 3, control.Y - window.Y + control.Height / 2);
        var rows       = Enumerable.Range(control.Y - window.Y - 4, control.Height + 8)
            .Where(y =>
            {
                var c = shot.GetPixel(x, y);
                return Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B) > 6;
            })
            .ToList();

        return rows.Count == 0 ? (0, 0) : (rows.Min(), rows.Max() - rows.Min() + 1);
    }

    [STATestMethod]
    [Timeout(120_000)]
    [DataRow("Light", true)]
    [DataRow("Dark", false)]
    public void Theme_FooterAndTextFollowTheme(string theme, bool light)
    {
        Launch(new AppSettings { PresetMb = 10 }, theme);
        Thread.Sleep(1000);

        using var shot  = _window.Capture();
        var footer      = ById("SizeCombo").BoundingRectangle;
        var window      = _window.BoundingRectangle;
        var background  = shot.GetPixel(footer.Right - window.X + 20, footer.Top - window.Y + footer.Height / 2).GetBrightness();

        Assert.IsTrue(light ? background > 0.75 : background < 0.3, $"{theme}: footer brightness {background:0.00}");

        // Window buttons follow the app theme: the X glyph must contrast with the title bar
        var close  = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Close")))!.BoundingRectangle;
        // Middle half of the button only; its outer corner shows the desktop through the rounded window edge
        var glyphs = Enumerable.Range(close.Width / 4, close.Width / 2)
            .SelectMany(x => Enumerable.Range(close.Height / 4, close.Height / 2).Select(y => shot.GetPixel(close.X - window.X + x, close.Y - window.Y + y).GetBrightness()))
            .ToList();

        var contrasting = glyphs.Count(b => light ? b < 0.45 : b > 0.6);

        Assert.IsTrue(contrasting >= 15, $"{theme}: only {contrasting} contrasting pixels in the close button");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void AppName_IsKipoVideoCompressor()
    {
        Launch(new AppSettings { PresetMb = 10 });

        Assert.AreEqual("Kipo Video Compressor", _window.Title);
        Assert.IsNotNull(TitleText(), "Title bar doesn't say Kipo Video Compressor");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void DropZone_ExplainsCompressing()
    {
        Launch(new AppSettings { PresetMb = 10 });

        var zone = ById("DropZone");

        Assert.IsNotNull(zone.FindFirstDescendant(cf => cf.ByName("Select a video to compress")), "Title missing");
        Assert.IsNotNull(zone.FindFirstDescendant(cf => cf.ByName("Drag and drop, paste, or browse for a video")), "Description missing");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Settings_ClipboardSettingIsACard()
    {
        Launch(new AppSettings { PresetMb = 10, CopyToClipboard = true });

        ById("SettingsButton").Click();
        Thread.Sleep(800);

        // Plain panels aren't visible to test tools, so check the pieces inside the settings view
        var view   = ById("SettingsView");
        var toggle = ById("CopyToClipboardToggle").BoundingRectangle;
        var label  = view.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("Copy result to clipboard")))
            ?? throw new AssertFailedException("Title missing");

        Assert.IsNotNull(view.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("Only works for single file selection."))), "Description missing");
        var state  = view.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("On")))
            ?? throw new AssertFailedException("On/Off text missing");
        var scale  = ById("BackButton").BoundingRectangle.Height / 32.0;

        Assert.IsTrue(label.BoundingRectangle.Right < state.BoundingRectangle.Left, "Label should sit left of the On/Off text");
        Assert.IsTrue(state.BoundingRectangle.Right <= toggle.Left + 4, "On/Off text should sit left of the switch");
        Assert.IsTrue(_window.BoundingRectangle.Right - toggle.Right < 60 * scale, $"Switch should sit at the right edge ({_window.BoundingRectangle.Right - toggle.Right}px away)");
    }

    [STATestMethod]
    [Timeout(600_000)]
    public void FinishedRow_ShowsFileButtons_AndOpensFolder()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        Launch(new AppSettings { UseCustom = true, CustomMb = 1, CopyToClipboard = false });

        PasteText(source);
        WaitForRows(1, "Done", Long);

        var row    = Rows()[0];
        var show   = row.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Open file"))) ?? throw new AssertFailedException("No Open file button");
        var folder = row.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Open in folder"))) ?? throw new AssertFailedException("No Open in folder button");
        var window = _window.BoundingRectangle;

        Assert.IsTrue(show.BoundingRectangle.Right <= window.Right && folder.BoundingRectangle.Right <= window.Right, "Buttons cut off by the window edge");
        Assert.IsTrue(show.BoundingRectangle.Width > 40 && folder.BoundingRectangle.Width > 40, "Buttons squeezed");

        folder.Click();

        var explorer = Retry.WhileNull(() => _automation!.GetDesktop()
            .FindAllChildren(cf => cf.ByClassName("CabinetWClass"))
            .FirstOrDefault(w => w.Name.Contains(Path.GetFileName(_dir), StringComparison.OrdinalIgnoreCase)), Short).Result;

        Assert.IsNotNull(explorer, "Explorer didn't open the folder");
        explorer.AsWindow().Close();
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void AlreadyUnderTargetRow_HasFileButtons()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);
        Launch(new AppSettings { PresetMb = 10 });

        PasteText(source);
        WaitForRows(1, "Already under target", Short);

        Assert.IsNotNull(Rows()[0].FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Open file"))), "No Open file button");

        var folder = Rows()[0].FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Open in folder")))
            ?? throw new AssertFailedException("No Open in folder button");

        // No compressed file was made, so it opens the original's folder
        SettledHeight();
        folder.Click();

        var explorer = Retry.WhileNull(() => _automation!.GetDesktop()
            .FindAllChildren(cf => cf.ByClassName("CabinetWClass"))
            .FirstOrDefault(w => w.Name.Contains(Path.GetFileName(_dir), StringComparison.OrdinalIgnoreCase)), Short).Result;

        Assert.IsNotNull(explorer, "Open in folder did nothing for an under-target file");
        explorer.AsWindow().Close();
    }

    [STATestMethod]
    [Timeout(600_000)]
    public void CompressingRow_ShowsSpinner_AndCancelAll()
    {
        var sources = new[] { TestVideos.Mid, TestVideos.Large }.Select(name => TestVideos.CopyToTemp(_dir, name)).ToArray();
        Launch(new AppSettings { UseCustom = true, CustomMb = 8, CopyToClipboard = false });

        PasteText(string.Join(Environment.NewLine, sources));

        var spinner = Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByAutomationId("RowSpinner")), Short, TimeSpan.FromMilliseconds(50)).Result;
        var cancel  = _window.FindFirstDescendant(cf => cf.ByAutomationId("CancelAllLink"));

        Assert.IsNotNull(spinner, "No spinner while compressing");
        Assert.IsNotNull(cancel, "No Cancel all while compressing");
        Assert.AreEqual(ControlType.Hyperlink, cancel.ControlType);

        // Window grows to fit the link; click once it has settled
        SettledHeight();
        ById("CancelAllLink").Click();
        Assert.IsTrue(Retry.WhileFalse(() => _window.FindFirstDescendant(cf => cf.ByAutomationId("CancelAllLink")) is null, Short).Result, "Cancel all still shown after cancelling");
    }

    [STATestMethod]
    [Timeout(120_000)]
    public void Settings_KeepsWindowHeight()
    {
        Launch(new AppSettings { PresetMb = 10 });
        var before = SettledHeight();

        ById("SettingsButton").Click();
        Assert.IsNotNull(ById("SettingsView"));

        Assert.IsTrue(Math.Abs(SettledHeight() - before) <= 4, $"Settings changed the height: {before} -> {SettledHeight()}");
    }

    // Height once all rows are finished (Cancel all hidden) and the window has stopped resizing
    private int IdleHeight()
    {
        Retry.WhileFalse(() => _window.FindFirstDescendant(cf => cf.ByAutomationId("CancelAllLink")) is null, Short);

        return SettledHeight();
    }

    // Waits until the window stops resizing and returns its height
    private int SettledHeight()
    {
        var last = -1;

        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(150);
            var now = _window.BoundingRectangle.Height;

            if (now == last)
            {
                return now;
            }

            last = now;
        }

        return last;
    }

    private void WaitForRowCount(int count) =>
        Assert.IsTrue(Retry.WhileFalse(() => Rows().Length == count, Short, TimeSpan.FromMilliseconds(250)).Result, $"Expected {count} rows, saw {Rows().Length}");

    private void DragBottomEdge(int deltaY)
    {
        var rect  = _window.BoundingRectangle;
        var start = new System.Drawing.Point(rect.X + rect.Width / 2, rect.Bottom - 2);

        SlowDrag(start, new System.Drawing.Point(start.X, start.Y + deltaY));
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
