# Kipo Video Compressor

A small, native Windows app that compresses videos to a size you pick, using the video encoder built into Windows.

## Introduction

Drop, paste (Ctrl+V), or browse for videos, pick a target size (8, 10, 25, 50, 100 MB or your own), and Kipo saves `name-compressed.mp4` next to each original. You can also right-click videos in File Explorer and pick **Compress with Kipo**. It's built with WinUI 3 (Windows App SDK) and Windows' own `MediaTranscoder`, so there's nothing extra to install.

### Prerequisites

- Windows 10 (2004) or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 or later with the **Desktop development with C++** workload, including the ARM64 build tools (the right-click add-in is compiled to native code)
- The Windows 10/11 SDK (comes with Visual Studio)

## Building

From the repo root, run:

```powershell
pwsh -File build.ps1
```

That's it! You'll find the packages in `dist\`:

- `dist\msix\` — MSIX packages for x64 and ARM64
- `dist\msi\` — MSI installers for x64 and ARM64

To build just one platform, pass `-Platforms x64` or `-Platforms arm64`.

## Installing

### MSIX

Trust the Kipo certificate once (see [Right-click menu](#right-click-menu)), then run:

```powershell
Add-AppxPackage dist\msix\Kipo_1.0.0.0_x64_Test\Kipo_1.0.0.0_x64.msix
```

If Windows asks for the Windows App SDK runtime, it's in the `Dependencies` folder next to the MSIX.

### MSI

Run `dist\msi\Kipo-x64.msi` (it needs admin). Kipo installs to `Program Files\Kipo` with a Start menu shortcut, and uninstalls from **Settings > Apps**.

## Right-click menu

Right-click one or more videos (`.mp4`, `.mov`, `.mkv`, `.avi`, `.wmv`, `.m4v`) and pick **Compress with Kipo**, then a size. A small Kipo window compresses them; **Custom…** asks for the size first.

> **You need the Kipo signing certificate trusted for the right-click menu to work.** The MSIX won't install without it, and the MSI's Windows 11 main-menu entry needs it too (the MSI's "Show more options" entry works without it). Trust it once, as admin:
>
> ```powershell
> Import-Certificate .certs\kipo-dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
> ```
>
> Release builds should be signed with a real code-signing certificate instead.

For MSI installs, the Windows 11 main-menu entry turns on the first time you open Kipo. If the certificate isn't trusted, Kipo notes it in `%LOCALAPPDATA%\Kipo\log.txt` and you still get the entry under **Show more options**.

## Testing

Run everything with:

```powershell
dotnet test tests/Kipo.Tests
```

> The UI tests take over your mouse and keyboard, and the right-click test opens File Explorer. Don't touch the mouse while they run. Build the app first (`dotnet build src/Kipo/Kipo.csproj -p:Platform=x64`) and publish the add-in (`build.ps1`) or those tests are skipped.

Video tests use sample recordings from the folder in the `KIPO_TEST_VIDEOS` environment variable.
