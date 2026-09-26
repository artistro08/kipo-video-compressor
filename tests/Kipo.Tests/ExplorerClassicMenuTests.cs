using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Windows.Management.Deployment;

namespace Kipo.Tests;

// "Show more options" (classic) right-click menu against the installed Kipo
public sealed partial class UiTests
{
    [STATestMethod]
    [TestCategory("Shell")]
    [Timeout(300_000)]
    public void Explorer_ClassicMenu_HasCompressWithKipo()
    {
        if (!new PackageManager().FindPackagesForUser(string.Empty).Any(p => p.Id.Name is "Kipo" or "KipoSparse"))
        {
            Assert.Inconclusive("Install the Kipo MSIX first.");
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

            var more = Retry.WhileNull(() => _automation.GetDesktop().FindFirstDescendant(cf => cf.ByName("Show more options")), Short).Result
                ?? throw new AssertFailedException("No \"Show more options\"");

            more.Click();

            var entry = Retry.WhileNull(() => _automation.GetDesktop()
                .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                .FirstOrDefault(m => (m.Properties.Name.ValueOrDefault ?? "").Contains("Compress with Kipo", StringComparison.Ordinal)), Short).Result;

            Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);

            Assert.IsNotNull(entry, "No \"Compress with Kipo\" under \"Show more options\"");
        }
        finally
        {
            explorer.Close();
        }
    }
}
