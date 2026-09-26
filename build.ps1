# =============================================================================
# Kipo build script
#
# Outputs:  dist\msix\  signed MSIX per platform
#           dist\msi\   MSI per platform (self-contained, runs on a clean PC)
#           dist\kipo-dev.cer  public signing certificate to trust
# Used for: producing installable packages locally.
# Needs:    .NET 10 SDK (https://dotnet.microsoft.com/download). WiX and the MSIX
#           tooling come in through NuGet during the build.
#
# The MSIX is signed with a self-signed dev certificate (CN=Kipo Dev) created on
# first run in your user certificate store; its public part is exported to
# .certs\kipo-dev.cer (git-ignored). To install that MSIX, trust it once (admin):
#   Import-Certificate .certs\kipo-dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
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
$certFile = Join-Path $root '.certs\kipo-dev.cer'
$cert     = Get-ChildItem 'Cert:\CurrentUser\My' |
    Where-Object { $_.Subject -eq 'CN=Kipo Dev' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject 'CN=Kipo Dev' `
        -KeyUsage DigitalSignature `
        -FriendlyName 'Kipo Dev' `
        -NotAfter (Get-Date).AddYears(10) `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

New-Item -ItemType Directory -Force (Split-Path $certFile) | Out-Null
Export-Certificate -Cert $cert -FilePath $certFile | Out-Null

# Windows SDK packaging tools (makeappx, signtool)
$timestamp = 'http://timestamp.digicert.com'
$sdkBin   = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object { Test-Path "$($_.FullName)\x64\makeappx.exe" } |
    Sort-Object Name -Descending |
    Select-Object -First 1
if (-not $sdkBin) { throw 'Windows SDK not found. Install it with the Visual Studio Installer.' }
$makeAppx = "$($sdkBin.FullName)\x64\makeappx.exe"
$signTool = "$($sdkBin.FullName)\x64\signtool.exe"

# Native AOT needs vswhere.exe on PATH (Visual Studio's vcvarsall calls it by name)
$env:PATH = "$env:PATH;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

foreach ($platform in $Platforms) {
    $rid             = "win-$platform"
    $msbuildPlatform = if ($platform -eq 'arm64') { 'ARM64' } else { 'x64' }
    $publishDir      = Join-Path $root "artifacts\publish\$rid\"

    # Right-click menu add-in (native DLL, bundled into both packages)
    dotnet publish "$root\src\Kipo.Shell\Kipo.Shell.csproj" -c $Configuration -r $rid -o "$root\artifacts\shell\$rid"
    if ($LASTEXITCODE) { throw "Right-click add-in build failed for $platform" }

    # MSIX
    dotnet publish "$root\src\Kipo\Kipo.csproj" -c $Configuration -r $rid `
        -p:Platform=$msbuildPlatform `
        -p:KipoPackaging=Msix `
        -p:PackageCertificateThumbprint=$($cert.Thumbprint) `
        -p:AppxPackageSigningTimestampServerUrl=$timestamp `
        -p:AppxPackageSigningTimestampDigestAlgorithm=SHA256 `
        -p:AppxPackageDir="$dist\msix\"
    if ($LASTEXITCODE) { throw "MSIX build failed for $platform" }

    # Unpackaged, self-contained (start clean so no leftover files ship in the MSI)
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish "$root\src\Kipo\Kipo.csproj" -c $Configuration -r $rid -p:Platform=$msbuildPlatform -o $publishDir
    if ($LASTEXITCODE) { throw "Unpackaged publish failed for $platform" }

    # Identity (sparse) package for the MSI's Windows 11 right-click menu, signed with the dev certificate
    $sparseStage = Join-Path $root "artifacts\sparse\$rid"
    Remove-Item $sparseStage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$sparseStage\Assets" | Out-Null
    Copy-Item "$root\src\Kipo.Installer\Sparse\AppxManifest.xml" $sparseStage
    Copy-Item "$root\src\Kipo\Assets\*.png" "$sparseStage\Assets"

    & $makeAppx pack /d $sparseStage /p "$publishDir\Kipo.Sparse.msix" /nv /o | Out-Null
    if ($LASTEXITCODE) { throw "Sparse package build failed for $platform" }

    & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint /tr $timestamp /td SHA256 "$publishDir\Kipo.Sparse.msix" | Out-Null
    if ($LASTEXITCODE) { throw "Sparse package signing failed for $platform" }

    # MSI (full rebuild: WiX doesn't notice when only the published files changed)
    dotnet build "$root\src\Kipo.Installer\Kipo.Installer.wixproj" -c $Configuration --no-incremental `
        -p:Platform=$platform `
        -p:InstallerPlatform=$platform `
        -p:PublishDir=$publishDir `
        -o "$dist\msi\"
    if ($LASTEXITCODE) { throw "MSI build failed for $platform" }
}

# Ship the public certificate next to the packages
Copy-Item $certFile $dist

Write-Host "Done. Packages are in $dist"
Write-Host ""
Write-Host "Right-click menu: you need the Kipo signing certificate trusted for it to work (and to install the MSIX)."
Write-Host "Trust it once, as admin:  Import-Certificate $certFile -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
Write-Host "Without it, MSI installs only get the menu under 'Show more options'."
