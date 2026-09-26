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
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

New-Item -ItemType Directory -Force (Split-Path $certFile) | Out-Null
Export-Certificate -Cert $cert -FilePath $certFile | Out-Null

foreach ($platform in $Platforms) {
    $rid             = "win-$platform"
    $msbuildPlatform = if ($platform -eq 'arm64') { 'ARM64' } else { 'x64' }
    $publishDir      = Join-Path $root "artifacts\publish\$rid\"

    # MSIX
    dotnet publish "$root\src\Kipo\Kipo.csproj" -c $Configuration -r $rid `
        -p:Platform=$msbuildPlatform `
        -p:KipoPackaging=Msix `
        -p:PackageCertificateThumbprint=$($cert.Thumbprint) `
        -p:AppxPackageDir="$dist\msix\"
    if ($LASTEXITCODE) { throw "MSIX build failed for $platform" }

    # Unpackaged, self-contained
    dotnet publish "$root\src\Kipo\Kipo.csproj" -c $Configuration -r $rid -p:Platform=$msbuildPlatform -o $publishDir
    if ($LASTEXITCODE) { throw "Unpackaged publish failed for $platform" }

    # MSI
    dotnet build "$root\src\Kipo.Installer\Kipo.Installer.wixproj" -c $Configuration `
        -p:Platform=$platform `
        -p:InstallerPlatform=$platform `
        -p:PublishDir=$publishDir `
        -o "$dist\msi\"
    if ($LASTEXITCODE) { throw "MSI build failed for $platform" }
}

Write-Host "Done. Packages are in $dist"
