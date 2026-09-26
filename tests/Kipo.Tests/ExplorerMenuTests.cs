using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Windows.Management.Deployment;

namespace Kipo.Tests;

// Real right-click in File Explorer against the installed Kipo MSIX
public sealed partial class UiTests
{
    private static readonly string[] MenuSizes = ["8 MB", "10 MB", "25 MB", "50 MB", "100 MB", "Custom…"];

    [STATestMethod]
    [TestCategory("Shell")]
    [Timeout(300_000)]
    public void Explorer_RightClick_CompressWithKipo()
    {
        if (!new PackageManager().FindPackagesForUser(string.Empty).Any(p => p.Id.Name is "Kipo" or "KipoSparse"))
        {
            Assert.Inconclusive("Install the Kipo MSIX, or launch the MSI build once so it registers its identity package.");
        }

        var source = TestVideos.CopyToTemp(_dir, TestVideos.Tiny);
        _automation = new UIA3Automation();

        var explorer = OpenExplorer(source);

        try
        {
            var file = Retry.WhileNull(() => explorer.FindFirstDescendant(cf => cf.ByName(Path.GetFileName(source)).And(cf.ByControlType(ControlType.ListItem))), Short).Result
                ?? throw new AssertFailedException("File not visible in Explorer");

            explorer.SetForeground();
            Mouse.RightClick(file.GetClickablePoint());

            // Windows 11 menu: "Compress with Kipo" opens the size submenu
            var entry = Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Compress with Kipo")), Short).Result
                ?? throw new AssertFailedException("No \"Compress with Kipo\" in the right-click menu");

            entry.Click();

            var items = Retry.WhileNull(() =>
            {
                var found = MenuSizes.Select(name => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.MenuItem)))).ToList();
                return found.All(i => i is not null) ? found : null;
            }, Short).Result ?? throw new AssertFailedException("Submenu items missing");

            var inOrder = items.Select(i => i!.BoundingRectangle.Y).ToList();
            CollectionAssert.AreEqual(inOrder.OrderBy(y => y).ToList(), inOrder, "Submenu items out of order");

            items[2]!.Click();   // 25 MB

            // The installed Kipo opens its dialog
            var dialog = Retry.WhileNull(() => _automation.GetDesktop()
                .FindAllChildren(cf => cf.ByName("Kipo Video Compressor"))
                .FirstOrDefault(w => w.FindFirstDescendant(cf => cf.ByAutomationId("QueueList")) is not null), Short).Result
                ?? throw new AssertFailedException("Kipo dialog didn't open");

            var done = Retry.WhileFalse(() => dialog.FindAllDescendants().Any(e => (e.Properties.Name.ValueOrDefault ?? "") == "Already under target"), Short).Result;

            Assert.IsTrue(done, "Dialog didn't process the file");
            dialog.AsWindow().Close();
        }
        finally
        {
            explorer.Close();

            foreach (var kipo in Process.GetProcessesByName("Kipo"))
            {
                kipo.Kill();
                kipo.Dispose();
            }
        }
    }
}
