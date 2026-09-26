using System.Runtime.InteropServices;
using Kipo.Core;

namespace Kipo.Tests;

/// <summary>
/// Test run setup: keeps tests out of your real Kipo log, and makes the test process
/// aware of display scaling so UI tests click where they aim on scaled monitors.
/// </summary>
[TestClass]
public static partial class TestSetup
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        Log.FilePath = Path.Combine(Path.GetTempPath(), "kipo-tests", "log.txt");
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(IntPtr value);
}
