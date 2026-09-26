using Kipo.Core;
using Microsoft.Win32;

namespace Kipo.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ShellRegistrarTests
{
    private const string TestClasses = @"Software\KipoTests\Classes";

    private string _dir = "";
    private string _msix = "";
    private FakePackages _packages = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir      = Directory.CreateTempSubdirectory("kipo-shellreg-").FullName;
        _msix     = Path.Combine(_dir, "Kipo.Sparse.msix");
        _packages = new FakePackages();
        File.WriteAllBytes(_msix, [0]);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\KipoTests", throwOnMissingSubKey: false);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\KipoTests", throwOnMissingSubKey: false);
        Directory.Delete(_dir, true);
    }

    private ShellRegistrar Registrar() => new(_packages, TestClasses, Path.Combine(_dir, "shell-registration-failed"));

    private static bool LegacyDisabled(string extension)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{TestClasses}\SystemFileAssociations\{extension}\shell\Kipo");
        return key?.GetValue("LegacyDisable") is not null;
    }

    [TestMethod]
    public async Task Ensure_Registers_AndHidesClassicEntries()
    {
        await Registrar().EnsureRegisteredAsync(_msix, _dir);

        Assert.AreEqual(1, _packages.RegisterCalls);
        Assert.AreEqual(_dir, _packages.ExternalFolder);
        Assert.IsTrue(PathValidator.VideoExtensions.All(LegacyDisabled), "Classic entry not hidden for every type");
    }

    [TestMethod]
    public async Task Ensure_AlreadyRegistered_DoesNothingButHide()
    {
        _packages.Registered = true;

        await Registrar().EnsureRegisteredAsync(_msix, _dir);

        Assert.AreEqual(0, _packages.RegisterCalls);
        Assert.IsTrue(PathValidator.VideoExtensions.All(LegacyDisabled));
    }

    [TestMethod]
    public async Task Ensure_Failure_LeavesClassicMenu_AndLogsOnce()
    {
        _packages.Fail = true;
        var logs       = 0;
        var registrar  = Registrar();
        registrar.Failed += (_, _) => logs++;

        await registrar.EnsureRegisteredAsync(_msix, _dir);
        await registrar.EnsureRegisteredAsync(_msix, _dir);

        Assert.IsFalse(PathValidator.VideoExtensions.Any(LegacyDisabled), "Classic entry hidden after a failure");
        Assert.AreEqual(1, logs, "Same failure reported more than once");
        Assert.AreEqual(2, _packages.RegisterCalls, "Didn't retry on the next launch");
    }

    [TestMethod]
    public async Task Ensure_AfterFailure_CertificateTrusted_Registers()
    {
        // First launch before the certificate is trusted, next launch after
        _packages.Fail = true;
        await Registrar().EnsureRegisteredAsync(_msix, _dir);

        _packages.Fail = false;
        await Registrar().EnsureRegisteredAsync(_msix, _dir);

        Assert.IsTrue(_packages.Registered, "Never registered once the certificate was trusted");
        Assert.IsTrue(PathValidator.VideoExtensions.All(LegacyDisabled));
    }

    [TestMethod]
    public async Task Ensure_NoSparsePackage_DoesNothing()
    {
        File.Delete(_msix);

        await Registrar().EnsureRegisteredAsync(_msix, _dir);

        Assert.AreEqual(0, _packages.RegisterCalls);
    }

    [TestMethod]
    public async Task Unregister_RemovesPackageAndOverrides()
    {
        await Registrar().EnsureRegisteredAsync(_msix, _dir);

        await Registrar().UnregisterAsync();

        Assert.IsFalse(_packages.Registered);
        Assert.IsFalse(PathValidator.VideoExtensions.Any(LegacyDisabled));
    }

    [TestMethod]
    public async Task Unregister_ClearsOverridesBeforeRemovingPackage()
    {
        // Removing the package ends Kipo's own process when it runs with that identity,
        // so everything else must already be cleaned up by then
        await Registrar().EnsureRegisteredAsync(_msix, _dir);

        var overridesAtRemoval = true;
        _packages.OnUnregister = () => overridesAtRemoval = PathValidator.VideoExtensions.Any(LegacyDisabled);

        await Registrar().UnregisterAsync();

        Assert.IsTrue(_packages.UnregisterCalled);
        Assert.IsFalse(overridesAtRemoval, "Overrides still present when the package was removed");
    }

    private sealed class FakePackages : ISparsePackages
    {
        public Action? OnUnregister { get; set; }

        public bool UnregisterCalled { get; private set; }

        public bool Registered { get; set; }

        public bool Fail { get; set; }

        public int RegisterCalls { get; private set; }

        public string? ExternalFolder { get; private set; }

        public bool IsRegistered() => Registered;

        public Task RegisterAsync(string msixPath, string externalFolder)
        {
            RegisterCalls++;
            ExternalFolder = externalFolder;

            if (Fail)
            {
                throw new InvalidOperationException("certificate not trusted");
            }

            Registered = true;
            return Task.CompletedTask;
        }

        public Task UnregisterAsync()
        {
            UnregisterCalled = true;
            OnUnregister?.Invoke();
            Registered = false;
            return Task.CompletedTask;
        }
    }
}
