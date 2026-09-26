# Kipo Video Compressor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Kipo, a tiny native Windows app that compresses videos to a target size with the built-in `MediaTranscoder`, shipped as MSIX and MSI.

**Architecture:** All logic lives in `Kipo.Core`, a plain class library that uses Windows Runtime APIs but not WinUI, so it's testable without a window. `Kipo` is a thin WinUI 3 app on top of it. `Kipo.Installer` (WiX) wraps the unpackaged self-contained build into an MSI; the same app project produces the MSIX when built with `KipoPackaging=Msix`.

**Tech Stack:** C# / .NET 10, WinAppSDK 2.5.1 (latest stable 2.x), `Windows.Media.Transcoding`, MSTest 4.4.1, FlaUI.UIA3 5.0.0, WiX Toolset SDK 5.0.2.

**Spec:** `docs/superpowers/specs/2026-09-26-kipo-video-compressor-design.md`

## Global Constraints

- Target framework: `net10.0-windows10.0.26100.0`, minimum OS `10.0.19041.0`.
- Approved dependencies only: `Microsoft.WindowsAppSDK`, WiX Toolset, MSTest, FlaUI. Anything else must be flagged and approved first.
- WiX pinned to 5.0.2 (MS-RL). WiX 6+ uses the OSMF EULA (maintenance fee for revenue-generating orgs); don't upgrade without approval.
- Nullable on, analyzers `latest-recommended`, warnings as errors, code style enforced in build.
- Presets: 8, 10, 25, 50, 100 MB, then "Custom" last. Custom range 1–10000 MB.
- Supported extensions: `.mp4`, `.mov`, `.mkv`, `.avi`, `.wmv`, `.m4v`.
- Output: `name-compressed.mp4` next to the source; collisions `name-compressed (2).mp4`; temp `name-compressed.part`.
- Budget = target bytes × 8 × 0.95. Audio AAC 128 kbps, 64 kbps when tight. Tiers: source, 1080, 720, 480, 360 (short side).
- Settings and log live in `%LOCALAPPDATA%\Kipo` (`KIPO_DATA_DIR` env var overrides it, used by UI tests).
- Sample videos: `KIPO_TEST_VIDEOS` env var, default `C:\Users\artistro08\Videos\Screen Recordings`. Tests copy to temp first and never write into the sample folder. Missing files make video tests Inconclusive.
- Status text exactly: `Waiting`, `Compressing N%`, `Done`, `Done (over target by X MB)`, `Already under target`, `Failed: reason`, `Canceled`.
- US English everywhere. 4-space indent in C#/XAML, 2-space in XML project files is fine.
- Commit after each task once its tests pass. Never push.

## Review Focus

1. Source with no audio track (common for screen recordings) — encodes video only, no crash. Test in Task 4 (`Plan_NoAudio_UsesWholeBudgetForVideo`); Task 9 sets `profile.Audio = null` for it.
2. Odd source dimensions (1366×768, 1919×1079) — output sizes are even, aspect kept. Test in Task 4.
3. Source deleted or moved after it was queued — row shows `Failed: not found`, no `.part` left, queue keeps going. Test in Task 9.
4. Same file added twice — both run; second output gets `(2)`. Test in Task 9.
5. Paste with nothing usable (image or HTML only on clipboard, empty text) — nothing is added, no crash. Test in Task 8.

---

## File Structure

```
Kipo.slnx
Directory.Build.props              shared compiler/analyzer settings
.gitignore
build.ps1                          builds MSIX + MSI into dist\
src/Kipo.Core/
  Kipo.Core.csproj
  KipoPaths.cs                     data folder, settings/log paths
  PathText.cs                      split pasted text into paths
  PathValidator.cs                 validate one input path
  OutputNamer.cs                   output and .part file names
  EncodePlanner.cs                 VideoInfo, EncodePlan, bitrate/downscale math
  AppSettings.cs                   settings record + SettingsStore + JSON context
  Log.cs                           small rolling log file
  QueueItem.cs                     one row: status, progress, cancel
  CompressQueue.cs                 sequential queue, batches, ShouldCopy
  ClipboardInput.cs                read paths from DataPackageView, copy file to clipboard
  Compressor.cs                    MediaTranscoder wrapper
src/Kipo/
  Kipo.csproj, app.manifest, Package.appxmanifest, Assets/*.png
  App.xaml(.cs)                    startup, crash handling
  MainWindow.xaml(.cs)             the whole UI
src/Kipo.Installer/
  Kipo.Installer.wixproj, Package.wxs
tests/Kipo.Tests/
  Kipo.Tests.csproj, .editorconfig
  TestVideos.cs                    sample video helper
  PathTextTests.cs, PathValidatorTests.cs, OutputNamerTests.cs,
  EncodePlannerTests.cs, SettingsTests.cs, LogTests.cs,
  CompressQueueTests.cs, ClipboardInputTests.cs, CompressorTests.cs,
  UiTests.cs
```

---

### Task 1: Solution scaffold + PathText

**Files:**
- Create: `Kipo.slnx`, `Directory.Build.props`, `.gitignore`
- Create: `src/Kipo.Core/Kipo.Core.csproj`, `src/Kipo.Core/PathText.cs`
- Create: `tests/Kipo.Tests/Kipo.Tests.csproj`, `tests/Kipo.Tests/.editorconfig`, `tests/Kipo.Tests/PathTextTests.cs`

**Interfaces:**
- Produces: `Kipo.Core.PathText.Parse(string? text) -> IReadOnlyList<string>`

- [ ] **Step 1: Shared build files**

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Authors>Devin Green (Artistro08)</Authors>
    <Product>Kipo</Product>
  </PropertyGroup>

  <!-- MSIX builds of the app use their own bin/obj so they never mix with unpackaged builds -->
  <PropertyGroup Condition="'$(MSBuildProjectName)' == 'Kipo' and '$(KipoPackaging)' == 'Msix'">
    <BaseIntermediateOutputPath>obj\msix\</BaseIntermediateOutputPath>
    <BaseOutputPath>bin\msix\</BaseOutputPath>
  </PropertyGroup>
</Project>
```

`.gitignore`:
```
bin/
obj/
artifacts/
dist/
.certs/
.vs/
*.user
TestResults/
```

`src/Kipo.Core/Kipo.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <RootNamespace>Kipo.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Kipo.Tests" />
  </ItemGroup>
</Project>
```

`tests/Kipo.Tests/Kipo.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MSTest" Version="4.4.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Kipo.Core\Kipo.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" />
  </ItemGroup>
</Project>
```

`tests/Kipo.Tests/.editorconfig`:
```ini
[*.cs]
# Test names use Method_Scenario_Result underscores
dotnet_diagnostic.CA1707.severity = none
```

Create the solution:
```bash
dotnet new sln --format slnx -n Kipo
dotnet sln Kipo.slnx add src/Kipo.Core/Kipo.Core.csproj tests/Kipo.Tests/Kipo.Tests.csproj
```

- [ ] **Step 2: Write the failing test**

`tests/Kipo.Tests/PathTextTests.cs`:
```csharp
using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class PathTextTests
{
    [TestMethod]
    public void Parse_Null_ReturnsEmpty()
    {
        Assert.AreEqual(0, PathText.Parse(null).Count);
    }

    [TestMethod]
    public void Parse_Whitespace_ReturnsEmpty()
    {
        Assert.AreEqual(0, PathText.Parse("  \r\n \n").Count);
    }

    [TestMethod]
    public void Parse_SinglePath_ReturnsIt()
    {
        CollectionAssert.AreEqual(new[] { @"C:\a.mp4" }, PathText.Parse(@"C:\a.mp4").ToArray());
    }

    [TestMethod]
    public void Parse_QuotedPath_StripsQuotes()
    {
        CollectionAssert.AreEqual(new[] { @"C:\my videos\a.mp4" }, PathText.Parse("\"C:\\my videos\\a.mp4\"").ToArray());
    }

    [TestMethod]
    public void Parse_MultipleLinesAndBlanks_ReturnsEachPath()
    {
        var text = "C:\\a.mp4\r\n\r\n  \"C:\\b.mov\"  \nC:\\c.mkv\n";

        CollectionAssert.AreEqual(new[] { @"C:\a.mp4", @"C:\b.mov", @"C:\c.mkv" }, PathText.Parse(text).ToArray());
    }

    [TestMethod]
    public void Parse_OnlyQuotes_Skipped()
    {
        Assert.AreEqual(0, PathText.Parse("\"\"").Count);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Kipo.Tests`
Expected: build FAIL, `The name 'PathText' does not exist`.

- [ ] **Step 4: Implement**

`src/Kipo.Core/PathText.cs`:
```csharp
namespace Kipo.Core;

/// <summary>
/// Turns pasted text into a list of candidate file paths.
/// </summary>
/// <remarks>
/// Pasted text can hold one path or many, one per line, often wrapped in quotes
/// (Explorer's "Copy as path" adds them). Blank lines are dropped. No validation
/// happens here; see <see cref="PathValidator"/>.
/// </remarks>
public static class PathText
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>
    /// Splits text into trimmed, unquoted, non-empty lines.
    /// </summary>
    /// <param name="text">Raw clipboard text. May be null.</param>
    /// <returns>Candidate paths in the order they appeared.</returns>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('"').Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Kipo.Tests`
Expected: 6 passed. Fix any analyzer errors the build reports (they are errors by design).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: scaffold solution and parse pasted path text"
```

---

### Task 2: PathValidator

**Files:**
- Create: `src/Kipo.Core/PathValidator.cs`
- Test: `tests/Kipo.Tests/PathValidatorTests.cs`

**Interfaces:**
- Produces:
  - `PathValidator.VideoExtensions : IReadOnlyList<string>` (`.mp4 .mov .mkv .avi .wmv .m4v`)
  - `PathValidator.Validate(string path) -> string?` — null when OK, otherwise one of the `InputErrors` constants
  - `InputErrors.UnsupportedPath = "unsupported path"`, `InputErrors.NotAVideo = "not a video"`, `InputErrors.NotFound = "not found"`

- [ ] **Step 1: Write the failing test**

`tests/Kipo.Tests/PathValidatorTests.cs`:
```csharp
using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class PathValidatorTests
{
    private static string _dir = "";

    [ClassInitialize]
    public static void Setup(TestContext _)
    {
        _dir = Directory.CreateTempSubdirectory("kipo-validator-").FullName;
        File.WriteAllBytes(Path.Combine(_dir, "clip.mp4"), [0]);
        File.WriteAllBytes(Path.Combine(_dir, "CLIP2.MOV"), [0]);
        File.WriteAllBytes(Path.Combine(_dir, "notes.txt"), [0]);
    }

    [ClassCleanup]
    public static void Cleanup() => Directory.Delete(_dir, true);

    [TestMethod]
    public void Validate_ExistingVideo_ReturnsNull()
    {
        Assert.IsNull(PathValidator.Validate(Path.Combine(_dir, "clip.mp4")));
    }

    [TestMethod]
    public void Validate_UppercaseExtension_ReturnsNull()
    {
        Assert.IsNull(PathValidator.Validate(Path.Combine(_dir, "CLIP2.MOV")));
    }

    [TestMethod]
    public void Validate_MissingVideo_ReturnsNotFound()
    {
        Assert.AreEqual(InputErrors.NotFound, PathValidator.Validate(Path.Combine(_dir, "gone.mp4")));
    }

    [TestMethod]
    public void Validate_NonVideo_ReturnsNotAVideo()
    {
        Assert.AreEqual(InputErrors.NotAVideo, PathValidator.Validate(Path.Combine(_dir, "notes.txt")));
    }

    [TestMethod]
    public void Validate_Folder_ReturnsNotAVideo()
    {
        Assert.AreEqual(InputErrors.NotAVideo, PathValidator.Validate(_dir));
    }

    [DataTestMethod]
    [DataRow(@"clip.mp4")]
    [DataRow(@"..\clip.mp4")]
    [DataRow(@"\clip.mp4")]
    [DataRow(@"C:clip.mp4")]
    [DataRow(@"\\server\share\clip.mp4")]
    [DataRow(@"//server/share/clip.mp4")]
    [DataRow(@"\\?\C:\clip.mp4")]
    [DataRow(@"\\.\C:\clip.mp4")]
    [DataRow(@"https://example.com/clip.mp4")]
    [DataRow(@"file:///C:/clip.mp4")]
    [DataRow(@"C:\clip.mp4:hidden.mp4")]
    [DataRow("C:\\cl\0ip.mp4")]
    [DataRow("")]
    public void Validate_UnsupportedPath_ReturnsUnsupported(string path)
    {
        Assert.AreEqual(InputErrors.UnsupportedPath, PathValidator.Validate(path));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Kipo.Tests --filter PathValidatorTests`
Expected: build FAIL, `PathValidator` not found.

- [ ] **Step 3: Implement**

`src/Kipo.Core/PathValidator.cs`:
```csharp
namespace Kipo.Core;

/// <summary>
/// Reasons an input path is rejected. Shown after "Failed: " in the queue.
/// </summary>
public static class InputErrors
{
    public const string UnsupportedPath = "unsupported path";
    public const string NotAVideo       = "not a video";
    public const string NotFound        = "not found";
}

/// <summary>
/// Checks that a path from outside the app is a local video file we're willing to open.
/// </summary>
/// <remarks>
/// Every path from the clipboard, a drop, or the picker passes through here before
/// Kipo touches the file. Only plain drive-letter paths are allowed. URLs, relative
/// paths, network (UNC) paths, device paths (<c>\\?\</c>, <c>\\.\</c>) and NTFS
/// alternate data streams (<c>file.mp4:stream</c>) are rejected.
/// </remarks>
public static class PathValidator
{
    /// <summary>File extensions Kipo accepts, lower case with dot.</summary>
    public static IReadOnlyList<string> VideoExtensions { get; } = [".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v"];

    /// <summary>
    /// Validates one path.
    /// </summary>
    /// <param name="path">Candidate path.</param>
    /// <returns>Null when the path is usable, otherwise an <see cref="InputErrors"/> value.</returns>
    public static string? Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!IsPlainLocalPath(path))
        {
            return InputErrors.UnsupportedPath;
        }

        if (!VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            return InputErrors.NotAVideo;
        }

        return File.Exists(path) ? null : InputErrors.NotFound;
    }

    // Drive letter, colon, slash; no other colons; no invalid characters
    private static bool IsPlainLocalPath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
        {
            return false;
        }

        if (path.IndexOf(':', 2) >= 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return false;
        }

        return Path.IsPathFullyQualified(path);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Kipo.Tests --filter PathValidatorTests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: validate input paths"
```

---

### Task 3: OutputNamer

**Files:**
- Create: `src/Kipo.Core/OutputNamer.cs`
- Test: `tests/Kipo.Tests/OutputNamerTests.cs`

**Interfaces:**
- Produces:
  - `OutputNamer.GetOutputPath(string sourcePath, Func<string, bool> exists) -> string`
  - `OutputNamer.GetPartPath(string sourcePath) -> string`

- [ ] **Step 1: Write the failing test**

`tests/Kipo.Tests/OutputNamerTests.cs`:
```csharp
using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class OutputNamerTests
{
    [TestMethod]
    public void GetOutputPath_NoCollision_AddsCompressedSuffix()
    {
        Assert.AreEqual(@"C:\v\clip-compressed.mp4", OutputNamer.GetOutputPath(@"C:\v\clip.mov", _ => false));
    }

    [TestMethod]
    public void GetOutputPath_OneCollision_Adds2()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\v\clip-compressed.mp4" };

        Assert.AreEqual(@"C:\v\clip-compressed (2).mp4", OutputNamer.GetOutputPath(@"C:\v\clip.mp4", taken.Contains));
    }

    [TestMethod]
    public void GetOutputPath_TwoCollisions_Adds3()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\v\clip-compressed.mp4",
            @"C:\v\clip-compressed (2).mp4",
        };

        Assert.AreEqual(@"C:\v\clip-compressed (3).mp4", OutputNamer.GetOutputPath(@"C:\v\clip.mp4", taken.Contains));
    }

    [TestMethod]
    public void GetOutputPath_NameWithDots_KeepsInnerDots()
    {
        Assert.AreEqual(@"C:\v\a.b.c-compressed.mp4", OutputNamer.GetOutputPath(@"C:\v\a.b.c.mkv", _ => false));
    }

    [TestMethod]
    public void GetPartPath_UsesPartExtension()
    {
        Assert.AreEqual(@"C:\v\clip-compressed.part", OutputNamer.GetPartPath(@"C:\v\clip.mp4"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Kipo.Tests --filter OutputNamerTests`
Expected: build FAIL, `OutputNamer` not found.

- [ ] **Step 3: Implement**

`src/Kipo.Core/OutputNamer.cs`:
```csharp
namespace Kipo.Core;

/// <summary>
/// Picks file names for compressed output next to the source.
/// </summary>
/// <remarks>
/// Output is always MP4: <c>name-compressed.mp4</c>, then <c>name-compressed (2).mp4</c>
/// and up when a name is taken. While encoding, Kipo writes to <c>name-compressed.part</c>
/// and renames it at the end so a half-written file never looks finished.
/// </remarks>
public static class OutputNamer
{
    /// <summary>
    /// Returns the first free output path for a source file.
    /// </summary>
    /// <param name="sourcePath">Full path of the source video.</param>
    /// <param name="exists">Returns true when a path is already taken. Pass <see cref="File.Exists"/> in real code.</param>
    /// <returns>Full output path.</returns>
    public static string GetOutputPath(string sourcePath, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var (folder, name) = Split(sourcePath);
        var candidate      = Path.Combine(folder, $"{name}-compressed.mp4");

        for (var number = 2; exists(candidate); number++)
        {
            candidate = Path.Combine(folder, $"{name}-compressed ({number}).mp4");
        }

        return candidate;
    }

    /// <summary>
    /// Returns the temp path used while encoding.
    /// </summary>
    /// <param name="sourcePath">Full path of the source video.</param>
    /// <returns>Full <c>.part</c> path.</returns>
    public static string GetPartPath(string sourcePath)
    {
        var (folder, name) = Split(sourcePath);

        return Path.Combine(folder, $"{name}-compressed.part");
    }

    private static (string Folder, string Name) Split(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);

        var folder = Path.GetDirectoryName(sourcePath) ?? throw new ArgumentException("Path has no folder.", nameof(sourcePath));

        return (folder, Path.GetFileNameWithoutExtension(sourcePath));
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Kipo.Tests --filter OutputNamerTests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: name compressed output files"
```

---

### Task 4: EncodePlanner

**Files:**
- Create: `src/Kipo.Core/EncodePlanner.cs`
- Test: `tests/Kipo.Tests/EncodePlannerTests.cs`

**Interfaces:**
- Produces:
  - `record VideoInfo(TimeSpan Duration, int Width, int Height, double FrameRate, bool HasAudio)`
  - `enum PlanKind { Encode, Skip, Fail }`
  - `record EncodePlan(PlanKind Kind, int Width, int Height, uint VideoBitrate, uint AudioBitrate, string? Error)` with statics `EncodePlan.Skip`, `EncodePlan.Failed(string error)`
  - `EncodePlanner.Plan(VideoInfo info, long sourceBytes, long targetBytes) -> EncodePlan`
  - `EncodePlanner.ScaleToShortSide(int width, int height, int shortSide) -> (int Width, int Height)`
  - `EncodePlanner.RetryBitrate(uint videoBitrate, long targetBytes, long actualBytes) -> uint`
  - `PlanErrors.TooSmall = "target too small for this video length"`, `PlanErrors.CantRead = "can't read video"`, `PlanErrors.InvalidTarget = "invalid target size"`
  - Constants: `BudgetFactor = 0.95`, `AudioBitrateHigh = 128_000`, `AudioBitrateLow = 64_000`, `TierBitsPerPixel = 0.04`, `MinBitsPerPixel = 0.015`, `DefaultFrameRate = 30`

Quality floor note: a tier is picked when bits-per-pixel ≥ 0.04. If even 360p is under 0.04, Kipo still encodes at the smallest size as long as it's ≥ 0.015; below that it fails. This follows the spec ("fail when below the floor at 360p") with the floor at 360p being the hard minimum.

- [ ] **Step 1: Write the failing test**

`tests/Kipo.Tests/EncodePlannerTests.cs`:
```csharp
using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class EncodePlannerTests
{
    private const long MB = 1_048_576;

    private static VideoInfo Info(double seconds, int width = 1920, int height = 1080, double fps = 30, bool audio = true) =>
        new(TimeSpan.FromSeconds(seconds), width, height, fps, audio);

    [TestMethod]
    public void Plan_SourceUnderTarget_Skips()
    {
        Assert.AreEqual(PlanKind.Skip, EncodePlanner.Plan(Info(60), 5 * MB, 10 * MB).Kind);
    }

    [TestMethod]
    public void Plan_SourceEqualsTarget_Skips()
    {
        Assert.AreEqual(PlanKind.Skip, EncodePlanner.Plan(Info(60), 10 * MB, 10 * MB).Kind);
    }

    [TestMethod]
    public void Plan_ZeroTarget_FailsInvalidTarget()
    {
        Assert.AreEqual(PlanErrors.InvalidTarget, EncodePlanner.Plan(Info(60), 5 * MB, 0).Error);
    }

    [TestMethod]
    public void Plan_ZeroDuration_FailsCantRead()
    {
        Assert.AreEqual(PlanErrors.CantRead, EncodePlanner.Plan(Info(0), 50 * MB, 10 * MB).Error);
    }

    [TestMethod]
    public void Plan_ZeroWidth_FailsCantRead()
    {
        Assert.AreEqual(PlanErrors.CantRead, EncodePlanner.Plan(Info(60, width: 0), 50 * MB, 10 * MB).Error);
    }

    [TestMethod]
    public void Plan_1080p60sTo10MB_DownscalesTo720p()
    {
        // Budget 79,691,776 bits / 60 s = 1,328,196 bps, minus 128k audio = 1,200,196
        var plan = EncodePlanner.Plan(Info(60), 100 * MB, 10 * MB);

        Assert.AreEqual(PlanKind.Encode, plan.Kind);
        Assert.AreEqual((1280, 720), (plan.Width, plan.Height));
        Assert.AreEqual(1_200_196u, plan.VideoBitrate);
        Assert.AreEqual(128_000u, plan.AudioBitrate);
    }

    [TestMethod]
    public void Plan_PlentyOfBudget_KeepsSourceSize()
    {
        var plan = EncodePlanner.Plan(Info(10), 100 * MB, 50 * MB);

        Assert.AreEqual((1920, 1080), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_TightBudget_DropsAudioTo64k()
    {
        // 381,300 bps total: 253,300 at 128k is under the 360p tier, 317,300 at 64k clears it
        var plan = EncodePlanner.Plan(Info(209), 100 * MB, 10 * MB);

        Assert.AreEqual(PlanKind.Encode, plan.Kind);
        Assert.AreEqual(64_000u, plan.AudioBitrate);
        Assert.AreEqual((640, 360), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_Under360pTierButAboveMinimum_EncodesAt360p()
    {
        var plan = EncodePlanner.Plan(Info(372), 100 * MB, 10 * MB);

        Assert.AreEqual(PlanKind.Encode, plan.Kind);
        Assert.AreEqual((640, 360), (plan.Width, plan.Height));
        Assert.AreEqual(64_000u, plan.AudioBitrate);
    }

    [TestMethod]
    public void Plan_HourLongTo8MB_FailsTooSmall()
    {
        var plan = EncodePlanner.Plan(Info(3600), 500 * MB, 8 * MB);

        Assert.AreEqual(PlanKind.Fail, plan.Kind);
        Assert.AreEqual(PlanErrors.TooSmall, plan.Error);
    }

    [TestMethod]
    public void Plan_NoAudio_UsesWholeBudgetForVideo()
    {
        var plan = EncodePlanner.Plan(Info(60, audio: false), 100 * MB, 10 * MB);

        Assert.AreEqual(0u, plan.AudioBitrate);
        Assert.AreEqual(1_328_196u, plan.VideoBitrate);
        Assert.AreEqual((1280, 720), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_Portrait_ScalesShortSide()
    {
        var plan = EncodePlanner.Plan(Info(60, width: 1080, height: 1920), 100 * MB, 10 * MB);

        Assert.AreEqual((720, 1280), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_UnknownFrameRate_Assumes30()
    {
        var plan = EncodePlanner.Plan(Info(60, fps: 0), 100 * MB, 10 * MB);

        Assert.AreEqual((1280, 720), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_OddSourceSize_RoundsToEven()
    {
        var plan = EncodePlanner.Plan(Info(10, width: 1919, height: 1079), 100 * MB, 90 * MB);

        Assert.AreEqual((1918, 1078), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void Plan_SmallSource_NeverUpscales()
    {
        var plan = EncodePlanner.Plan(Info(10, width: 320, height: 240), 100 * MB, 50 * MB);

        Assert.AreEqual((320, 240), (plan.Width, plan.Height));
    }

    [TestMethod]
    public void ScaleToShortSide_1366x768To480_KeepsAspectAndEven()
    {
        Assert.AreEqual((852, 480), EncodePlanner.ScaleToShortSide(1366, 768, 480));
    }

    [TestMethod]
    public void ScaleToShortSide_1080pTo720_Is1280x720()
    {
        Assert.AreEqual((1280, 720), EncodePlanner.ScaleToShortSide(1920, 1080, 720));
    }

    [TestMethod]
    public void ScaleToShortSide_AlreadySmaller_ReturnsEvenSource()
    {
        Assert.AreEqual((640, 358), EncodePlanner.ScaleToShortSide(641, 359, 480));
    }

    [TestMethod]
    public void RetryBitrate_ScalesByTargetOverActual()
    {
        Assert.AreEqual(833_333u, EncodePlanner.RetryBitrate(1_000_000, 10 * MB, 12 * MB));
    }

    [TestMethod]
    public void RetryBitrate_NeverZero()
    {
        Assert.AreEqual(1u, EncodePlanner.RetryBitrate(1, 1, long.MaxValue));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Kipo.Tests --filter EncodePlannerTests`
Expected: build FAIL, `EncodePlanner` not found.

- [ ] **Step 3: Implement**

`src/Kipo.Core/EncodePlanner.cs`:
```csharp
namespace Kipo.Core;

/// <summary>What Kipo knows about a source video before encoding.</summary>
/// <param name="Duration">Play length.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="FrameRate">Frames per second. Zero or less means unknown.</param>
/// <param name="HasAudio">True when the source has an audio track.</param>
public sealed record VideoInfo(TimeSpan Duration, int Width, int Height, double FrameRate, bool HasAudio);

/// <summary>What to do with a source file.</summary>
public enum PlanKind
{
    Encode,
    Skip,
    Fail,
}

/// <summary>Reasons a plan fails. Shown after "Failed: " in the queue.</summary>
public static class PlanErrors
{
    public const string TooSmall      = "target too small for this video length";
    public const string CantRead      = "can't read video";
    public const string InvalidTarget = "invalid target size";
}

/// <summary>
/// The encode settings for one file, or the reason there's nothing to encode.
/// </summary>
/// <param name="Kind">Encode, skip, or fail.</param>
/// <param name="Width">Output width (even). Zero unless encoding.</param>
/// <param name="Height">Output height (even). Zero unless encoding.</param>
/// <param name="VideoBitrate">Video bits per second.</param>
/// <param name="AudioBitrate">Audio bits per second. Zero when the source has no audio.</param>
/// <param name="Error">Failure reason when <paramref name="Kind"/> is <see cref="PlanKind.Fail"/>.</param>
public sealed record EncodePlan(PlanKind Kind, int Width, int Height, uint VideoBitrate, uint AudioBitrate, string? Error)
{
    /// <summary>Source is already under the target.</summary>
    public static EncodePlan Skip { get; } = new(PlanKind.Skip, 0, 0, 0, 0, null);

    /// <summary>Creates a failed plan.</summary>
    /// <param name="error">A <see cref="PlanErrors"/> value.</param>
    /// <returns>The plan.</returns>
    public static EncodePlan Failed(string error) => new(PlanKind.Fail, 0, 0, 0, 0, error);
}

/// <summary>
/// Works out resolution and bitrates that fit a video into a target size.
/// </summary>
/// <remarks>
/// Budget is target bytes × 8 × 0.95, leaving 5% for MP4 overhead. Audio gets 128 kbps,
/// or 64 kbps when that would starve the video. The rest goes to video. Resolution
/// steps down through 1080p, 720p, 480p and 360p (measured on the short side, so
/// portrait works) until bits-per-pixel reaches <see cref="TierBitsPerPixel"/>. Never
/// upscales. If 360p is still under the tier but above <see cref="MinBitsPerPixel"/>
/// it encodes there anyway; below that it fails.
/// </remarks>
public static class EncodePlanner
{
    public const double BudgetFactor     = 0.95;
    public const uint   AudioBitrateHigh = 128_000;
    public const uint   AudioBitrateLow  = 64_000;
    public const double TierBitsPerPixel = 0.04;
    public const double MinBitsPerPixel  = 0.015;
    public const double DefaultFrameRate = 30;

    private static readonly int[] Tiers = [1080, 720, 480, 360];

    /// <summary>
    /// Plans the encode for one file.
    /// </summary>
    /// <param name="info">Source video facts.</param>
    /// <param name="sourceBytes">Source file size.</param>
    /// <param name="targetBytes">Target output size.</param>
    /// <returns>The plan.</returns>
    public static EncodePlan Plan(VideoInfo info, long sourceBytes, long targetBytes)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (targetBytes <= 0)
        {
            return EncodePlan.Failed(PlanErrors.InvalidTarget);
        }

        if (sourceBytes <= targetBytes)
        {
            return EncodePlan.Skip;
        }

        if (info.Duration <= TimeSpan.Zero || info.Width <= 0 || info.Height <= 0)
        {
            return EncodePlan.Failed(PlanErrors.CantRead);
        }

        // Budget
        var frameRate     = info.FrameRate > 0 ? info.FrameRate : DefaultFrameRate;
        var bitsPerSecond = targetBytes * 8.0 * BudgetFactor / info.Duration.TotalSeconds;
        var sizes         = CandidateSizes(info.Width, info.Height);
        var smallest      = sizes[^1];

        // Audio
        var audio = info.HasAudio ? AudioBitrateHigh : 0;
        var video = bitsPerSecond - audio;

        if (info.HasAudio && BitsPerPixel(video, smallest, frameRate) < TierBitsPerPixel)
        {
            audio = AudioBitrateLow;
            video = bitsPerSecond - audio;
        }

        // Resolution
        foreach (var size in sizes)
        {
            if (BitsPerPixel(video, size, frameRate) >= TierBitsPerPixel)
            {
                return Encode(size, video, audio);
            }
        }

        return BitsPerPixel(video, smallest, frameRate) >= MinBitsPerPixel
            ? Encode(smallest, video, audio)
            : EncodePlan.Failed(PlanErrors.TooSmall);
    }

    /// <summary>
    /// Scales a frame so its short side is at most <paramref name="shortSide"/>, keeping aspect ratio and even sizes.
    /// </summary>
    /// <param name="width">Source width.</param>
    /// <param name="height">Source height.</param>
    /// <param name="shortSide">Largest allowed short side.</param>
    /// <returns>Even width and height. Never larger than the source.</returns>
    public static (int Width, int Height) ScaleToShortSide(int width, int height, int shortSide)
    {
        var shortest = Math.Min(width, height);

        if (shortest <= shortSide)
        {
            return (Even(width), Even(height));
        }

        var factor = (double)shortSide / shortest;

        return (Even((int)Math.Round(width * factor)), Even((int)Math.Round(height * factor)));
    }

    /// <summary>
    /// Lowers the video bitrate after an encode came out over target.
    /// </summary>
    /// <param name="videoBitrate">Bitrate used for the first try.</param>
    /// <param name="targetBytes">Target size.</param>
    /// <param name="actualBytes">Size the first try produced.</param>
    /// <returns>New bitrate, at least 1.</returns>
    public static uint RetryBitrate(uint videoBitrate, long targetBytes, long actualBytes) =>
        (uint)Math.Max(1, Math.Floor(videoBitrate * ((double)targetBytes / actualBytes)));

    private static List<(int Width, int Height)> CandidateSizes(int width, int height)
    {
        var sizes = new List<(int Width, int Height)> { ScaleToShortSide(width, height, int.MaxValue) };

        foreach (var tier in Tiers.Where(tier => tier < Math.Min(width, height)))
        {
            sizes.Add(ScaleToShortSide(width, height, tier));
        }

        return sizes;
    }

    private static double BitsPerPixel(double videoBitrate, (int Width, int Height) size, double frameRate) =>
        videoBitrate / (size.Width * (double)size.Height * frameRate);

    private static EncodePlan Encode((int Width, int Height) size, double video, uint audio) =>
        new(PlanKind.Encode, size.Width, size.Height, (uint)Math.Floor(video), audio, null);

    private static int Even(int value) => Math.Max(2, value & ~1);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Kipo.Tests --filter EncodePlannerTests`
Expected: all pass. If an expected number is off by one from floating-point rounding, recompute by hand from the formula before changing either side.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: plan bitrate and resolution for target size"
```

---

### Task 5: Settings + data paths

**Files:**
- Create: `src/Kipo.Core/KipoPaths.cs`, `src/Kipo.Core/AppSettings.cs`
- Test: `tests/Kipo.Tests/SettingsTests.cs`

**Interfaces:**
- Produces:
  - `KipoPaths.DataDir : string` (env `KIPO_DATA_DIR` or `%LOCALAPPDATA%\Kipo`), `KipoPaths.SettingsFile`, `KipoPaths.LogFile`
  - `record AppSettings { int PresetMb = 10; bool UseCustom = false; int CustomMb = 20; bool CopyToClipboard = true; }`
  - `AppSettings.Presets : IReadOnlyList<int>` = 8, 10, 25, 50, 100; `AppSettings.MinCustomMb = 1`, `MaxCustomMb = 10000`
  - `AppSettings.TargetBytes : long`, `AppSettings.Normalize() -> AppSettings`
  - `SettingsStore(string filePath)`, `.Load() -> AppSettings`, `.Save(AppSettings settings)`

- [ ] **Step 1: Write the failing test**

`tests/Kipo.Tests/SettingsTests.cs`:
```csharp
using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
public sealed class SettingsTests
{
    private string _dir = "";
    private string _file = "";

    [TestInitialize]
    public void Setup()
    {
        _dir  = Directory.CreateTempSubdirectory("kipo-settings-").FullName;
        _file = Path.Combine(_dir, "sub", "settings.json");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_dir, true);

    [TestMethod]
    public void Load_MissingFile_ReturnsDefaults()
    {
        Assert.AreEqual(new AppSettings(), new SettingsStore(_file).Load());
    }

    [TestMethod]
    public void SaveThenLoad_RoundTrips()
    {
        var store    = new SettingsStore(_file);
        var settings = new AppSettings { PresetMb = 25, UseCustom = true, CustomMb = 7, CopyToClipboard = false };

        store.Save(settings);

        Assert.AreEqual(settings, store.Load());
    }

    [TestMethod]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ not json");

        Assert.AreEqual(new AppSettings(), new SettingsStore(_file).Load());
    }

    [TestMethod]
    public void Load_WrongTypes_ReturnsDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ \"PresetMb\": \"big\" }");

        Assert.AreEqual(new AppSettings(), new SettingsStore(_file).Load());
    }

    [TestMethod]
    public void Normalize_UnknownPreset_ResetsTo10()
    {
        Assert.AreEqual(10, new AppSettings { PresetMb = 33 }.Normalize().PresetMb);
    }

    [TestMethod]
    public void Normalize_CustomZero_ClampsTo1()
    {
        Assert.AreEqual(1, new AppSettings { CustomMb = 0 }.Normalize().CustomMb);
    }

    [TestMethod]
    public void Normalize_CustomHuge_ClampsTo10000()
    {
        Assert.AreEqual(10_000, new AppSettings { CustomMb = int.MaxValue }.Normalize().CustomMb);
    }

    [TestMethod]
    public void Load_OutOfRangeValues_AreNormalized()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ \"PresetMb\": 3, \"CustomMb\": -5 }");

        var settings = new SettingsStore(_file).Load();

        Assert.AreEqual(10, settings.PresetMb);
        Assert.AreEqual(1, settings.CustomMb);
    }

    [TestMethod]
    public void TargetBytes_Preset_UsesPreset()
    {
        Assert.AreEqual(25L * 1_048_576, new AppSettings { PresetMb = 25 }.TargetBytes);
    }

    [TestMethod]
    public void TargetBytes_Custom_UsesCustom()
    {
        Assert.AreEqual(7L * 1_048_576, new AppSettings { UseCustom = true, CustomMb = 7 }.TargetBytes);
    }

    [TestMethod]
    public void DataDir_EnvOverride_IsUsed()
    {
        var before = Environment.GetEnvironmentVariable("KIPO_DATA_DIR");

        try
        {
            Environment.SetEnvironmentVariable("KIPO_DATA_DIR", _dir);

            Assert.AreEqual(_dir, KipoPaths.DataDir);
            Assert.AreEqual(Path.Combine(_dir, "settings.json"), KipoPaths.SettingsFile);
            Assert.AreEqual(Path.Combine(_dir, "log.txt"), KipoPaths.LogFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KIPO_DATA_DIR", before);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Kipo.Tests --filter SettingsTests`
Expected: build FAIL, `AppSettings` not found.

- [ ] **Step 3: Implement**

`src/Kipo.Core/KipoPaths.cs`:
```csharp
namespace Kipo.Core;

/// <summary>
/// Where Kipo keeps its settings and log.
/// </summary>
/// <remarks>
/// Defaults to <c>%LOCALAPPDATA%\Kipo</c>, which works in both the MSIX and MSI builds.
/// The <c>KIPO_DATA_DIR</c> environment variable overrides it so UI tests can run
/// without touching your real settings.
/// </remarks>
public static class KipoPaths
{
    /// <summary>Folder holding settings and log.</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("KIPO_DATA_DIR") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kipo");

    /// <summary>Settings JSON file.</summary>
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    /// <summary>Log file.</summary>
    public static string LogFile => Path.Combine(DataDir, "log.txt");
}
```

`src/Kipo.Core/AppSettings.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kipo.Core;

/// <summary>
/// The user's saved choices.
/// </summary>
/// <remarks>
/// <see cref="PresetMb"/> is one of <see cref="Presets"/>. When <see cref="UseCustom"/> is on,
/// <see cref="CustomMb"/> (1–10000) is the target instead. <see cref="CopyToClipboard"/>
/// controls whether a single-file result lands on the clipboard.
/// </remarks>
public sealed record AppSettings
{
    public const int MinCustomMb = 1;
    public const int MaxCustomMb = 10_000;
    public const long BytesPerMb = 1_048_576;

    /// <summary>Preset sizes in MB, in combo box order. "Custom" follows them.</summary>
    public static IReadOnlyList<int> Presets { get; } = [8, 10, 25, 50, 100];

    public int PresetMb { get; init; } = 10;

    public bool UseCustom { get; init; }

    public int CustomMb { get; init; } = 20;

    public bool CopyToClipboard { get; init; } = true;

    /// <summary>Target size in bytes for the current choice.</summary>
    [JsonIgnore]
    public long TargetBytes => (UseCustom ? CustomMb : PresetMb) * BytesPerMb;

    /// <summary>
    /// Returns a copy with out-of-range values fixed.
    /// </summary>
    /// <returns>Unknown presets reset to 10 MB; custom size clamped to 1–10000.</returns>
    public AppSettings Normalize() => this with
    {
        PresetMb = Presets.Contains(PresetMb) ? PresetMb : 10,
        CustomMb = Math.Clamp(CustomMb, MinCustomMb, MaxCustomMb),
    };
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
/// </summary>
/// <remarks>
/// Load never throws: a missing, unreadable, or corrupt file gives defaults. Save
/// writes a temp file and swaps it in, so a crash mid-save can't leave half a file.
/// </remarks>
/// <param name="filePath">Full path of the settings file.</param>
public sealed class SettingsStore(string filePath)
{
    /// <summary>Reads settings, falling back to defaults.</summary>
    /// <returns>Normalized settings.</returns>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize(File.ReadAllText(filePath), SettingsJson.Default.AppSettings);

            return (settings ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Error("Couldn't read settings, using defaults", ex);

            return new AppSettings();
        }
    }

    /// <summary>Writes settings to disk.</summary>
    /// <param name="settings">Settings to save.</param>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var tempPath = filePath + ".tmp";

        File.WriteAllText(tempPath, JsonSerializer.Serialize(settings.Normalize(), SettingsJson.Default.AppSettings));
        File.Move(tempPath, filePath, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
```

This references `Log` from Task 6. Create a stub now so it builds, finished in Task 6:

`src/Kipo.Core/Log.cs`:
```csharp
namespace Kipo.Core;

/// <summary>Writes failures to Kipo's log file.</summary>
public static class Log
{
    /// <summary>Logs an error.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="exception">Optional exception.</param>
    public static void Error(string message, Exception? exception = null)
    {
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Kipo.Tests --filter SettingsTests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: load and save settings"
```

---

### Task 6: Log

**Files:**
- Modify: `src/Kipo.Core/Log.cs` (replace stub)
- Test: `tests/Kipo.Tests/LogTests.cs`

**Interfaces:**
- Produces: `Log.FilePath : string` (settable, defaults to `KipoPaths.LogFile`), `Log.MaxBytes : long` (settable, default 1 MB), `Log.Error(string message, Exception? exception = null)`. Rolls `log.txt` to `log.old.txt` when over `MaxBytes`. Never throws.

- [ ] **Step 1: Write the failing test**

`tests/Kipo.Tests/LogTests.cs`:
```csharp
using Kipo.Core;

namespace Kipo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LogTests
{
    private string _dir = "";
    private string _originalPath = "";
    private long _originalMax;

    [TestInitialize]
    public void Setup()
    {
        _dir          = Directory.CreateTempSubdirectory("kipo-log-").FullName;
        _originalPath = Log.FilePath;
        _originalMax  = Log.MaxBytes;
        Log.FilePath  = Path.Combine(_dir, "nested", "log.txt");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Log.FilePath = _originalPath;
        Log.MaxBytes = _originalMax;
        Directory.Delete(_dir, true);
    }

    [TestMethod]
    public void Error_WritesMessageAndException()
    {
        Log.Error("boom happened", new InvalidOperationException("inner detail"));

        var text = File.ReadAllText(Log.FilePath);

        StringAssert.Contains(text, "boom happened");
        StringAssert.Contains(text, "inner detail");
    }

    [TestMethod]
    public void Error_OverMaxBytes_RollsToOld()
    {
        Log.MaxBytes = 100;

        Log.Error(new string('a', 200));
        Log.Error("fresh line");

        var oldPath = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "log.old.txt");

        Assert.IsTrue(File.Exists(oldPath));
        StringAssert.Contains(File.ReadAllText(Log.FilePath), "fresh line");
        Assert.IsFalse(File.ReadAllText(Log.FilePath).Contains(new string('a', 200), StringComparison.Ordinal));
    }

    [TestMethod]
    public void Error_UnwritablePath_DoesNotThrow()
    {
        Log.FilePath = "Z:\\\0bad\\log.txt";

        Log.Error("ignored");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Kipo.Tests --filter LogTests`
Expected: build FAIL, `Log.FilePath` not found.

- [ ] **Step 3: Implement**

`src/Kipo.Core/Log.cs`:
```csharp
namespace Kipo.Core;

/// <summary>
/// Writes failures to Kipo's log file.
/// </summary>
/// <remarks>
/// Plain text, one entry per call, timestamped. When the file passes
/// <see cref="MaxBytes"/> it's renamed to <c>log.old.txt</c> (replacing any older one)
/// and a fresh file starts, so the log never grows past about 2 MB total. Logging
/// must never take the app down, so every file error is swallowed.
/// </remarks>
public static class Log
{
    private static readonly Lock Gate = new();

    /// <summary>Log file path. Tests point this at a temp folder.</summary>
    public static string FilePath { get; set; } = KipoPaths.LogFile;

    /// <summary>Size at which the log rolls over.</summary>
    public static long MaxBytes { get; set; } = 1_048_576;

    /// <summary>
    /// Logs an error.
    /// </summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="exception">Optional exception; its full text is written.</param>
    public static void Error(string message, Exception? exception = null)
    {
        var entry = $"{DateTimeOffset.Now:u} ERROR {message}{Environment.NewLine}"
                  + (exception is null ? "" : exception + Environment.NewLine);

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RollIfNeeded();
                File.AppendAllText(FilePath, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Nowhere left to report this; drop it
            }
        }
    }

    private static void RollIfNeeded()
    {
        var file = new FileInfo(FilePath);

        if (file.Exists && file.Length > MaxBytes)
        {
            File.Move(FilePath, Path.Combine(file.DirectoryName!, "log.old.txt"), overwrite: true);
        }
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Kipo.Tests`
Expected: all pass (whole suite, since Settings uses Log).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add rolling error log"
```

---

### Task 7: QueueItem + CompressQueue

**Files:**
- Create: `src/Kipo.Core/QueueItem.cs`, `src/Kipo.Core/CompressQueue.cs`
- Test: `tests/Kipo.Tests/CompressQueueTests.cs`

**Interfaces:**
- Consumes: `PathValidator.Validate`, `Log.Error`
- Produces:
  - `enum ItemStatus { Waiting, Compressing, Done, DoneOverTarget, Skipped, Failed, Canceled }`
  - `record CompressResult(ItemStatus Status, string? OutputPath = null, string? Detail = null)`
  - `QueueItem(string path, long targetBytes)`: `Path`, `FileName`, `TargetBytes`, `Status`, `Progress` (0–100), `Detail`, `OutputPath`, `StatusText`, `CanCancel`, `Cancel()`, `Token`; implements `INotifyPropertyChanged`
  - `delegate Task<CompressResult> CompressWorker(QueueItem item, IProgress<double> progress, CancellationToken cancellationToken)`
  - `CompressQueue(CompressWorker worker)`: `Items : ObservableCollection<QueueItem>`, `AddPaths(IEnumerable<string> paths, long targetBytes)`, `CancelAll()`, `WhenIdle() -> Task`, `event EventHandler<IReadOnlyList<QueueItem>>? BatchCompleted`
  - `CompressQueue.ShouldCopyToClipboard(IReadOnlyList<QueueItem> batch, bool enabled) -> string?` (output path or null)

- [ ] **Step 1: Write the failing test**

`tests/Kipo.Tests/CompressQueueTests.cs`:
```csharp
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
        var item = new QueueItem(P("a.mp4"), 100);

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
    public void ShouldCopy_SingleDone_ReturnsOutput()
    {
        var item = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.Done, OutputPath = @"C:\x-compressed.mp4" };

        Assert.AreEqual(@"C:\x-compressed.mp4", CompressQueue.ShouldCopyToClipboard([item], enabled: true));
    }

    [TestMethod]
    public void ShouldCopy_SingleOverTarget_ReturnsOutput()
    {
        var item = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.DoneOverTarget, OutputPath = @"C:\x-compressed.mp4" };

        Assert.AreEqual(@"C:\x-compressed.mp4", CompressQueue.ShouldCopyToClipboard([item], enabled: true));
    }

    [TestMethod]
    public void ShouldCopy_Disabled_ReturnsNull()
    {
        var item = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.Done, OutputPath = @"C:\x.mp4" };

        Assert.IsNull(CompressQueue.ShouldCopyToClipboard([item], enabled: false));
    }

    [TestMethod]
    public void ShouldCopy_TwoItems_ReturnsNull()
    {
        var a = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.Done, OutputPath = @"C:\a.mp4" };
        var b = new QueueItem(P("b.mp4"), 100) { Status = ItemStatus.Done, OutputPath = @"C:\b.mp4" };

        Assert.IsNull(CompressQueue.ShouldCopyToClipboard([a, b], enabled: true));
    }

    [TestMethod]
    public void ShouldCopy_SingleFailed_ReturnsNull()
    {
        var item = new QueueItem(P("a.mp4"), 100) { Status = ItemStatus.Failed };

        Assert.IsNull(CompressQueue.ShouldCopyToClipboard([item], enabled: true));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Kipo.Tests --filter CompressQueueTests`
Expected: build FAIL, `CompressQueue` not found.

- [ ] **Step 3: Implement**

`src/Kipo.Core/QueueItem.cs`:
```csharp
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Kipo.Core;

/// <summary>Where a queued file is in its life.</summary>
public enum ItemStatus
{
    Waiting,
    Compressing,
    Done,
    DoneOverTarget,
    Skipped,
    Failed,
    Canceled,
}

/// <summary>
/// What a worker reports back for one file.
/// </summary>
/// <param name="Status">Final status.</param>
/// <param name="OutputPath">Output file, when one was written.</param>
/// <param name="Detail">Failure reason, or MB over target for <see cref="ItemStatus.DoneOverTarget"/>.</param>
public sealed record CompressResult(ItemStatus Status, string? OutputPath = null, string? Detail = null);

/// <summary>
/// One row in the queue.
/// </summary>
/// <remarks>
/// Raises <see cref="PropertyChanged"/> so the UI updates live. <see cref="StatusText"/> is
/// the exact text shown in the row. Each item owns its own cancellation source, so one
/// row can be canceled without touching the others.
/// </remarks>
public sealed class QueueItem : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private ItemStatus _status;
    private double _progress;
    private string? _detail;

    /// <summary>Creates a waiting item.</summary>
    /// <param name="path">Source file path.</param>
    /// <param name="targetBytes">Target size in bytes, fixed when the file was added.</param>
    public QueueItem(string path, long targetBytes)
    {
        Path        = path;
        TargetBytes = targetBytes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public long TargetBytes { get; }

    public string? OutputPath { get; set; }

    public CancellationToken Token => _cancel.Token;

    public ItemStatus Status
    {
        get => _status;
        set => Set(ref _status, value, nameof(StatusText), nameof(CanCancel));
    }

    /// <summary>Encode progress, 0–100.</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value, nameof(StatusText));
    }

    public string? Detail
    {
        get => _detail;
        set => Set(ref _detail, value, nameof(StatusText));
    }

    public bool CanCancel => Status is ItemStatus.Waiting or ItemStatus.Compressing;

    public string StatusText => Status switch
    {
        ItemStatus.Waiting        => "Waiting",
        ItemStatus.Compressing    => string.Create(CultureInfo.InvariantCulture, $"Compressing {Math.Round(Progress):0}%"),
        ItemStatus.Done           => "Done",
        ItemStatus.DoneOverTarget => $"Done (over target by {Detail} MB)",
        ItemStatus.Skipped        => "Already under target",
        ItemStatus.Failed         => $"Failed: {Detail}",
        ItemStatus.Canceled       => "Canceled",
        _                         => Status.ToString(),
    };

    /// <summary>Cancels this item. Waiting items flip to Canceled right away.</summary>
    public void Cancel()
    {
        if (!CanCancel)
        {
            return;
        }

        if (Status == ItemStatus.Waiting)
        {
            Status = ItemStatus.Canceled;
        }

        _cancel.Cancel();
    }

    public void Dispose() => _cancel.Dispose();

    // Property name comes from the calling setter
    private void Set<T>(ref T field, T value, string first, string? second = null, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(name);
        Raise(first);

        if (second is not null)
        {
            Raise(second);
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

`src/Kipo.Core/CompressQueue.cs`:
```csharp
using System.Collections.ObjectModel;

namespace Kipo.Core;

/// <summary>Does the work for one queued file.</summary>
/// <param name="item">The file.</param>
/// <param name="progress">Receives 0–100.</param>
/// <param name="cancellationToken">Canceled when the user cancels the row.</param>
/// <returns>The outcome.</returns>
public delegate Task<CompressResult> CompressWorker(QueueItem item, IProgress<double> progress, CancellationToken cancellationToken);

/// <summary>
/// Runs files one at a time, in the order added.
/// </summary>
/// <remarks>
/// Adding files starts the queue if it's idle; files added while it runs join the end.
/// A "batch" is everything added between idle and idle; <see cref="BatchCompleted"/>
/// fires once per batch with all its items. Create and use the queue from the UI thread:
/// awaits resume there, so <see cref="Items"/> and item properties change on the UI thread.
/// </remarks>
/// <param name="worker">Does the actual compression.</param>
public sealed class CompressQueue(CompressWorker worker)
{
    private readonly List<QueueItem> _batch = [];
    private Task _running = Task.CompletedTask;

    /// <summary>Fires when the queue goes idle, with every item added since it last went idle.</summary>
    public event EventHandler<IReadOnlyList<QueueItem>>? BatchCompleted;

    /// <summary>All rows ever added, oldest first.</summary>
    public ObservableCollection<QueueItem> Items { get; } = [];

    /// <summary>
    /// Validates and queues paths. Invalid ones get a Failed row and never run.
    /// </summary>
    /// <param name="paths">Candidate file paths.</param>
    /// <param name="targetBytes">Target size for these files.</param>
    public void AddPaths(IEnumerable<string> paths, long targetBytes)
    {
        ArgumentNullException.ThrowIfNull(paths);

        foreach (var path in paths)
        {
            var item  = new QueueItem(path, targetBytes);
            var error = PathValidator.Validate(path);

            if (error is not null)
            {
                item.Detail = error;
                item.Status = ItemStatus.Failed;
            }

            Items.Add(item);
            _batch.Add(item);
        }

        if (_running.IsCompleted && _batch.Count > 0)
        {
            _running = RunAsync();
        }
    }

    /// <summary>Cancels every waiting and running item.</summary>
    public void CancelAll()
    {
        foreach (var item in Items)
        {
            item.Cancel();
        }
    }

    /// <summary>Completes when the queue has nothing left to run.</summary>
    /// <returns>The current run, or a completed task.</returns>
    public Task WhenIdle() => _running;

    /// <summary>
    /// Decides whether a finished batch's result goes on the clipboard.
    /// </summary>
    /// <param name="batch">Items from <see cref="BatchCompleted"/>.</param>
    /// <param name="enabled">The user's clipboard setting.</param>
    /// <returns>Output path to copy, or null.</returns>
    public static string? ShouldCopyToClipboard(IReadOnlyList<QueueItem> batch, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (!enabled || batch.Count != 1)
        {
            return null;
        }

        var item = batch[0];

        return item.Status is ItemStatus.Done or ItemStatus.DoneOverTarget ? item.OutputPath : null;
    }

    private async Task RunAsync()
    {
        while (Items.FirstOrDefault(i => i.Status == ItemStatus.Waiting) is { } item)
        {
            await RunOneAsync(item);
        }

        var finished = _batch.ToList();
        _batch.Clear();
        BatchCompleted?.Invoke(this, finished);
    }

    private async Task RunOneAsync(QueueItem item)
    {
        item.Status   = ItemStatus.Compressing;
        item.Progress = 0;

        var progress = new Progress<double>(value => item.Progress = value);

        try
        {
            var result = await worker(item, progress, item.Token);

            item.OutputPath = result.OutputPath;
            item.Detail     = result.Detail;
            item.Status     = result.Status;
        }
        catch (OperationCanceledException)
        {
            item.Status = ItemStatus.Canceled;
        }
        catch (Exception ex)
        {
            Log.Error($"Compressing {item.Path} failed", ex);
            item.Detail = ex.Message;
            item.Status = ItemStatus.Failed;
        }
    }
}
```

`catch (Exception ex)` is intentional here: any worker failure must become a Failed row, not a crash. If CA1031 fires, suppress it on that line with a justification:
```csharp
#pragma warning disable CA1031 // Any worker failure becomes a Failed row instead of crashing the app
        catch (Exception ex)
#pragma warning restore CA1031
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Kipo.Tests --filter CompressQueueTests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: add sequential compress queue"
```

---

### Task 8: ClipboardInput (paste, drop, picker, clipboard output)

**Files:**
- Create: `src/Kipo.Core/ClipboardInput.cs`
- Create: `tests/Kipo.Tests/TestVideos.cs`
- Test: `tests/Kipo.Tests/ClipboardInputTests.cs`

**Interfaces:**
- Consumes: `PathText.Parse`, `CompressQueue.AddPaths`
- Produces:
  - `ClipboardInput.ReadPathsAsync(DataPackageView view) -> Task<IReadOnlyList<string>>` — storage items win over text; returns empty for anything else
  - `ClipboardInput.CopyFileAsync(string path) -> Task` — puts the file on the clipboard as a storage item
  - `TestVideos` helper (test project): `Folder`, `Tiny`, `Small`, `Mid`, `Large`, `Batch`, `NewTempDir()`, `CopyToTemp(string dir, string name) -> string`

- [ ] **Step 1: Sample video helper**

`tests/Kipo.Tests/TestVideos.cs`:
```csharp
namespace Kipo.Tests;

/// <summary>
/// Real screen recordings used by video and UI tests.
/// </summary>
/// <remarks>
/// Files are copied into a temp folder before use; the sample folder is never written to.
/// Set KIPO_TEST_VIDEOS to point somewhere else. Missing files make the test Inconclusive.
/// </remarks>
internal static class TestVideos
{
    public const string Tiny  = "Screen Recording 2026-09-25 195601.mp4";   // 0.37 MB
    public const string Small = "Screen Recording 2026-08-06 082223.mp4";   // 1.2 MB
    public const string Mid   = "Screen Recording 2026-09-26 081940.mp4";   // 18.9 MB
    public const string Large = "Screen Recording 2026-07-27 075812.mp4";   // 129 MB

    public static readonly string[] Batch =
    [
        "Screen Recording 2026-09-18 092650.mp4",   // 0.70 MB
        "Screen Recording 2026-09-18 092820.mp4",   // 0.66 MB
        "Screen Recording 2026-09-18 113937.mp4",   // 0.71 MB
    ];

    public static string Folder =>
        Environment.GetEnvironmentVariable("KIPO_TEST_VIDEOS") is { Length: > 0 } folder
            ? folder
            : @"C:\Users\artistro08\Videos\Screen Recordings";

    public static string NewTempDir() => Directory.CreateTempSubdirectory("kipo-test-").FullName;

    public static string CopyToTemp(string dir, string name)
    {
        var source = Path.Combine(Folder, name);

        if (!File.Exists(source))
        {
            Assert.Inconclusive($"Sample video missing: {source}");
        }

        var copy = Path.Combine(dir, name);
        File.Copy(source, copy, overwrite: true);

        return copy;
    }
}
```

- [ ] **Step 2: Write the failing test**

`tests/Kipo.Tests/ClipboardInputTests.cs`:
```csharp
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
    [STATestMethod]
    public async Task ReadPaths_DroppedFiles_ReturnsPaths()
    {
        var files   = await SampleFilesAsync(TestVideos.Batch);
        var package = new DataPackage();
        package.SetStorageItems(files);

        var paths = await ClipboardInput.ReadPathsAsync(package.GetView());

        CollectionAssert.AreEqual(files.Select(f => f.Path).ToArray(), paths.ToArray());
    }

    [STATestMethod]
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

    [STATestMethod]
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

    [STATestMethod]
    public async Task ReadPaths_HtmlOnly_ReturnsEmpty()
    {
        var package = new DataPackage();
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<b>hi</b>"));

        Assert.AreEqual(0, (await ClipboardInput.ReadPathsAsync(package.GetView())).Count);
    }

    [STATestMethod]
    public async Task ReadPaths_EmptyText_ReturnsEmpty()
    {
        var package = new DataPackage();
        package.SetText("   ");

        Assert.AreEqual(0, (await ClipboardInput.ReadPathsAsync(package.GetView())).Count);
    }

    [STATestMethod]
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
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Kipo.Tests --filter ClipboardInputTests`
Expected: build FAIL, `ClipboardInput` not found.

- [ ] **Step 4: Implement**

`src/Kipo.Core/ClipboardInput.cs`:
```csharp
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Kipo.Core;

/// <summary>
/// Moves file paths between Kipo and the Windows clipboard or a drop.
/// </summary>
/// <remarks>
/// Paste and drag-drop both hand over a <see cref="DataPackageView"/>, so one reader
/// serves both. Copied files (from Explorer) win; otherwise text is parsed as paths.
/// Anything else (images, HTML) gives an empty list. Paths aren't validated here;
/// <see cref="CompressQueue.AddPaths"/> does that.
/// </remarks>
public static class ClipboardInput
{
    /// <summary>
    /// Reads candidate paths from pasted or dropped data.
    /// </summary>
    /// <param name="view">Clipboard content or drop data.</param>
    /// <returns>Paths in order; empty when there's nothing usable.</returns>
    public static async Task<IReadOnlyList<string>> ReadPathsAsync(DataPackageView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var items = await view.GetStorageItemsAsync();

            return items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToList();
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            return PathText.Parse(await view.GetTextAsync());
        }

        return [];
    }

    /// <summary>
    /// Puts a file on the clipboard, ready to paste into Explorer or a chat app.
    /// </summary>
    /// <param name="path">File to copy.</param>
    /// <returns>A task that completes once the clipboard is set.</returns>
    public static async Task CopyFileAsync(string path)
    {
        var file    = await StorageFile.GetFileFromPathAsync(path);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };

        package.SetStorageItems([file]);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Kipo.Tests --filter ClipboardInputTests`
Expected: all pass. If a clipboard test fails with `0x800401D0 (CLIPBRD_E_CANT_OPEN)`, another app holds the clipboard; rerun. If `[STATestMethod]` isn't recognized in MSTest 4.4.1, use `[TestMethod]` on a `[STATestClass]` class.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: read pasted and dropped files, copy result to clipboard"
```

---

### Task 9: Compressor + video integration tests

**Files:**
- Create: `src/Kipo.Core/Compressor.cs`
- Test: `tests/Kipo.Tests/CompressorTests.cs`

**Interfaces:**
- Consumes: `EncodePlanner.Plan`, `EncodePlanner.RetryBitrate`, `OutputNamer`, `CompressResult`, `ItemStatus`, `InputErrors`, `Log`
- Produces: `Compressor.CompressAsync(string sourcePath, long targetBytes, IProgress<double>? progress, CancellationToken cancellationToken) -> Task<CompressResult>`; static `Compressor.ReadInfoAsync(StorageFile file) -> Task<VideoInfo?>`; `CompressErrors.CantWrite = "can't write output"`, `CompressErrors.CantEncode = "can't encode this video"`

- [ ] **Step 1: Write the failing test**

`tests/Kipo.Tests/CompressorTests.cs`:
```csharp
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

        var result = await new Compressor().CompressAsync(source, 8 * MB, null, CancellationToken.None);

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

        var result = await new Compressor().CompressAsync(source, 25 * MB, null, CancellationToken.None);

        Assert.AreEqual(ItemStatus.Done, result.Status, result.Detail);
        Assert.IsTrue(new FileInfo(result.OutputPath!).Length <= 25 * MB);
        AssertNoPartFiles();
    }

    [TestMethod]
    public async Task Compress_UnderTarget_Skips()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);

        var result = await new Compressor().CompressAsync(source, 10 * MB, null, CancellationToken.None);

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

        var result = await new Compressor().CompressAsync(source, MB, progress, CancellationToken.None);

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

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new Compressor().CompressAsync(source, 25 * MB, progress, cancel.Token));

        Assert.AreEqual(1, Directory.GetFiles(_dir).Length);
    }

    [TestMethod]
    public async Task Compress_MissingSource_FailsNotFound()
    {
        var result = await new Compressor().CompressAsync(Path.Combine(_dir, "gone.mp4"), MB, null, CancellationToken.None);

        Assert.AreEqual(ItemStatus.Failed, result.Status);
        Assert.AreEqual(InputErrors.NotFound, result.Detail);
        AssertNoPartFiles();
    }

    [TestMethod]
    public async Task Compress_NotReallyAVideo_Fails()
    {
        var source = Path.Combine(_dir, "fake.mp4");
        File.WriteAllText(source, new string('x', 2_000_000));

        var result = await new Compressor().CompressAsync(source, MB, null, CancellationToken.None);

        Assert.AreEqual(ItemStatus.Failed, result.Status);
        AssertNoPartFiles();
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task Queue_SameFileTwice_SecondGetsNumberedName()
    {
        var source     = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        var compressor = new Compressor();
        var queue      = new CompressQueue((item, progress, token) => compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));

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
        var compressor = new Compressor();
        var queue      = new CompressQueue((item, progress, token) => compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));

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
        var compressor = new Compressor();
        var queue      = new CompressQueue((item, progress, token) => compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));

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
```

Note on `Queue_SourceDeletedAfterQueued`: the first file is Tiny (skipped fast), so the second is still waiting when it's deleted. Deletion happens synchronously after `AddPaths`, before the first await completes.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Kipo.Tests --filter CompressorTests`
Expected: build FAIL, `Compressor` not found.

- [ ] **Step 3: Implement**

`src/Kipo.Core/Compressor.cs`:
```csharp
using System.Globalization;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Kipo.Core;

/// <summary>Reasons an encode fails. Shown after "Failed: " in the queue.</summary>
public static class CompressErrors
{
    public const string CantWrite  = "can't write output";
    public const string CantEncode = "can't encode this video";
}

/// <summary>
/// Compresses one video to a target size with the built-in Windows MediaTranscoder.
/// </summary>
/// <remarks>
/// Reads the source's size, length, frame rate and audio, asks <see cref="EncodePlanner"/>
/// for a plan, then encodes H.264 + AAC into <c>name-compressed.part</c> next to the source.
/// If the result is over target it retries once at a lower bitrate. On success the
/// <c>.part</c> is renamed to the final name; on failure or cancel it's deleted. The
/// source is only ever read.
/// </remarks>
/// <seealso href="https://learn.microsoft.com/uwp/api/windows.media.transcoding.mediatranscoder"/>
public sealed class Compressor
{
    /// <summary>
    /// Compresses a file.
    /// </summary>
    /// <param name="sourcePath">Validated source path.</param>
    /// <param name="targetBytes">Target output size.</param>
    /// <param name="progress">Receives 0–100 per encode pass.</param>
    /// <param name="cancellationToken">Stops the encode.</param>
    /// <returns>The outcome. Expected failures come back as <see cref="ItemStatus.Failed"/>.</returns>
    /// <exception cref="OperationCanceledException">When canceled.</exception>
    public async Task<CompressResult> CompressAsync(string sourcePath, long targetBytes, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var partPath = OutputNamer.GetPartPath(sourcePath);

        try
        {
            if (!File.Exists(sourcePath))
            {
                return Failed(InputErrors.NotFound);
            }

            // Plan
            var source = await StorageFile.GetFileFromPathAsync(sourcePath);
            var info   = await ReadInfoAsync(source);

            if (info is null)
            {
                return Failed(PlanErrors.CantRead);
            }

            var plan = EncodePlanner.Plan(info, new FileInfo(sourcePath).Length, targetBytes);

            if (plan.Kind == PlanKind.Skip)
            {
                return new CompressResult(ItemStatus.Skipped);
            }

            if (plan.Kind == PlanKind.Fail)
            {
                return Failed(plan.Error);
            }

            // Encode, with one retry if over target
            var folder       = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(sourcePath));
            var videoBitrate = plan.VideoBitrate;
            long actualBytes = 0;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                await TranscodeAsync(source, folder, Path.GetFileName(partPath), plan with { VideoBitrate = videoBitrate }, info, progress, cancellationToken);

                actualBytes = new FileInfo(partPath).Length;

                if (actualBytes <= targetBytes)
                {
                    break;
                }

                videoBitrate = EncodePlanner.RetryBitrate(videoBitrate, targetBytes, actualBytes);
            }

            // Finish
            var outputPath = OutputNamer.GetOutputPath(sourcePath, File.Exists);
            File.Move(partPath, outputPath);

            if (actualBytes <= targetBytes)
            {
                return new CompressResult(ItemStatus.Done, outputPath);
            }

            var overMb = ((actualBytes - targetBytes) / (double)AppSettings.BytesPerMb).ToString("0.0", CultureInfo.InvariantCulture);

            return new CompressResult(ItemStatus.DoneOverTarget, outputPath, overMb);
        }
        catch (OperationCanceledException)
        {
            DeleteQuietly(partPath);
            throw;
        }
        catch (FileNotFoundException)
        {
            DeleteQuietly(partPath);
            return Failed(InputErrors.NotFound);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error($"No write access for {sourcePath}", ex);
            DeleteQuietly(partPath);
            return Failed(CompressErrors.CantWrite);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException || ex.HResult < 0)
        {
            Log.Error($"Encode failed for {sourcePath}", ex);
            DeleteQuietly(partPath);
            return Failed(CompressErrors.CantEncode);
        }
    }

    /// <summary>
    /// Reads what the planner needs from a video file.
    /// </summary>
    /// <param name="file">Source file.</param>
    /// <returns>Video facts, or null when the file has no readable video track.</returns>
    public static async Task<VideoInfo?> ReadInfoAsync(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var profile = await MediaEncodingProfile.CreateFromFileAsync(file);

        if (profile.Video is null)
        {
            return null;
        }

        var properties = await file.Properties.GetVideoPropertiesAsync();
        var frameRate  = profile.Video.FrameRate.Denominator == 0
            ? 0
            : profile.Video.FrameRate.Numerator / (double)profile.Video.FrameRate.Denominator;

        return new VideoInfo(properties.Duration, (int)profile.Video.Width, (int)profile.Video.Height, frameRate, profile.Audio is not null);
    }

    private static async Task TranscodeAsync(StorageFile source, StorageFolder folder, string partName, EncodePlan plan, VideoInfo info, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var part    = await folder.CreateFileAsync(partName, CreationCollisionOption.ReplaceExisting);
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

        // Video
        profile.Video.Width   = (uint)plan.Width;
        profile.Video.Height  = (uint)plan.Height;
        profile.Video.Bitrate = plan.VideoBitrate;

        if (info.FrameRate > 0)
        {
            profile.Video.FrameRate.Numerator   = (uint)Math.Round(info.FrameRate * 1000);
            profile.Video.FrameRate.Denominator = 1000;
        }

        // Audio
        profile.Audio = info.HasAudio ? AudioEncodingProperties.CreateAac(48_000, 2, plan.AudioBitrate) : null;

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared   = await transcoder.PrepareFileTranscodeAsync(source, part, profile);

        if (!prepared.CanTranscode)
        {
            throw new InvalidOperationException($"Transcode not possible: {prepared.FailureReason}");
        }

        // Forward straight to the caller's progress so values stay on its thread and in order
        await prepared.TranscodeAsync().AsTask(cancellationToken, progress ?? new Progress<double>());
    }

    private static CompressResult Failed(string? reason) => new(ItemStatus.Failed, null, reason);

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't delete {path}", ex);
        }
    }
}
```

If `PrepareFileTranscodeAsync` refuses the `.part` extension (it picks the container from the profile, so it shouldn't), switch `OutputNamer.GetPartPath` to `name-compressed.part.mp4`, update the OutputNamer test and the spec, and note it in the commit.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Kipo.Tests --filter CompressorTests`
Expected: all pass (large-file tests take minutes). If an encode comes out over target on both tries, look at actual vs target before changing `BudgetFactor`; report the numbers.

- [ ] **Step 5: Full suite**

Run: `dotnet test tests/Kipo.Tests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: compress videos with MediaTranscoder"
```

---

### Task 10: WinUI app

**Files:**
- Create: `src/Kipo/Kipo.csproj`, `src/Kipo/app.manifest`, `src/Kipo/Package.appxmanifest`
- Create: `src/Kipo/App.xaml`, `src/Kipo/App.xaml.cs`, `src/Kipo/MainWindow.xaml`, `src/Kipo/MainWindow.xaml.cs`
- Create: `src/Kipo/Assets/Square150x150Logo.png`, `Square44x44Logo.png`, `StoreLogo.png`
- Modify: `Kipo.slnx` (add project)

**Interfaces:**
- Consumes: everything in `Kipo.Core`
- Produces: `Kipo.exe` with AutomationIds used by Task 11: `DropZone`, `BrowseButton`, `SizeCombo`, `CustomSizeBox`, `QueueList`, `CancelAllButton`, `SettingsButton`, `CopyToClipboardToggle`. Combo item text: `8 MB`, `10 MB`, `25 MB`, `50 MB`, `100 MB`, `Custom`.

- [ ] **Step 1: Project file**

`src/Kipo/Kipo.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <RootNamespace>Kipo</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Platforms>x64;ARM64</Platforms>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>true</EnableMsixTooling>
    <Version>1.0.0</Version>
    <KipoPackaging Condition="'$(KipoPackaging)' == ''">Unpackaged</KipoPackaging>
    <!-- bin/obj hold both packaging modes; keep their generated files out of the compile -->
    <DefaultItemExcludes>$(DefaultItemExcludes);bin\**;obj\**</DefaultItemExcludes>
  </PropertyGroup>

  <!-- Unpackaged: used for dev, UI tests and the MSI. Ships .NET and WinAppSDK inside. -->
  <PropertyGroup Condition="'$(KipoPackaging)' == 'Unpackaged'">
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <SelfContained>true</SelfContained>
  </PropertyGroup>

  <!-- MSIX: packaged build, uses the WinAppSDK framework package -->
  <PropertyGroup Condition="'$(KipoPackaging)' == 'Msix'">
    <GenerateAppxPackageOnBuild>true</GenerateAppxPackageOnBuild>
    <AppxPackageSigningEnabled>true</AppxPackageSigningEnabled>
    <AppxBundle>Never</AppxBundle>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="Assets\*.png" />
    <Manifest Include="$(ApplicationManifest)" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.5.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Kipo.Core\Kipo.Core.csproj" />
  </ItemGroup>

  <ItemGroup Condition="'$(KipoPackaging)' == 'Msix'">
    <ProjectCapability Include="Msix" />
  </ItemGroup>
</Project>
```

`src/Kipo/app.manifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="Kipo.app"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/PM</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2, PerMonitor</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

`src/Kipo/Package.appxmanifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">

  <Identity Name="Kipo" Publisher="CN=Kipo Dev" Version="1.0.0.0" />

  <Properties>
    <DisplayName>Kipo</DisplayName>
    <PublisherDisplayName>Devin Green</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>

  <Resources>
    <Resource Language="en-us" />
  </Resources>

  <Applications>
    <Application Id="App" Executable="$targetnametoken$.exe" EntryPoint="$targetentrypoint$">
      <uap:VisualElements
        DisplayName="Kipo"
        Description="Compress videos to a target size."
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>

  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
```

Assets: generate plain placeholder PNGs (150×150, 44×44, 50×50, solid accent color with a "K") using Windows PowerShell and `System.Drawing`:
```powershell
powershell.exe -NoProfile -Command {
  Add-Type -AssemblyName System.Drawing
  $sizes = @{ 'Square150x150Logo' = 150; 'Square44x44Logo' = 44; 'StoreLogo' = 50 }
  foreach ($name in $sizes.Keys) {
    $n = $sizes[$name]
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 0, 120, 212))
    $font = New-Object System.Drawing.Font 'Segoe UI', ([int]($n * 0.55)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'; $format.LineAlignment = 'Center'
    $g.DrawString('K', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $n, $n), $format)
    $bmp.Save("D:\kipo\src\Kipo\Assets\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
  }
}
```

Add to solution: `dotnet sln Kipo.slnx add src/Kipo/Kipo.csproj`.

- [ ] **Step 2: App**

`src/Kipo/App.xaml`:
```xml
<Application
    x:Class="Kipo.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`src/Kipo/App.xaml.cs`:
```csharp
using Kipo.Core;
using Microsoft.UI.Xaml;

namespace Kipo;

/// <summary>
/// Starts Kipo and catches anything that would otherwise crash it silently.
/// </summary>
/// <remarks>
/// UI-thread exceptions are logged and shown in a dialog, then marked handled so the
/// window stays open. Background exceptions are logged.
/// </remarks>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // Crash Handling
        UnhandledException                         += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException      += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        _window?.ShowError("Something went wrong. Details were saved to the log.");
    }
}
```

- [ ] **Step 3: Main window**

`src/Kipo/MainWindow.xaml`:
```xml
<Window
    x:Class="Kipo.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:core="using:Kipo.Core"
    Title="Kipo">

    <Window.SystemBackdrop>
        <MicaBackdrop />
    </Window.SystemBackdrop>

    <Grid x:Name="Root"
          Padding="16"
          RowSpacing="12"
          AllowDrop="True"
          DragOver="OnDragOver"
          Drop="OnDrop">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <Grid.KeyboardAccelerators>
            <KeyboardAccelerator Key="V" Modifiers="Control" Invoked="OnPasteInvoked" />
        </Grid.KeyboardAccelerators>

        <!-- Drop Zone -->
        <Grid x:Name="DropZone"
              Grid.Row="0"
              Height="140"
              AutomationProperties.AutomationId="DropZone"
              AutomationProperties.Name="Drop zone">
            <Rectangle RadiusX="8"
                       RadiusY="8"
                       Stroke="{ThemeResource ControlStrongStrokeColorDefaultBrush}"
                       StrokeThickness="1.5"
                       StrokeDashArray="4 3"
                       Fill="{ThemeResource CardBackgroundFillColorDefaultBrush}" />
            <StackPanel HorizontalAlignment="Center"
                        VerticalAlignment="Center"
                        Spacing="10">
                <TextBlock Text="Drop, paste (Ctrl+V), or browse"
                           HorizontalAlignment="Center"
                           Style="{StaticResource BodyStrongTextBlockStyle}" />
                <Button x:Name="BrowseButton"
                        HorizontalAlignment="Center"
                        Content="Browse"
                        AutomationProperties.AutomationId="BrowseButton"
                        Click="OnBrowseClick" />
            </StackPanel>
        </Grid>
        <!-- /Drop Zone -->

        <!-- Target Size -->
        <StackPanel Grid.Row="1"
                    Orientation="Horizontal"
                    Spacing="8">
            <ComboBox x:Name="SizeCombo"
                      Header="Target size"
                      MinWidth="140"
                      AutomationProperties.AutomationId="SizeCombo"
                      SelectionChanged="OnSizeChanged" />
            <NumberBox x:Name="CustomSizeBox"
                       Header="MB"
                       Width="120"
                       Minimum="1"
                       Maximum="10000"
                       SpinButtonPlacementMode="Compact"
                       Visibility="Collapsed"
                       AutomationProperties.AutomationId="CustomSizeBox"
                       AutomationProperties.Name="Custom size in MB"
                       ValueChanged="OnCustomSizeChanged" />
        </StackPanel>

        <!-- Queue -->
        <ListView x:Name="QueueList"
                  Grid.Row="2"
                  SelectionMode="None"
                  AutomationProperties.AutomationId="QueueList"
                  AutomationProperties.Name="Queue">
            <ListView.ItemTemplate>
                <DataTemplate x:DataType="core:QueueItem">
                    <Grid ColumnSpacing="8" Padding="0,6">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <StackPanel>
                            <TextBlock Text="{x:Bind FileName}"
                                       TextTrimming="CharacterEllipsis" />
                            <TextBlock Text="{x:Bind StatusText, Mode=OneWay}"
                                       Style="{StaticResource CaptionTextBlockStyle}"
                                       Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                        </StackPanel>
                        <Button Grid.Column="1"
                                VerticalAlignment="Center"
                                Visibility="{x:Bind CanCancel, Mode=OneWay}"
                                AutomationProperties.Name="Cancel"
                                ToolTipService.ToolTip="Cancel"
                                Click="OnCancelItemClick">
                            <FontIcon Glyph="&#xE711;" FontSize="12" />
                        </Button>
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
        <!-- /Queue -->

        <!-- Bottom Bar -->
        <Grid Grid.Row="3">
            <Button x:Name="CancelAllButton"
                    Content="Cancel all"
                    AutomationProperties.AutomationId="CancelAllButton"
                    Click="OnCancelAllClick" />
            <Button x:Name="SettingsButton"
                    HorizontalAlignment="Right"
                    AutomationProperties.AutomationId="SettingsButton"
                    AutomationProperties.Name="Settings"
                    ToolTipService.ToolTip="Settings">
                <FontIcon Glyph="&#xE713;" FontSize="14" />
                <Button.Flyout>
                    <Flyout>
                        <ToggleSwitch x:Name="CopyToClipboardToggle"
                                      Header="Copy result to clipboard (single file)"
                                      AutomationProperties.AutomationId="CopyToClipboardToggle"
                                      Toggled="OnCopyToggled" />
                    </Flyout>
                </Button.Flyout>
            </Button>
        </Grid>
    </Grid>
</Window>
```

`src/Kipo/MainWindow.xaml.cs`:
```csharp
using Kipo.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace Kipo;

/// <summary>
/// Kipo's only window: drop zone, size picker, queue, and settings.
/// </summary>
/// <remarks>
/// All real work is in Kipo.Core. This class turns clicks, pastes and drops into
/// <see cref="CompressQueue.AddPaths"/> calls, keeps the settings file in sync with
/// the controls, and copies a single-file result to the clipboard when enabled.
/// </remarks>
public sealed partial class MainWindow : Window
{
    private const int WindowWidth  = 420;
    private const int WindowHeight = 520;

    private readonly SettingsStore _settingsStore = new(KipoPaths.SettingsFile);
    private readonly Compressor _compressor       = new();
    private readonly CompressQueue _queue;
    private AppSettings _settings;
    private bool _loadingControls;

    public MainWindow()
    {
        InitializeComponent();

        _settings = _settingsStore.Load();
        _queue    = new CompressQueue((item, progress, token) => _compressor.CompressAsync(item.Path, item.TargetBytes, progress, token));

        _queue.BatchCompleted += OnBatchCompleted;
        QueueList.ItemsSource  = _queue.Items;
        Root.Loaded           += (_, _) => ResizeForDpi();

        LoadControls();
    }

    /// <summary>Shows a simple error dialog.</summary>
    /// <param name="message">Plain-language message.</param>
    public async void ShowError(string message)
    {
        if (Content.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title           = "Kipo",
            Content         = message,
            CloseButtonText = "OK",
            XamlRoot        = Content.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    // =========================================================================
    // INPUT
    // =========================================================================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();

        try
        {
            AddPaths(await ClipboardInput.ReadPathsAsync(e.DataView));
        }
        catch (Exception ex) when (ex.HResult < 0 || ex is IOException)
        {
            Log.Error("Drop failed", ex);
            ShowError("Couldn't read the dropped files.");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnPasteInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Let the custom size box handle its own paste
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox)
        {
            return;
        }

        args.Handled = true;

        try
        {
            AddPaths(await ClipboardInput.ReadPathsAsync(Clipboard.GetContent()));
        }
        catch (Exception ex) when (ex.HResult < 0 || ex is IOException)
        {
            Log.Error("Paste failed", ex);
            ShowError("Couldn't read the clipboard.");
        }
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);

            foreach (var extension in PathValidator.VideoExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            var picked = await picker.PickMultipleFilesAsync();

            AddPaths(picked.Select(result => result.Path).ToList());
        }
        catch (Exception ex) when (ex.HResult < 0 || ex is IOException)
        {
            Log.Error("File picker failed", ex);
            ShowError("Couldn't open the file picker.");
        }
    }

    private void AddPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        _queue.AddPaths(paths, _settings.TargetBytes);
    }

    // =========================================================================
    // QUEUE
    // =========================================================================

    private void OnCancelItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueueItem item)
        {
            item.Cancel();
        }
    }

    private void OnCancelAllClick(object sender, RoutedEventArgs e) => _queue.CancelAll();

    private async void OnBatchCompleted(object? sender, IReadOnlyList<QueueItem> batch)
    {
        var outputPath = CompressQueue.ShouldCopyToClipboard(batch, _settings.CopyToClipboard);

        if (outputPath is null)
        {
            return;
        }

        try
        {
            await ClipboardInput.CopyFileAsync(outputPath);
        }
        catch (Exception ex) when (ex.HResult < 0 || ex is IOException)
        {
            Log.Error("Copy to clipboard failed", ex);
        }
    }

    // =========================================================================
    // SETTINGS
    // =========================================================================

    private void LoadControls()
    {
        _loadingControls = true;

        foreach (var mb in AppSettings.Presets)
        {
            SizeCombo.Items.Add($"{mb} MB");
        }

        SizeCombo.Items.Add("Custom");

        SizeCombo.SelectedIndex         = _settings.UseCustom ? AppSettings.Presets.Count : IndexOfPreset(_settings.PresetMb);
        CustomSizeBox.Value             = _settings.CustomMb;
        CustomSizeBox.Visibility        = _settings.UseCustom ? Visibility.Visible : Visibility.Collapsed;
        CopyToClipboardToggle.IsOn      = _settings.CopyToClipboard;

        _loadingControls = false;
    }

    private void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        var useCustom = SizeCombo.SelectedIndex == AppSettings.Presets.Count;

        CustomSizeBox.Visibility = useCustom ? Visibility.Visible : Visibility.Collapsed;

        SaveSettings(useCustom
            ? _settings with { UseCustom = true }
            : _settings with { UseCustom = false, PresetMb = AppSettings.Presets[Math.Max(0, SizeCombo.SelectedIndex)] });
    }

    private void OnCustomSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            return;
        }

        SaveSettings(_settings with { CustomMb = (int)Math.Round(args.NewValue) });
    }

    private void OnCopyToggled(object sender, RoutedEventArgs e) =>
        SaveSettings(_settings with { CopyToClipboard = CopyToClipboardToggle.IsOn });

    private void SaveSettings(AppSettings settings)
    {
        if (_loadingControls)
        {
            return;
        }

        _settings = settings.Normalize();

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Couldn't save settings", ex);
        }
    }

    private static int IndexOfPreset(int mb)
    {
        for (var i = 0; i < AppSettings.Presets.Count; i++)
        {
            if (AppSettings.Presets[i] == mb)
            {
                return i;
            }
        }

        return 1;
    }

    // =========================================================================
    // WINDOW
    // =========================================================================

    private void ResizeForDpi()
    {
        var scale = Content.XamlRoot?.RasterizationScale ?? 1.0;

        AppWindow.Resize(new SizeInt32((int)(WindowWidth * scale), (int)(WindowHeight * scale)));
    }
}
```

Note: `ShowError` is `async void` because it's fire-and-forget from event handlers and the app-level crash handler; it can't throw past its own await in practice. If analyzers flag it (VSTHRD100 isn't installed, so only CA rules apply), leave it.

- [ ] **Step 4: Build**

Run: `dotnet build src/Kipo/Kipo.csproj -p:Platform=x64`
Expected: builds. Fix analyzer errors (they're errors by design). Common ones: CA1515 (make public types internal) is off in `latest-recommended` for exes? If it fires on `App`/`MainWindow`, make them `internal` only if XAML codegen allows; otherwise suppress in `src/Kipo/.editorconfig` with a comment explaining XAML needs them public.

- [ ] **Step 5: Smoke run**

Launch `src/Kipo/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Kipo.exe` with `KIPO_DATA_DIR` pointed at a temp folder. Paste a sample path, confirm the row goes Compressing → Done and the output appears next to the source copy. Close it.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: add WinUI window"
```

---

### Task 11: UI automation tests (FlaUI)

**Files:**
- Modify: `tests/Kipo.Tests/Kipo.Tests.csproj` (add `FlaUI.UIA3` 5.0.0)
- Create: `tests/Kipo.Tests/UiTests.cs`

**Interfaces:**
- Consumes: `Kipo.exe` AutomationIds from Task 10, `TestVideos`, `AppSettings`, `SettingsStore`

Takes over the mouse and keyboard (user approved). Needs an unlocked desktop. Run after `dotnet build src/Kipo/Kipo.csproj -p:Platform=x64`; if `Kipo.exe` is missing the tests are Inconclusive.

- [ ] **Step 1: Add FlaUI**

In `tests/Kipo.Tests/Kipo.Tests.csproj` add to the package `ItemGroup`:
```xml
    <PackageReference Include="FlaUI.UIA3" Version="5.0.0" />
```

- [ ] **Step 2: Write the tests**

`tests/Kipo.Tests/UiTests.cs`:
```csharp
using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
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
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
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

    private void WaitForRows(int count, string? status = null, TimeSpan? timeout = null)
    {
        var ok = Retry.WhileFalse(() =>
        {
            var rows = ById("QueueList").FindAllChildren();
            return rows.Length == count && (status is null || rows.All(r => r.FindAllDescendants().Any(t => t.Name.StartsWith(status, StringComparison.Ordinal))));
        }, timeout ?? Short, TimeSpan.FromMilliseconds(250)).Result;

        Assert.IsTrue(ok, $"Expected {count} rows with status '{status}'");
    }

    private Window OpenExplorer(string selectPath)
    {
        Process.Start("explorer.exe", $"/select,\"{selectPath}\"")?.Dispose();

        var folderName = Path.GetFileName(Path.GetDirectoryName(selectPath));
        var explorer   = Retry.WhileNull(() => _automation!.GetDesktop().FindFirstChild(cf => cf.ByName(folderName!))?.AsWindow(), Short).Result;

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

    [STATestMethod]
    [Timeout(600_000)]
    public void DragFromExplorer_Compresses()
    {
        var source = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        Launch(new AppSettings { UseCustom = true, CustomMb = 1 });

        var explorer = OpenExplorer(source);

        try
        {
            var file = Retry.WhileNull(() => explorer.FindFirstDescendant(cf => cf.ByName(Path.GetFileName(source)).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))), Short).Result
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
            explorer.Focus();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
            Thread.Sleep(500);
        }
        finally
        {
            explorer.Close();
        }

        _window.Focus();
        ById("QueueList").Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);

        WaitForRows(2, "Already under target");
    }

    [STATestMethod]
    [Timeout(300_000)]
    public void Browse_PickTwoFiles_Queues()
    {
        var sources = TestVideos.Batch.Take(2).Select(name => TestVideos.CopyToTemp(_dir, name)).ToArray();
        Launch(new AppSettings { PresetMb = 10 });

        ById("BrowseButton").Click();

        var dialog = Retry.WhileNull(() => _app!.GetAllTopLevelWindows(_automation!).SelectMany(w => w.ModalWindows.Append(w)).FirstOrDefault(w => w.ClassName == "#32770"), Short).Result
            ?? throw new AssertFailedException("File picker didn't open");
        var nameBox = dialog.FindFirstDescendant(cf => cf.ByAutomationId("1148")).AsTextBox();

        nameBox.Text = _dir;
        Keyboard.Type(VirtualKeyShort.ENTER);
        Thread.Sleep(1000);
        nameBox.Text = string.Join(" ", sources.Select(s => $"\"{Path.GetFileName(s)}\""));
        Keyboard.Type(VirtualKeyShort.ENTER);

        WaitForRows(2, "Already under target");
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
        var first  = TestVideos.CopyToTemp(_dir, TestVideos.Small);
        Launch(new AppSettings { UseCustom = true, CustomMb = 1, CopyToClipboard = true });

        // On: result lands on clipboard
        PasteText(first);
        WaitForRows(1, "Done", Long);
        Assert.IsTrue(Retry.WhileFalse(() => ClipboardHasFile(), Short).Result, "Result not on clipboard");

        // Off: clipboard keeps the pasted text
        ById("SettingsButton").Click();
        ById("CopyToClipboardToggle").AsToggleButton().Toggle();
        Keyboard.Type(VirtualKeyShort.ESCAPE);

        PasteText(first);
        WaitForRows(2, "Done", Long);
        Thread.Sleep(1000);
        Assert.IsFalse(ClipboardHasFile(), "Clipboard changed with setting off");
    }

    private void PasteText(string path)
    {
        var package = new DataPackage();
        package.SetText(path);
        Clipboard.SetContent(package);
        Clipboard.Flush();

        _window.Focus();
        ById("QueueList").Click();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
    }

    private static bool ClipboardHasFile() => Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
}
```

- [ ] **Step 3: Build app, run UI tests**

Run:
```bash
dotnet build src/Kipo/Kipo.csproj -p:Platform=x64
dotnet test tests/Kipo.Tests --filter TestCategory=UI
```
Expected: 5 pass. Don't touch the mouse while they run. Likely snags and fixes:
- Explorer window name isn't the folder name (depends on "show full path in title bar"): fall back to matching `ClassName == "CabinetWClass"` and newest window.
- Combo item text: WinUI exposes `ComboBoxItem` names from the string content; if `Select("Custom")` fails, `Expand()` then click the item found by name.
- File picker name box AutomationId `1148` is stable for the common dialog; if not found, use the first `Edit` under a `ComboBox` named "File name:".
- WinUI list rows: if `QueueList.FindAllChildren()` includes non-row elements, filter by `ControlType.ListItem`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "test: add UI automation tests for drag, paste, picker and settings"
```

---

### Task 12: Packaging (MSIX + MSI) and build script

**Files:**
- Create: `src/Kipo.Installer/Kipo.Installer.wixproj`, `src/Kipo.Installer/Package.wxs`
- Create: `build.ps1`

**Interfaces:**
- Consumes: `src/Kipo/Kipo.csproj` with `KipoPackaging=Unpackaged|Msix`

- [ ] **Step 1: WiX project**

`src/Kipo.Installer/Kipo.Installer.wixproj`:
```xml
<Project Sdk="WixToolset.Sdk/5.0.2">
  <PropertyGroup>
    <OutputName>Kipo-$(InstallerPlatform)</OutputName>
    <InstallerPlatform Condition="'$(InstallerPlatform)' == ''">x64</InstallerPlatform>
    <DefineConstants>PublishDir=$(PublishDir)</DefineConstants>
    <!-- WiX warnings aren't C# analyzer warnings; keep them visible but not fatal -->
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

`src/Kipo.Installer/Package.wxs`:
```xml
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
    <Package Name="Kipo"
             Manufacturer="Devin Green"
             Version="1.0.0"
             UpgradeCode="6F2C8A3E-4B1D-4E7A-9C55-2D8B1F0A7E43"
             Scope="perMachine">

        <MajorUpgrade DowngradeErrorMessage="A newer version of Kipo is already installed." />
        <MediaTemplate EmbedCab="yes" />

        <!-- App Files -->
        <StandardDirectory Id="ProgramFiles6432Folder">
            <Directory Id="INSTALLFOLDER" Name="Kipo">
                <Files Include="$(PublishDir)**" />
            </Directory>
        </StandardDirectory>

        <!-- Start Menu Shortcut -->
        <StandardDirectory Id="ProgramMenuFolder">
            <Component Id="StartMenuShortcut">
                <Shortcut Id="KipoShortcut"
                          Name="Kipo"
                          Description="Compress videos to a target size."
                          Target="[INSTALLFOLDER]Kipo.exe"
                          WorkingDirectory="INSTALLFOLDER" />
                <RegistryValue Root="HKLM"
                               Key="Software\Kipo"
                               Name="StartMenuShortcut"
                               Type="integer"
                               Value="1"
                               KeyPath="yes" />
            </Component>
        </StandardDirectory>
    </Package>
</Wix>
```

Add to solution: `dotnet sln Kipo.slnx add src/Kipo.Installer/Kipo.Installer.wixproj`. If adding it makes `dotnet test` try to build it without `PublishDir`, remove it from the solution instead; `build.ps1` builds it directly.

- [ ] **Step 2: Build script**

`build.ps1`:
```powershell
# =============================================================================
# Kipo build script
#
# Outputs:  dist\msix\  signed MSIX per platform
#           dist\msi\   MSI per platform (self-contained, runs on a clean PC)
# Used for: producing installable packages locally.
# Needs:    .NET 10 SDK (https://dotnet.microsoft.com/download). WiX and the MSIX
#           tooling come in through NuGet during the build.
#
# The MSIX is signed with a self-signed dev certificate (CN=Kipo Dev) created on
# first run in .certs\ (git-ignored). To install that MSIX, trust the certificate:
#   Import-PfxCertificate .certs\kipo-dev.pfx -CertStoreLocation Cert:\LocalMachine\TrustedPeople
# Swap in a real code-signing certificate for release.
# =============================================================================

param(
    [string[]] $Platforms     = @('x64', 'arm64'),
    [string]   $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

# Dev Signing Certificate
$certFile     = Join-Path $root '.certs\kipo-dev.pfx'
$certPassword = if ($env:KIPO_CERT_PASSWORD) { $env:KIPO_CERT_PASSWORD } else { 'kipo-dev' }

if (-not (Test-Path $certFile)) {
    New-Item -ItemType Directory -Force (Split-Path $certFile) | Out-Null

    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject 'CN=Kipo Dev' `
        -KeyUsage DigitalSignature `
        -FriendlyName 'Kipo Dev' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

    Export-PfxCertificate -Cert $cert -FilePath $certFile -Password (ConvertTo-SecureString $certPassword -AsPlainText -Force) | Out-Null
}

foreach ($platform in $Platforms) {
    $rid             = "win-$platform"
    $msbuildPlatform = if ($platform -eq 'arm64') { 'ARM64' } else { 'x64' }
    $publishDir      = Join-Path $root "artifacts\publish\$rid\"

    # MSIX
    dotnet publish "$root\src\Kipo\Kipo.csproj" -c $Configuration -r $rid `
        -p:Platform=$msbuildPlatform `
        -p:KipoPackaging=Msix `
        -p:PackageCertificateKeyFile=$certFile `
        -p:PackageCertificatePassword=$certPassword `
        -p:AppxPackageDir="$dist\msix\"
    if ($LASTEXITCODE) { throw "MSIX build failed for $platform" }

    # Unpackaged, self-contained
    dotnet publish "$root\src\Kipo\Kipo.csproj" -c $Configuration -r $rid -p:Platform=$msbuildPlatform -o $publishDir
    if ($LASTEXITCODE) { throw "Unpackaged publish failed for $platform" }

    # MSI
    dotnet build "$root\src\Kipo.Installer\Kipo.Installer.wixproj" -c $Configuration `
        -p:InstallerPlatform=$platform `
        -p:PublishDir=$publishDir `
        -o "$dist\msi\"
    if ($LASTEXITCODE) { throw "MSI build failed for $platform" }
}

Write-Host "Done. Packages are in $dist"
```

- [ ] **Step 3: Run it**

Run: `pwsh -File build.ps1 -Platforms x64`
Expected: `dist\msix\...\Kipo_1.0.0.0_x64.msix` and `dist\msi\Kipo-x64.msi`. Then `pwsh -File build.ps1` for both platforms.

Likely snags:
- MSIX via `dotnet publish` may need `-p:GenerateAppxPackageOnBuild=true` passed on the command line, or `dotnet build` instead of publish. Use whichever produces the `.msix`.
- WiX `Files` wildcard can't express `$(PublishDir)` with a trailing slash quirk: if files come out empty, use `$(PublishDir)\**` or `!(bindpath.publish)` with `-bindpath`. Confirm the MSI contains `Kipo.exe` with `msiexec /a dist\msi\Kipo-x64.msi /qn TARGETDIR=<temp>`.
- ARM64 cross-build on x64: MSI and MSIX build fine; can't be smoke-run here.

- [ ] **Step 4: Verify installers (x64)**

1. MSI: `msiexec /i dist\msi\Kipo-x64.msi /qn` (needs admin), check `C:\Program Files\Kipo\Kipo.exe` launches from the Start menu, then `msiexec /x dist\msi\Kipo-x64.msi /qn` and check the folder and shortcut are gone.
2. MSIX: trust the dev cert (command in build.ps1 header, admin), `Add-AppxPackage <path>.msix`, launch from Start, `Remove-AppxPackage` by name.
Report results; anything needing admin is handed to the user if the session can't elevate.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "build: package Kipo as MSIX and MSI"
```

---

### Task 13: Final verification

- [ ] **Step 1:** `dotnet build Kipo.slnx -p:Platform=x64` — zero warnings, zero errors.
- [ ] **Step 2:** `dotnet test tests/Kipo.Tests` — all categories pass (unit, Video, UI).
- [ ] **Step 3:** Walk the spec top to bottom and tick each requirement against the running app. List anything missing.
- [ ] **Step 4:** Request a whole-branch code review (superpowers:requesting-code-review), fix findings, re-run tests, commit.
