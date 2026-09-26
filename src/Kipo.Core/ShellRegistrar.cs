using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Kipo.Core;

/// <summary>Registers the MSI install's identity (sparse) package with Windows.</summary>
public interface ISparsePackages
{
    /// <summary>True when the identity package is registered for this user.</summary>
    bool IsRegistered();

    /// <summary>Registers the identity package, pointing it at the install folder.</summary>
    /// <param name="msixPath">Signed Kipo.Sparse.msix.</param>
    /// <param name="externalFolder">Folder holding Kipo.exe and Kipo.Shell.dll.</param>
    /// <returns>A task that completes when registered.</returns>
    Task RegisterAsync(string msixPath, string externalFolder);

    /// <summary>Removes the identity package for this user.</summary>
    /// <returns>A task that completes when removed.</returns>
    Task UnregisterAsync();
}

/// <summary>
/// Puts "Compress with Kipo" into Windows 11's main right-click menu for MSI installs.
/// </summary>
/// <remarks>
/// The MSI always installs the classic ("Show more options") menu through the registry.
/// Windows 11's main menu only loads add-ins from packaged apps, so on first launch Kipo
/// registers a small signed identity package that points at the install folder. When that
/// works, the classic registry entry is hidden for this user (a per-user <c>LegacyDisable</c>
/// override) so it doesn't show twice. Registration needs the signing certificate to be
/// trusted; until it is, every launch retries quietly (a marker file remembers the last
/// error so each one is reported once) and the classic menu keeps working.
/// </remarks>
/// <param name="packages">Package manager access; tests pass a fake.</param>
/// <param name="classesPath">HKCU path of the per-user classes key (<c>Software\Classes</c>).</param>
/// <param name="failureMarker">File that remembers a failed registration.</param>
public sealed class ShellRegistrar(ISparsePackages packages, string classesPath, string failureMarker)
{
    /// <summary>Fires once when registration fails, with the error.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>
    /// Registers the identity package if needed and hides the duplicate classic entry.
    /// </summary>
    /// <param name="msixPath">Signed Kipo.Sparse.msix in the install folder.</param>
    /// <param name="installFolder">The install folder.</param>
    /// <returns>A task that completes when done. Never throws.</returns>
    public async Task EnsureRegisteredAsync(string msixPath, string installFolder)
    {
        try
        {
            if (!packages.IsRegistered())
            {
                if (!File.Exists(msixPath))
                {
                    return;
                }

                await packages.RegisterAsync(msixPath, installFolder);
            }

            SetClassicHidden(true);
            ForgetFailure();
        }
#pragma warning disable CA1031 // Any registration failure leaves the classic menu in place
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Retry every launch (the certificate may be trusted later), but report each error once
            if (RememberFailure(ex.Message))
            {
                Failed?.Invoke(this, ex);
            }
        }
    }

    /// <summary>Removes the per-user overrides, then the identity package.</summary>
    /// <remarks>
    /// The package goes last: when Kipo runs with that package's identity, removing it ends
    /// Kipo's own process, so everything else has to be cleaned up first.
    /// </remarks>
    /// <returns>A task that completes when done. Never throws.</returns>
    public async Task UnregisterAsync()
    {
        try
        {
            SetClassicHidden(false);
            ForgetFailure();
        }
#pragma warning disable CA1031 // Uninstall cleanup is best effort
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failed?.Invoke(this, ex);
        }

        try
        {
            await packages.UnregisterAsync();
        }
#pragma warning disable CA1031 // Uninstall cleanup is best effort
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failed?.Invoke(this, ex);
        }
    }

    private void SetClassicHidden(bool hidden)
    {
        foreach (var extension in PathValidator.VideoExtensions)
        {
            var path = $@"{classesPath}\SystemFileAssociations\{extension}\shell\Kipo";

            if (hidden)
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("LegacyDisable", "");
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            }
        }
    }

    // Stores the error; returns true when it differs from the last one stored (so it's worth reporting)
    private bool RememberFailure(string error)
    {
        try
        {
            if (File.Exists(failureMarker) && File.ReadAllText(failureMarker) == error)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(failureMarker)!);
            File.WriteAllText(failureMarker, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker the error may be logged again next launch
        }

        return true;
    }

    private void ForgetFailure()
    {
        try
        {
            File.Delete(failureMarker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to clean up
        }
    }
}

/// <summary>
/// The real identity package registration, through Windows' package manager.
/// </summary>
public sealed class SparsePackages : ISparsePackages
{
    public const string Name      = "KipoSparse";
    public const string Publisher = "CN=Kipo Dev";

    private readonly PackageManager _manager = new();

    public bool IsRegistered() => _manager.FindPackagesForUser(string.Empty, Name, Publisher).Any();

    public async Task RegisterAsync(string msixPath, string externalFolder)
    {
        var options = new AddPackageOptions { ExternalLocationUri = new Uri(externalFolder) };
        var result  = await _manager.AddPackageByUriAsync(new Uri(msixPath), options);

        if (result.ExtendedErrorCode is not null)
        {
            throw new InvalidOperationException($"Identity package registration failed: {result.ErrorText}", result.ExtendedErrorCode);
        }
    }

    public async Task UnregisterAsync()
    {
        foreach (var package in _manager.FindPackagesForUser(string.Empty, Name, Publisher).ToList())
        {
            await _manager.RemovePackageAsync(package.Id.FullName);
        }
    }
}
