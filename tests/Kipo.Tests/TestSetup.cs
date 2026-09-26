using Kipo.Core;

namespace Kipo.Tests;

/// <summary>
/// Keeps test runs from writing into your real Kipo log.
/// </summary>
[TestClass]
public static class TestSetup
{
    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        Log.FilePath = Path.Combine(Path.GetTempPath(), "kipo-tests", "log.txt");
    }
}
