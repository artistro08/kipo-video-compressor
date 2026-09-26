using Kipo.Core;

namespace Kipo;

/// <summary>
/// Right-click menu registration for MSI installs.
/// </summary>
/// <remarks>
/// The MSIX registers its menu through its manifest, so this only runs for the MSI build
/// (no package identity yet). It registers the signed Kipo.Sparse.msix shipped next to
/// Kipo.exe so Windows 11's main menu can load the add-in, and hides the duplicate classic
/// entry once that works. Needs the Kipo signing certificate trusted; otherwise the
/// failure is logged once and the classic "Show more options" menu keeps working.
/// </remarks>
public static class ShellRegistration
{
    private static string InstallFolder => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Registers in the background on launch of an MSI install; no-op otherwise.</summary>
    public static void EnsureInBackground()
    {
        if (HasPackageIdentity())
        {
            return;
        }

        _ = Task.Run(() => Create().EnsureRegisteredAsync(Path.Combine(InstallFolder, "Kipo.Sparse.msix"), InstallFolder));
    }

    /// <summary>Removes this user's right-click registration (MSI uninstall runs this).</summary>
    public static void Unregister() =>
        Task.Run(() => Create().UnregisterAsync()).GetAwaiter().GetResult();

    private static ShellRegistrar Create()
    {
        var registrar = new ShellRegistrar(new SparsePackages(), @"Software\Classes", Path.Combine(KipoPaths.DataDir, "shell-registration-failed"));

        registrar.Failed += (_, ex) => Log.Error("Right-click menu registration failed (is the Kipo certificate trusted?)", ex);

        return registrar;
    }

    // True for the MSIX, or an MSI install whose identity package is already registered
    private static bool HasPackageIdentity()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
