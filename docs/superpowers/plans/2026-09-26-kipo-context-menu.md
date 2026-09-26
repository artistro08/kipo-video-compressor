# Kipo Right-Click Menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add "Compress with Kipo ▸ 8/10/25/50/100 MB / Custom…" to File Explorer's right-click menu (Windows 11 main menu and classic menu), opening a compress dialog, for both MSIX and MSI installs.

**Architecture:** A new Native AOT C# project `Kipo.Shell` builds a native `Kipo.Shell.dll` that implements `IExplorerCommand` and launches `Kipo.exe --compress <size> <paths>`. Kipo.exe parses that, groups launches within 500 ms, and shows a new `CompressDialog` window that reuses a shared queue list component. MSIX registers the DLL in its manifest; MSI registers a classic registry menu and, on first launch, a signed sparse identity package so Windows 11's main menu can load the same DLL.

**Tech Stack:** C# / .NET 10, WinAppSDK 2.5.1, Native AOT, .NET COM source generators, WiX 5.0.2, MSTest 4.4.1, FlaUI 5.0.0. No new packages.

**Spec:** `docs/superpowers/specs/2026-09-26-kipo-context-menu-design.md`

## Global Constraints

- No new NuGet packages. Native AOT uses the installed Visual Studio C++ tools (x64 and ARM64 confirmed present).
- File types: `.mp4`, `.mov`, `.mkv`, `.avi`, `.wmv`, `.m4v`.
- Menu text exactly: root "Compress with Kipo"; items "8 MB", "10 MB", "25 MB", "50 MB", "100 MB", "Custom…". Separator before "Custom…".
- Command line exactly: `Kipo.exe --compress <8|10|25|50|100|custom> "<path>" …`; unregister: `Kipo.exe --unregister-shell`.
- Grouping window: 500 ms. Command lines split before 30,000 characters.
- Dialog title "Kipo Video Compressor"; close prompt "Stop compressing and close?" with buttons "Stop" and "Keep going".
- Shell CLSID: `{6B1F4A52-8C3E-4E7B-9A2D-5C0F1E3B7A94}`. Sparse package name `KipoSparse`, publisher `CN=Kipo Dev`.
- The right-click menu needs the signing certificate trusted. This must be stated in the README and printed by `build.ps1`.
- Existing rules: 4-space C#/XAML, US English, tests first, commit per task, rebuild + reinstall MSIX after each finished batch, never push.

## Review Focus

1. Explorer stability: any exception inside `Kipo.Shell.dll` must be caught; a crash takes Explorer down. Tests call every exported entry point with null/garbage input (Task 4).
2. Command lines with quotes/spaces/unicode paths and very large selections (thousands of files). Tokenizer and chunking tests (Tasks 1, 3).
3. Classic menu launching one process per file: all must land in one dialog, not N dialogs (Task 2, Task 6 UI test).
4. Closing the dialog mid-compress must not leave `.part` files (Task 6 UI test).
5. MSI main-menu registration failing (untrusted cert) must leave the classic menu working and not spam errors (Task 8 test on the registrar with a fake package manager).

---

## File Structure

```
src/Kipo.Core/
  LaunchRequest.cs        parse --compress / --unregister-shell; command-line tokenizer
  RequestBatcher.cs       groups requests arriving within 500 ms
src/Kipo.Shell/            NEW, Native AOT library
  Kipo.Shell.csproj
  ShellMenu.cs            presets, titles, command-line builder (pure C#, unit tested)
  ComInterfaces.cs        IExplorerCommand, IEnumExplorerCommand, IShellItemArray, IShellItem, IClassFactory
  Commands.cs             RootCommand, SizeCommand, SeparatorCommand, CommandEnumerator
  Exports.cs              DllGetClassObject, DllCanUnloadNow, ClassFactory, own-folder lookup
src/Kipo/
  QueueView.xaml(.cs)     NEW shared list: rows, separator, Cancel all, file buttons
  WindowFitter.cs         NEW content-height sizing + animation, used by both windows
  CompressDialog.xaml(.cs) NEW
  ShellRegistration.cs    NEW sparse package + LegacyDisable + unregister (MSI only)
  App.xaml.cs, Program.cs, MainWindow.*   route launches, use QueueView/WindowFitter
  Package.appxmanifest    add fileExplorerContextMenus + comServer
  app.unpackaged.manifest NEW: exe manifest with <msix> identity for the sparse package
src/Kipo.Installer/
  Package.wxs             classic menu registry, unregister custom action
  Sparse/AppxManifest.xml NEW sparse package manifest
tests/Kipo.Tests/
  LaunchRequestTests.cs, RequestBatcherTests.cs, ShellMenuTests.cs, ShellNativeTests.cs,
  ShellRegistrationTests.cs, CompressDialogUiTests.cs, ExplorerMenuTests.cs
README.md                  NEW, includes the certificate notice
build.ps1                  AOT-publish Kipo.Shell, build + sign sparse package, print certificate notice
```

---

### Task 1: LaunchRequest + tokenizer

**Files:** Create `src/Kipo.Core/LaunchRequest.cs`; Test `tests/Kipo.Tests/LaunchRequestTests.cs`

**Interfaces — Produces:**
- `enum LaunchAction { Main, Compress, UnregisterShell }`
- `record LaunchRequest(LaunchAction Action, int? SizeMb, bool Custom, IReadOnlyList<string> Paths)`
- `LaunchRequest.Parse(IReadOnlyList<string> args) -> LaunchRequest` (args exclude the exe)
- `LaunchRequest.ParseCommandLine(string commandLine) -> LaunchRequest` (tokenizes; drops a leading token ending in `Kipo.exe`)
- `CommandLine.Split(string commandLine) -> IReadOnlyList<string>` (Windows rules: spaces split, double quotes group, `\"` inside quotes is a literal quote)

- [ ] **Step 1: Failing tests**

```csharp
using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class LaunchRequestTests
{
    [TestMethod]
    public void Parse_NoArgs_IsMain() => Assert.AreEqual(LaunchAction.Main, LaunchRequest.Parse([]).Action);

    [TestMethod]
    [DataRow("8", 8)]
    [DataRow("10", 10)]
    [DataRow("25", 25)]
    [DataRow("50", 50)]
    [DataRow("100", 100)]
    public void Parse_Preset_IsCompressWithSize(string size, int mb)
    {
        var request = LaunchRequest.Parse(["--compress", size, @"C:\v\a.mp4"]);

        Assert.AreEqual(LaunchAction.Compress, request.Action);
        Assert.AreEqual(mb, request.SizeMb);
        Assert.IsFalse(request.Custom);
        CollectionAssert.AreEqual(new[] { @"C:\v\a.mp4" }, request.Paths.ToArray());
    }

    [TestMethod]
    public void Parse_Custom_HasNoSize()
    {
        var request = LaunchRequest.Parse(["--compress", "custom", @"C:\v\a.mp4", @"C:\v\b.mov"]);

        Assert.IsTrue(request.Custom);
        Assert.IsNull(request.SizeMb);
        Assert.AreEqual(2, request.Paths.Count);
    }

    [TestMethod]
    [DataRow("7")]
    [DataRow("big")]
    [DataRow("")]
    public void Parse_UnknownSize_IsMain(string size) =>
        Assert.AreEqual(LaunchAction.Main, LaunchRequest.Parse(["--compress", size, @"C:\v\a.mp4"]).Action);

    [TestMethod]
    public void Parse_NoPaths_IsMain() => Assert.AreEqual(LaunchAction.Main, LaunchRequest.Parse(["--compress", "25"]).Action);

    [TestMethod]
    public void Parse_UnregisterShell() => Assert.AreEqual(LaunchAction.UnregisterShell, LaunchRequest.Parse(["--unregister-shell"]).Action);

    [TestMethod]
    public void Parse_KeepsNetworkPathsForValidatorToReject()
    {
        // Validation happens when queued (Failed: unsupported path), not here
        Assert.AreEqual(@"\\server\s\a.mp4", LaunchRequest.Parse(["--compress", "25", @"\\server\s\a.mp4"]).Paths[0]);
    }

    [TestMethod]
    public void Split_QuotesAndSpaces()
    {
        CollectionAssert.AreEqual(
            new[] { "--compress", "25", @"C:\my videos\a b.mp4", @"C:\c.mkv" },
            CommandLine.Split("--compress 25 \"C:\\my videos\\a b.mp4\" C:\\c.mkv").ToArray());
    }

    [TestMethod]
    public void Split_Unicode() =>
        Assert.AreEqual(@"C:\vidéos\日本.mp4", CommandLine.Split("\"C:\\vidéos\\日本.mp4\"")[0]);

    [TestMethod]
    public void ParseCommandLine_DropsExeToken()
    {
        var request = LaunchRequest.ParseCommandLine("\"C:\\Program Files\\Kipo\\Kipo.exe\" --compress 8 \"C:\\v\\a.mp4\"");

        Assert.AreEqual(8, request.SizeMb);
        Assert.AreEqual(@"C:\v\a.mp4", request.Paths[0]);
    }

    [TestMethod]
    public void ParseCommandLine_WithoutExeToken()
    {
        Assert.AreEqual(50, LaunchRequest.ParseCommandLine("--compress 50 \"C:\\v\\a.mp4\"").SizeMb);
    }
}
```

- [ ] **Step 2:** `dotnet test tests/Kipo.Tests --filter LaunchRequestTests` → build FAIL (types missing).
- [ ] **Step 3: Implement** `LaunchRequest.cs`: `Parse` checks `args[0]` for `--compress` (size in `AppSettings.Presets` or `"custom"`, ≥1 path) or `--unregister-shell`; anything else → `Main`. `CommandLine.Split` walks characters: whitespace outside quotes ends a token; `"` toggles quoting; `\"` → literal quote. Class docblocks per project style.
- [ ] **Step 4:** tests pass.
- [ ] **Step 5:** commit `feat: parse right-click launch requests`.

---

### Task 2: RequestBatcher

**Files:** Create `src/Kipo.Core/RequestBatcher.cs`; Test `tests/Kipo.Tests/RequestBatcherTests.cs`

**Interfaces — Produces:**
- `RequestBatcher<TTarget>(Func<DateTimeOffset> now, TimeSpan window, Func<LaunchRequest, TTarget> open, Action<TTarget, IReadOnlyList<string>> add)`
- `.Submit(LaunchRequest request)` — joins the last target if same size choice and within `window` of the last submit, else opens a new one.

- [ ] **Step 1: Failing tests**

```csharp
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
        request => { var d = new List<string>(request.Paths); _dialogs.Add(d); return d; },
        (d, paths) => d.AddRange(paths));

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
        Assert.AreEqual(3, _dialogs[0].Count);
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

        Assert.AreEqual(2, _dialogs.Count);
    }
}
```

- [ ] **Step 2–4:** RED, implement (~30 lines), GREEN.
- [ ] **Step 5:** commit `feat: group right-click launches into one dialog`.

---

### Task 3: Kipo.Shell project + ShellMenu logic

**Files:** Create `src/Kipo.Shell/Kipo.Shell.csproj`, `src/Kipo.Shell/ShellMenu.cs`; Test `tests/Kipo.Tests/ShellMenuTests.cs`; Modify `Kipo.slnx`, `tests/Kipo.Tests/Kipo.Tests.csproj` (reference Kipo.Shell).

`Kipo.Shell.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>Kipo.Shell</RootNamespace>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <IsAotCompatible>true</IsAotCompatible>
    <PublishAot>true</PublishAot>
    <NativeLib>Shared</NativeLib>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Kipo.Tests" />
  </ItemGroup>
</Project>
```

Kipo.Shell does not reference Kipo.Core (keeps WinRT out of the native DLL). It keeps its own preset list; a test pins it to `AppSettings.Presets`.

**Interfaces — Produces:**
- `ShellMenu.RootTitle = "Compress with Kipo"`, `ShellMenu.Items : IReadOnlyList<ShellMenuItem>` where `record ShellMenuItem(string Title, string Size, bool SeparatorBefore)`; sizes `8,10,25,50,100,custom`.
- `ShellMenu.CommandLines(string size, IReadOnlyList<string> paths, int maxLength = 30_000) -> IReadOnlyList<string>` — each `--compress <size> "p1" "p2"…`, split so none exceeds `maxLength`.

- [ ] **Step 1: Failing tests**

```csharp
using Kipo.Core;
using Kipo.Shell;

namespace Kipo.Tests;

[TestClass]
public sealed class ShellMenuTests
{
    [TestMethod]
    public void Items_MatchAppPresets_ThenCustom()
    {
        var expected = AppSettings.Presets.Select(mb => $"{mb} MB").Append("Custom…").ToArray();

        CollectionAssert.AreEqual(expected, ShellMenu.Items.Select(i => i.Title).ToArray());
        Assert.IsTrue(ShellMenu.Items[^1].SeparatorBefore);
        Assert.IsTrue(ShellMenu.Items.Take(ShellMenu.Items.Count - 1).All(i => !i.SeparatorBefore));
    }

    [TestMethod]
    public void CommandLines_QuotesEveryPath()
    {
        var lines = ShellMenu.CommandLines("25", [@"C:\my videos\a b.mp4", @"C:\c.mkv"]);

        Assert.AreEqual(1, lines.Count);
        Assert.AreEqual("--compress 25 \"C:\\my videos\\a b.mp4\" \"C:\\c.mkv\"", lines[0]);
    }

    [TestMethod]
    public void CommandLines_SplitLongSelections()
    {
        var paths = Enumerable.Range(0, 2000).Select(i => $@"C:\videos\clip number {i:0000}.mp4").ToList();

        var lines = ShellMenu.CommandLines("custom", paths);

        Assert.IsTrue(lines.Count > 1);
        Assert.IsTrue(lines.All(l => l.Length <= 30_000 && l.StartsWith("--compress custom ", StringComparison.Ordinal)));
        Assert.AreEqual(2000, lines.Sum(l => CommandLine.Split(l).Count - 2));
    }

    [TestMethod]
    public void CommandLines_RoundTripThroughLaunchParser()
    {
        var line = ShellMenu.CommandLines("custom", [@"C:\vidéos\日本 1.mp4"])[0];

        Assert.AreEqual(@"C:\vidéos\日本 1.mp4", LaunchRequest.ParseCommandLine(line).Paths[0]);
    }
}
```

- [ ] **Step 2–4:** RED, implement `ShellMenu`, GREEN.
- [ ] **Step 5:** commit `feat: add shell menu model`.

---

### Task 4: Kipo.Shell COM layer (native)

**Files:** Create `src/Kipo.Shell/ComInterfaces.cs`, `Commands.cs`, `Exports.cs`; Test `tests/Kipo.Tests/ShellNativeTests.cs`

**Interfaces — Produces:** native exports `DllGetClassObject(Guid*, Guid*, void**)`, `DllCanUnloadNow()`; CLSID `6B1F4A52-8C3E-4E7B-9A2D-5C0F1E3B7A94`.

Design notes (implement exactly):
- `[GeneratedComInterface]` definitions with `[PreserveSig]` methods returning `int` HRESULT for: `IExplorerCommand` (`a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9`), `IEnumExplorerCommand` (`a88826f8-186f-4987-aade-ea0cef8fbfe8`), `IShellItemArray` (`b63ea76d-1f85-456f-a19c-48159efa858b`), `IShellItem` (`43826d1e-e718-42ee-bc55-a1e261c37bfe`), `IClassFactory` (`00000001-0000-0000-C000-000000000046`). String outs are `nint` filled with `Marshal.StringToCoTaskMemUni`.
- `RootCommand : IExplorerCommand` — title `ShellMenu.RootTitle`, icon `"<folder>\Kipo.exe,0"`, flags `ECF_HASSUBCOMMANDS (0x1)`, `EnumSubCommands` returns a `CommandEnumerator` over `SizeCommand`s with a `SeparatorCommand` (flags `ECF_ISSEPARATOR 0x8`) before Custom.
- `SizeCommand.Invoke(items)` reads paths via `IShellItemArray.GetCount/GetItemAt` + `IShellItem.GetDisplayName(SIGDN_FILESYSPATH 0x80058000)`, builds `ShellMenu.CommandLines`, starts `<folder>\Kipo.exe` with each line (`UseShellExecute = false`).
- `<folder>` = directory of this DLL via `GetModuleHandleExW(FROM_ADDRESS | UNCHANGED_REFCOUNT, &DllGetClassObject)` + `GetModuleFileNameW`.
- Every method body is wrapped in `try { … } catch { return E_FAIL; }` and logs to `%LOCALAPPDATA%\Kipo\shell-log.txt` (best effort, swallow IO errors).
- One static `StrategyBasedComWrappers` for creating COM pointers (`GetOrCreateComInterfaceForObject`) and wrapping incoming ones.

- [ ] **Step 1: Failing native tests** — `ShellNativeTests` publishes nothing itself; it loads `artifacts\shell\win-x64\Kipo.Shell.dll` (Inconclusive if missing, with the publish command in the message) via `NativeLibrary.Load`, then:
  1. `DllGetClassObject` with the Kipo CLSID and `IID_IClassFactory` → `S_OK`; `CreateInstance(IID_IExplorerCommand)` → object.
  2. `GetTitle(null)` → "Compress with Kipo"; `GetFlags` has `0x1`; `EnumSubCommands` → 7 entries (6 sizes + separator) with titles in order.
  3. Robustness: `DllGetClassObject` with an unknown CLSID → `CLASS_E_CLASSNOTAVAILABLE (0x80040111)`; null out-pointer → `E_POINTER`; `Invoke(null, null)` on a size command → returns an HRESULT, does not throw or crash the test host.
- [ ] **Step 2:** `dotnet publish src/Kipo.Shell -c Release -r win-x64 -o artifacts/shell/win-x64` then run tests → FAIL (no exports yet).
- [ ] **Step 3:** implement the three files.
- [ ] **Step 4:** republish (x64 and arm64: `-r win-arm64 -o artifacts/shell/win-arm64`), tests GREEN; `dumpbin`-free check: `NativeLibrary.GetExport` finds both exports.
- [ ] **Step 5:** commit `feat: native Explorer command for Compress with Kipo`.

---

### Task 5: Shared QueueView + WindowFitter (refactor)

**Files:** Create `src/Kipo/QueueView.xaml(.cs)`, `src/Kipo/WindowFitter.cs`; Modify `MainWindow.xaml(.cs)`.

- Move the separator, `QueueList` ListView (template, row icons, spinner, file buttons, per-row cancel) and `CancelAllLink` into `QueueView` (a `UserControl` with a `Queue` property of type `CompressQueue`). File-button and cancel handlers move with it; errors surface through `event EventHandler<string>? Error`.
- Move `FitToContent`, `AnimateClientHeight`, min/max presenter heights into `WindowFitter(Window window, FrameworkElement page, Func<bool> paused)` with `FitLater()` / `FitNow()`.
- AutomationIds stay identical (`QueueList`, `QueueSeparator`, `CancelAllLink`, `RowIcon`, `RowSpinner`) so every existing UI test keeps working unchanged.
- [ ] **Step 1:** Run full UI suite before refactor → green baseline.
- [ ] **Step 2:** Refactor.
- [ ] **Step 3:** Full suite → still green (no test edits allowed in this task).
- [ ] **Step 4:** commit `refactor: share queue list and window sizing`.

---

### Task 6: CompressDialog + launch routing

**Files:** Create `src/Kipo/CompressDialog.xaml(.cs)`; Modify `Program.cs`, `App.xaml.cs`; Test `tests/Kipo.Tests/CompressDialogUiTests.cs`

Behavior:
- `App.OnLaunched`: `LaunchRequest.Parse(Environment.GetCommandLineArgs()[1..])`: `Compress` → `RequestBatcher.Submit` (opens `CompressDialog`, no main window); `UnregisterShell` → `ShellRegistration.Unregister()` then exit; `Main` → main window as today.
- `Program` redirect: the running copy's `Activated` handler reads `AppActivationArguments.Data` (`ILaunchActivatedEventArgs.Arguments`) → `LaunchRequest.ParseCommandLine` → same routing on the UI thread. A `Main` request brings (or creates) the main window.
- `CompressDialog(LaunchRequest)`: XAML title bar "Kipo Video Compressor" + icon, Mica, theme-aware window buttons, `WindowFitter`. Preset → `QueueView` with `queue.AddPaths(paths, mb * 1 MB)` immediately. Custom → form (`CustomDialogSizeBox` NumberBox pre-filled from settings, `CustomDialogCompressButton` "Compress", Enter submits) → saves `CustomMb`, drills to the list. `AddPaths(IReadOnlyList<string>)` for batched joins. Clipboard copy + "Copied to clipboard" banner on single-file batches when enabled.
- Closing: `AppWindow.Closing` → if `queue.CanCancelAny`, cancel close and show `ContentDialog` "Stop compressing and close?" (Primary "Stop", Close "Keep going"); Stop → `CancelAll`, `await WhenIdle()`, close.

- [ ] **Step 1: Failing UI tests** (launch `Kipo.exe` with args via a new `LaunchWithArgs(params string[])` helper):
  1. `--compress 25 <tiny>` → dialog titled "Kipo Video Compressor" with one row reaching "Already under target"; no main window (`DropZone` absent).
  2. `--compress 1`… not allowed (1 isn't a preset) → main window opens instead.
  3. `--compress custom <small>` → `CustomDialogSizeBox` shown; type 1, press Enter → row reaches "Done"; settings file now has `CustomMb` 1.
  4. Three separate launches of `--compress 25 <file>` 100 ms apart → one dialog with 3 rows.
  5. `--compress 8 <large>` then close the dialog while compressing → prompt with "Stop compressing and close?"; click "Stop" → dialog closes, no `.part` left in the temp folder.
  6. Open file / Open in folder present on the finished row (reuse existing assertions).
- [ ] **Step 2:** RED. **Step 3:** implement. **Step 4:** GREEN + full suite.
- [ ] **Step 5:** commit `feat: compress dialog for right-click launches`.

---

### Task 7: MSIX registration + real Explorer test

**Files:** Modify `src/Kipo/Package.appxmanifest`, `src/Kipo/Kipo.csproj`, `build.ps1`; Test `tests/Kipo.Tests/ExplorerMenuTests.cs`

- Manifest: add `desktop4`, `desktop5`, `com` namespaces; under `<Application>` add `<Extensions>` with `desktop4:Extension Category="windows.fileExplorerContextMenus"` listing a `desktop5:ItemType` per extension, each with `desktop5:Verb Id="KipoCompress" Clsid="6B1F4A52-…"`; and `com:Extension Category="windows.comServer"` → `com:SurrogateServer DisplayName="Kipo Video Compressor"` → `com:Class Id="6B1F4A52-…" Path="Kipo.Shell.dll" ThreadingModel="STA"`.
- `Kipo.csproj`: include `$(SolutionDir)artifacts\shell\$(RuntimeIdentifier)\Kipo.Shell.dll` as Content (copy to output + publish) when it exists.
- `build.ps1`: before the app builds, `dotnet publish src\Kipo.Shell -c Release -r $rid -o artifacts\shell\$rid`.
- [ ] **Step 1: Failing E2E test** (`[TestCategory("Shell")]`, Inconclusive unless the installed Kipo MSIX contains `Kipo.Shell.dll`): open Explorer on a temp folder holding the Tiny sample, right-click the file, find menu item "Compress with Kipo" in the Windows 11 menu, open it, assert the 6 item titles in order, click "25 MB", find a Kipo window titled "Kipo Video Compressor" with a row reaching "Already under target", close it and Explorer.
- [ ] **Step 2:** RED against the currently installed MSIX (no menu). **Step 3:** implement, `build.ps1`, reinstall MSIX. **Step 4:** GREEN.
- [ ] **Step 5:** commit `feat: register right-click menu in MSIX`.

---

### Task 8: MSI registration (classic menu + sparse package)

**Files:** Create `src/Kipo/ShellRegistration.cs`, `src/Kipo/app.unpackaged.manifest`, `src/Kipo.Installer/Sparse/AppxManifest.xml`; Modify `Kipo.csproj`, `Package.wxs`, `build.ps1`; Test `tests/Kipo.Tests/ShellRegistrationTests.cs`

- **Classic menu (WiX):** for each extension, `HKLM\Software\Classes\SystemFileAssociations\<ext>\shell\Kipo` with `MUIVerb="Compress with Kipo"`, `Icon="[INSTALLFOLDER]Kipo.exe,0"`, `ExtendedSubCommandsKey="Kipo.CompressMenu"`, `MultiSelectModel="Document"`. `HKLM\Software\Classes\Kipo.CompressMenu\shell\01_8`…`06_custom`, each `MUIVerb` = item title, `command` = `"[INSTALLFOLDER]Kipo.exe" --compress <size> "%1"`, and `CommandFlags=0x20` (separator before) on Custom.
- **Sparse package:** `Sparse/AppxManifest.xml` with Identity `KipoSparse` / `CN=Kipo Dev`, `uap10:AllowExternalContent`, `Application Executable="Kipo.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App"`, and the same `fileExplorerContextMenus` + `comServer` extensions as Task 7. `app.unpackaged.manifest` = current app.manifest + `<msix xmlns="urn:schemas-microsoft-com:msix.v1" publisher="CN=Kipo Dev" packageName="KipoSparse" applicationId="Kipo" />`, used only for `KipoPackaging=Unpackaged`.
- **build.ps1:** for the MSI publish, `makeappx pack /d <temp> /p Kipo.Sparse.msix /nv` + `signtool sign /fd SHA256 /sha1 <thumbprint>`, output into the publish folder.
- **ShellRegistration** (unpackaged only, called on every main-window/dialog start, cheap no-op when done): if `KipoSparse` isn't registered for this user, `PackageManager.AddPackageByUriAsync(sparse, new AddPackageOptions { ExternalLocationUri = installFolder })`; on success write `HKCU\Software\Classes\SystemFileAssociations\<ext>\shell\Kipo` value `LegacyDisable=""` for each extension; on failure log once (marker file in `%LOCALAPPDATA%\Kipo`) and leave the classic menu alone. `Unregister()` removes the package and the HKCU keys. Package manager and registry root are injected so tests use fakes / `HKCU\Software\KipoTests`.
- **MSI uninstall:** `CustomAction` running `"[INSTALLFOLDER]Kipo.exe" --unregister-shell`, `Execute="immediate"`, `Return="ignore"`, scheduled `Before="RemoveFiles"` with `REMOVE="ALL"`.
- [ ] **Step 1: Failing tests** (`ShellRegistrationTests`, fakes): success path writes `LegacyDisable` for all 6 extensions under the test root; failure path writes nothing and logs once (second call doesn't log again); already-registered path does nothing; `Unregister` removes package + keys.
- [ ] **Step 2–4:** RED, implement, GREEN; `build.ps1` produces `Kipo.Sparse.msix` inside the MSI payload (admin-extract check like before).
- [ ] **Step 5:** commit `feat: register right-click menu for MSI installs`.

---

### Task 9: Certificate notice (README + build output)

**Files:** Create `README.md`; Modify `build.ps1`

`README.md` (second person, per documentation style): `# Kipo Video Compressor` → one-line description → `## Introduction` → `### Prerequisites` (.NET 10 SDK, Visual Studio C++ build tools for x64/ARM64) → `## Building` (`pwsh -File build.ps1`) → `## Installing` (MSIX and MSI steps) → `## Right-click menu` containing:

> **You need the Kipo signing certificate trusted for the right-click menu to work.** The MSIX won't install without it, and the MSI's Windows 11 main-menu entry needs it too (the MSI's "Show more options" entry works without it). Trust it once, as admin:
>
> ```powershell
> Import-Certificate .certs\kipo-dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
> ```
>
> Release builds should be signed with a real code-signing certificate instead.

`build.ps1` ends by printing the same notice (plain text, 3 lines) after "Done. Packages are in dist".

- [ ] **Step 1:** Write README and build message. **Step 2:** run `build.ps1 -Platforms x64`, confirm the notice prints. **Step 3:** commit `docs: README with right-click menu certificate notice`.

---

### Task 10: Final verification

- [ ] `dotnet build Kipo.slnx` → 0 warnings, 0 errors.
- [ ] Full test suite (unit, Video, UI, Shell) → green.
- [ ] `build.ps1` both platforms; reinstall MSIX; manual right-click check in Explorer (main menu and "Show more options").
- [ ] Whole-branch review (superpowers:requesting-code-review), fix Critical/Important, commit.
