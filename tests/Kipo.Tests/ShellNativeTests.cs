using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Kipo.Shell;

namespace Kipo.Tests;

/// <summary>
/// Loads the published native Kipo.Shell.dll the way Explorer does and walks the menu.
/// </summary>
/// <remarks>
/// Needs the add-in published first:
/// <c>dotnet publish src/Kipo.Shell -c Release -r win-x64 -o artifacts/shell/win-x64</c>.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed unsafe class ShellNativeTests
{
    private const int SOk                   = 0;
    private const int EPointer              = unchecked((int)0x80004003);
    private const int ClassNotAvailable     = unchecked((int)0x80040111);
    private const uint HasSubCommands       = 0x1;
    private const uint IsSeparator          = 0x8;

    private static readonly Guid Clsid             = new("6B1F4A52-8C3E-4E7B-9A2D-5C0F1E3B7A94");
    private static readonly Guid IidClassFactory   = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid IidExplorerCommand = new("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static nint _library;
    private static delegate* unmanaged<Guid*, Guid*, nint*, int> _getClassObject;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dll  = Path.Combine(root, "artifacts", "shell", "win-x64", "Kipo.Shell.dll");

        if (!File.Exists(dll))
        {
            Assert.Inconclusive($"Publish the add-in first: dotnet publish src/Kipo.Shell -c Release -r win-x64 -o artifacts/shell/win-x64 ({dll})");
        }

        _library        = NativeLibrary.Load(dll);
        _getClassObject = (delegate* unmanaged<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(_library, "DllGetClassObject");
    }

    private static IExplorerCommand CreateRoot()
    {
        var clsid = Clsid;
        var iid   = IidClassFactory;
        nint factoryPtr;

        Assert.AreEqual(SOk, _getClassObject(&clsid, &iid, &factoryPtr));

        var factory = (IClassFactory)Wrappers.GetOrCreateObjectForComInstance(factoryPtr, CreateObjectFlags.UniqueInstance);
        Marshal.Release(factoryPtr);

        Assert.AreEqual(SOk, factory.CreateInstance(0, IidExplorerCommand, out var commandPtr));

        var command = (IExplorerCommand)Wrappers.GetOrCreateObjectForComInstance(commandPtr, CreateObjectFlags.UniqueInstance);
        Marshal.Release(commandPtr);

        return command;
    }

    private static string Title(IExplorerCommand command)
    {
        Assert.AreEqual(SOk, command.GetTitle(0, out var text));

        try
        {
            return Marshal.PtrToStringUni(text) ?? "";
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static List<IExplorerCommand> SubCommands(IExplorerCommand root)
    {
        Assert.AreEqual(SOk, root.EnumSubCommands(out var enumPtr));

        var enumerator = (IEnumExplorerCommand)Wrappers.GetOrCreateObjectForComInstance(enumPtr, CreateObjectFlags.UniqueInstance);
        Marshal.Release(enumPtr);

        var commands = new List<IExplorerCommand>();

        while (true)
        {
            nint item;
            uint fetched;

            if (enumerator.Next(1, &item, &fetched) != SOk || fetched == 0)
            {
                break;
            }

            commands.Add((IExplorerCommand)Wrappers.GetOrCreateObjectForComInstance(item, CreateObjectFlags.UniqueInstance));
            Marshal.Release(item);
        }

        return commands;
    }

    [TestMethod]
    public void Root_HasTitleAndSubmenu()
    {
        var root = CreateRoot();

        Assert.AreEqual("Compress with Kipo", Title(root));
        Assert.AreEqual(SOk, root.GetFlags(out var flags));
        Assert.AreEqual(HasSubCommands, flags & HasSubCommands);
    }

    [TestMethod]
    public void Submenu_PresetsSeparatorThenCustom()
    {
        var items = SubCommands(CreateRoot());

        var titles = items.Select(c =>
        {
            c.GetFlags(out var f);
            return (f & IsSeparator) != 0 ? "---" : Title(c);
        }).ToArray();

        CollectionAssert.AreEqual(new[] { "8 MB", "10 MB", "25 MB", "50 MB", "100 MB", "---", "Custom…" }, titles);
    }

    [TestMethod]
    public void UnknownClass_IsRefused()
    {
        var clsid = Guid.NewGuid();
        var iid   = IidClassFactory;
        nint ppv;

        Assert.AreEqual(ClassNotAvailable, _getClassObject(&clsid, &iid, &ppv));
        Assert.AreEqual(0, ppv);
    }

    [TestMethod]
    public void NullOutPointer_IsRefused()
    {
        var clsid = Clsid;
        var iid   = IidClassFactory;

        Assert.AreEqual(EPointer, _getClassObject(&clsid, &iid, null));
    }

    [TestMethod]
    public void Invoke_WithNoSelection_ReturnsWithoutCrashing()
    {
        var size = SubCommands(CreateRoot())[0];

        // No items: nothing to launch, must just return an HRESULT
        var hr = size.Invoke(0, 0);

        Assert.IsTrue(hr == SOk || hr < 0, $"Unexpected HRESULT {hr:X8}");
    }
}
