# Kipo Right-Click Menu — Design

Date: 2026-09-26

## Goal

Right-click one or more videos in File Explorer, pick "Compress with Kipo" and a size, and a small Kipo dialog compresses them. Works in both the Windows 11 main menu and the classic "Show more options" menu, for both the MSIX and MSI installs.

## Decisions

| Topic | Decision |
|---|---|
| Menus | Both: Windows 11 main menu and classic menu |
| Menu shape | "Compress with Kipo" → 8 MB, 10 MB, 25 MB, 50 MB, 100 MB, divider, Custom… |
| File types | .mp4, .mov, .mkv, .avi, .wmv, .m4v |
| Preset click | Dialog opens and starts compressing immediately |
| Custom click | Dialog asks for MB first, then compresses |
| Multiple files | One dialog lists them all |
| When done | Dialog stays open with results until closed |
| Settings | Dialog uses the main app's settings; "Copy result to clipboard" applies (single file only) |
| Dialog | Its own small window; runs whether or not the main window is open |
| Main-menu add-in | C# compiled to native code (Native AOT) as `Kipo.Shell.dll`, one per CPU (x64, ARM64) |
| New dependencies | None. Uses .NET 10's built-in COM source generators and Native AOT, with the Visual Studio C++ build tools already installed |

## 1. Menu and Launch

- Appears on right-click of the 6 video types, single or multiple selection.
- Menu: "Compress with Kipo" with the Kipo icon, opening a submenu: 8 MB, 10 MB, 25 MB, 50 MB, 100 MB, divider, Custom….
- The preset list comes from `AppSettings.Presets`, the same list the app uses.
- If non-videos are mixed in, the menu still shows; non-videos get a "Failed: not a video" row.
- A click runs `Kipo.exe --compress <mb|custom> <paths…>`.
- Paths go through `PathValidator` (local drive paths only; no network, device or stream paths).
- A malformed command line (unknown size, no paths) opens the main window instead. No crash.
- Single instance: if Kipo is already running, the new launch hands its request to the running copy and exits.
- Files arriving within 500 ms of each other join the same dialog (the classic menu launches once per file). A later right-click opens a new dialog.

## 2. Compress Dialog

- Separate small window: Mica, "Kipo Video Compressor" title, Kipo icon, XAML title bar with full-height window buttons, light and dark theme, height sized to content with animation (same rules as the main window).
- Preset: opens straight to the file list and starts compressing.
- Custom: opens to a form with a "Target size" MB box (pre-filled with the saved custom value) and a Compress button; Enter also compresses. Then switches to the file list with an animated transition.
- File list: the same component as the main window's list (video icon, spinner, checkmark, status text, Open file / Open in folder, per-row cancel, Cancel all while anything can be cancelled). The list is extracted into a shared component used by both windows.
- When finished, the dialog stays open. With a single file and the clipboard setting on, the result is copied and the "Copied to clipboard" banner shows.
- Closing while compressing asks "Stop compressing and close?" with Stop / Keep going. Stop cancels and deletes the unfinished `.part` file.
- No settings button in the dialog. A custom size entered in the dialog is saved as the custom size setting.

## 3. Add-in and Registration

### Add-in (`src/Kipo.Shell`)

- C# class library published with Native AOT to a native `Kipo.Shell.dll` per CPU (x64, ARM64).
- Exports `DllGetClassObject` and `DllCanUnloadNow`.
- Implements `IExplorerCommand` for "Compress with Kipo" and an `IEnumExplorerCommand` for its submenu, using .NET's COM source generators (`[GeneratedComInterface]`, `[GeneratedComClass]`).
- A click builds the command line and starts `Kipo.exe` from the add-in's own folder. It contains no compression logic.
- Every failure is caught and logged; Explorer never sees an exception.

### MSIX

- The manifest registers the add-in with `desktop4:FileExplorerContextMenus` for the 6 types, plus a `com:SurrogateServer` entry pointing at `Kipo.Shell.dll`.
- Windows shows it in both the main Windows 11 menu and the classic menu.

### MSI

- **Classic menu:** registry entries under `HKLM\Software\Classes\SystemFileAssociations\<ext>\shell\Kipo` create the cascading submenu. Each item runs `Kipo.exe --compress …`. Works on Windows 10 too.
- **Windows 11 main menu:** on first launch, Kipo registers a signed identity package (sparse package) with its external location set to the install folder, so Windows can load the same add-in.
  - Requires a signature the PC trusts. With the dev test certificate this only works where that certificate is trusted; a real code-signing certificate fixes it for release. The classic menu works regardless.
- **No duplicates:** after registering the identity package, Kipo sets a per-user registry override (`HKCU\...\shell\Kipo` with `LegacyDisable`) so the registry verb doesn't also appear in the classic menu.
- **Uninstall:** the MSI runs `Kipo.exe --unregister-shell` before removing files, which removes the identity package and the per-user override. The MSI removes its own registry entries.

### Signing

- Dev builds sign the MSIX and the identity package with the existing test certificate (`CN=Kipo Dev`). Swap in a real certificate for release.

## 4. Errors

- Add-in: failures (Kipo.exe missing, launch blocked) are caught and logged.
- Command line: malformed input opens the main window; bad paths become "Failed:" rows.
- Identity package registration failure (e.g. untrusted certificate) is logged; the classic menu stays.

## 5. Testing

Written test-first, per piece:

1. **Command-line parsing:** each preset, custom, quoted paths, many files, unknown size, no paths, network paths rejected.
2. **Grouping:** requests within 500 ms go to one dialog; a later request opens a new dialog.
3. **Add-in logic:** submenu matches `AppSettings.Presets` in order with Custom last; built command line is exact, including paths with spaces.
4. **Dialog UI tests** (FlaUI, take over the mouse): preset launch compresses immediately; Custom shows the MB box then compresses at that size; several files share one dialog; closing mid-compress asks; Open file / Open in folder work.
5. **Real Explorer tests** (FlaUI, take over the mouse): right-click a sample video, check "Compress with Kipo" and its 6 submenu items, pick 25 MB, confirm the dialog opens and compresses. Runs against the installed MSIX.
6. **Manual:** MSI install on a PC, classic menu, and main menu where the certificate is trusted.

## Out of Scope

- Per-file sizes in one dialog.
- Menu entries on non-video files.
- A settings button inside the dialog.
