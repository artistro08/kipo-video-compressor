# Kipo Video Compressor

Compress videos to the size you pick. A small, native Windows app that uses the video encoder built into Windows.

## Install

1. Download the files for your PC (x64 or ARM64) from [Releases](https://github.com/artistro08/kipo-video-compressor/releases).
2. Trust the Kipo certificate once. Open PowerShell as admin in the download folder and run:

   ```powershell
   Import-Certificate kipo-dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

3. Double-click the `.msix` to install. Prefer a classic installer? Run the `.msi` instead.

> **You need the Kipo certificate trusted for the right-click menu to work.** The MSIX won't install without it, and the MSI only shows the menu under "Show more options" until it's trusted.

## Use

- Drop, paste (Ctrl+V), or browse for videos, pick a size, and Kipo saves `name-compressed.mp4` next to each one.
- Or right-click videos in File Explorer and pick **Compress with Kipo**.

## Build

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Visual Studio's **Desktop development with C++** tools (x64 and ARM64). Then run:

```powershell
pwsh -File build.ps1
```

That's it! The packages land in `dist\`.

## License

[MIT](LICENSE)
