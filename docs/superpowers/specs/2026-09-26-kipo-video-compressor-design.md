# Kipo Video Compressor — Design

Date: 2026-09-26

## Goal

A tiny, fully native Windows app that compresses videos to a target size in MB. You drop, paste, or browse for video files, pick a size, and Kipo compresses them next to the originals. Built on WinAppSDK 2.0 and the built-in Windows `MediaTranscoder` API. Ships as both MSIX and MSI.

## Decisions

| Topic | Decision |
|---|---|
| Language | C# on .NET 10 |
| UI | WinUI 3 (WinAppSDK 2.0; exact package version confirmed at plan time) |
| Encoder | `Windows.Media.Transcoding.MediaTranscoder`, H.264 + AAC in MP4 |
| Input | Drag-drop, paste (Ctrl+V), file picker. Multiple files. |
| Batch behavior | Queue, one file at a time. Cancel per file and cancel all. |
| Output | Next to source as `name-compressed.mp4` |
| Clipboard output | Single-file batch: result file copied to clipboard. Setting to turn off. |
| Packaging | One app project, two outputs: MSIX (packaged) and MSI (unpackaged, self-contained, WiX) |
| Tests | MSTest, written test-first alongside each piece |

## Approved Dependencies

- `Microsoft.WindowsAppSDK` (required for WinUI 3)
- WiX Toolset (MSI build only)
- MSTest (tests only)
- FlaUI (UI automation tests only)

Any other dependency must be flagged and approved before it's added.

## UI

- Window about 420×520, Mica background, title "Kipo".
- **Drop zone** at the top: "Drop, paste (Ctrl+V), or browse". Browse button opens a multi-select file picker filtered to `.mp4`, `.mov`, `.mkv`, `.avi`, `.wmv`, `.m4v`.
- **Target size combo box**: 8, 10, 25, 50, 100 MB, then "Custom" last. Picking Custom shows a number box (MB, min 1, max 10000). Hidden otherwise. Last choice is remembered.
- **Queue list**: one row per file with file name, status, and a cancel button. Statuses:
  - Waiting
  - Compressing N%
  - Done
  - Done (over target by X MB)
  - Already under target
  - Failed: reason
  - Canceled
- **Bottom bar**: "Cancel all" button and a gear button. Gear opens a flyout with one toggle: "Copy result to clipboard (single file)". Default on.
- Adding files starts processing immediately. There is no Start button.
- Files added while a batch runs join the end of the queue.

## Input Handling

- **Paste** accepts either copied files (e.g. from Explorer) or text containing one or more paths, one per line. Surrounding quotes are stripped and blank lines ignored.
- **Path validation** (applies to all input sources):
  - Must be a fully qualified local path (drive letter).
  - Rejected: URLs, relative paths, UNC paths (`\\server\share`), device paths (`\\?\`, `\\.\`).
  - File must exist.
  - Extension must be one of the supported video types.
- Rejected input still gets a queue row with "Failed: not found", "Failed: not a video", or "Failed: unsupported path" so you can see what happened.

## Compression Logic

1. Read duration, width, height, and frame rate from the file.
2. If the source file is already ≤ target size, skip it and mark "Already under target".
3. **Budget** = target MB × 1,048,576 × 8 × 0.95 bits (5% room for container overhead).
4. **Audio** = AAC 128 kbps. Drops to 64 kbps when the budget is tight (video bitrate would fall below the 360p minimum otherwise).
5. **Video bitrate** = budget ÷ duration seconds − audio bitrate.
6. **Auto downscale**: choose the largest of source / 1080p / 720p / 480p / 360p where bits-per-pixel (video bitrate ÷ (width × height × fps)) stays at or above a quality floor. Keep aspect ratio, round to even numbers, never upscale. Tiers compare against the short side so portrait video works the same way.
7. If the bitrate is still below the floor at 360p, fail with "Failed: target too small for this video length".
8. Encode with `MediaTranscoder`, hardware acceleration on, into a temp file `name-compressed.part` next to the source.
9. On success, rename to `name-compressed.mp4`. If taken, use `name-compressed (2).mp4`, `(3)`, and so on.
10. **Overshoot guard**: if the result is over target, retry once with the video bitrate scaled by target ÷ actual. Still over after retry: keep the file and mark "Done (over target by X MB)".
11. Cancel or failure deletes the `.part` file. The source file is never modified.
12. Transcoder progress feeds the row's percentage.

## Clipboard Output

When a batch that contained exactly one file finishes with a Done status and the setting is on, the output file is placed on the clipboard as a storage item, so it pastes straight into Explorer, Discord, etc.

## Settings

- Stored at `%LOCALAPPDATA%\Kipo\settings.json`. Works in both packaged and unpackaged builds.
- Fields: selected preset, custom MB value, copy-to-clipboard toggle.
- Missing or corrupt file loads defaults without crashing. Out-of-range values (e.g. custom MB of 0 or over 10000) are clamped or reset to defaults.

## Errors and Logging

- Every failure is shown in its queue row.
- Failures and unhandled exceptions are logged to `%LOCALAPPDATA%\Kipo\log.txt`. The log is capped in size (rolls over).
- Unhandled exceptions show a friendly dialog instead of a silent crash.

## Code Standards

- Nullable reference types on.
- .NET analyzers on, warnings treated as errors.
- Logic kept out of the UI so it can be tested without a window.
- All paths from outside input (clipboard text, drag-drop, picker) validated before use.

## Code Layout

- `Kipo/` — WinUI 3 app
  - `MainWindow` — UI only: drop zone, buttons, combo box, queue list, settings flyout.
  - `Compressor` — `MediaTranscoder` wrapper, bitrate math, downscale choice, overshoot retry.
  - `ClipboardInput` — reads pasted files or path text, writes the result file to the clipboard.
  - `Settings` — load/save JSON settings.
  - Queue and path validation logic live in small plain classes the UI calls.
- `Kipo.Tests/` — MSTest project.
- `Kipo.Installer/` — WiX project for the MSI.
- `build.ps1` — builds MSIX and MSI into `dist\`.

## Packaging

- **MSIX**: packaged build, x64 and arm64. Uses the WinAppSDK framework package. Signed with a dev test certificate locally; swap in a real certificate for release.
- **MSI**: unpackaged, self-contained build (.NET and WinAppSDK bundled), x64 and arm64. Installs per-machine to `Program Files\Kipo`, adds a Start menu shortcut, and uninstalls cleanly.

## Testing

Tests are written first for each piece as it's built.

**Unit tests** (no video files needed):

1. Bitrate math: budget, audio drop to 64 kbps, too-small failure.
2. Downscale choice: each tier, aspect ratio, even sizes, no upscale, portrait video.
3. Skip rule: source ≤ target.
4. Output naming: `-compressed`, `(2)`, `(3)` collisions.
5. Clipboard text path parsing: quotes, multiple lines, blanks, URLs, UNC, `\\?\`, relative paths, missing files, non-video extensions.
6. Settings: load, save, missing file, corrupt JSON, out-of-range values.
7. Queue: order, cancel one, cancel all, add while running.
8. Overshoot retry decision.

**Sample videos:**

- Real screen recordings are read from the folder in the `KIPO_TEST_VIDEOS` environment variable, defaulting to `C:\Users\artistro08\Videos\Screen Recordings`.
- Tests copy the files they need into a temp folder first. The originals are never modified and no output is written into the sample folder.
- If the folder or a file is missing, video tests are skipped (Inconclusive), not failed.
- Picks: a tiny file (~0.4 MB) for "Already under target", a mid file (~19 MB → 8 MB), a large file (~129 MB → 25 MB), and several small files for a batch.

**Integration tests** (real `MediaTranscoder`, sample videos):

9. Compress to a target and assert the output is ≤ target, the source is untouched, and no `.part` file remains.
10. Cancel mid-encode leaves no output and no `.part` file.
11. File already under target is skipped.
12. Multi-file batch runs in order and every output lands next to its source.

**Input handler tests** (no UI, real Windows data objects):

13. Clipboard: put real sample files on the Windows clipboard as storage items, read them through `ClipboardInput`. Same for path text.
14. Clipboard output: after a single-file Done, the clipboard holds the output file. With the setting off, the clipboard is unchanged.
15. Drag-drop: build a real `DataPackage` with sample files and pass its view to the drop handler (the same data object Windows hands the app on drop).
16. Picker: pass picked `StorageFile` items into the same add path the picker uses.

**UI automation tests** (FlaUI, real app, real mouse and keyboard, desktop session only):

17. Launch the app, drag a sample file from an Explorer window onto the drop zone, and see it compress.
18. Copy files in Explorer, focus the app, press Ctrl+V, and see them queue.
19. Click Browse, drive the real file picker to select several files, and see them queue.
20. Pick Custom in the combo box and see the number box appear; pick a preset and see it hide.
21. Toggle the clipboard setting and confirm the behavior changes.

**Manual checklist** (not automated):

- MSIX installs, runs, and uninstalls.
- MSI installs on a clean machine, runs, and uninstalls.

## Out of Scope

- Output formats other than MP4.
- Per-file target sizes.
- Parallel encoding.
- Network (UNC) paths.
- Trimming, cropping, or other editing.
