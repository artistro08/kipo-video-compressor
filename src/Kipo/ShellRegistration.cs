namespace Kipo;

/// <summary>
/// Registers and removes the right-click menu for MSI installs.
/// </summary>
/// <remarks>
/// Filled in by the MSI task: registers the signed sparse package so Windows 11's main
/// menu can load the add-in, and hides the classic registry entry for this user once it
/// has. For now <see cref="Unregister"/> is a no-op so the uninstall command line works.
/// </remarks>
public static class ShellRegistration
{
    /// <summary>Removes this user's right-click registration.</summary>
    public static void Unregister()
    {
    }
}
