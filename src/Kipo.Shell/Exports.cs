using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

#pragma warning disable CA1031 // Code running inside Explorer catches everything and returns E_FAIL instead of crashing it

namespace Kipo.Shell;

/// <summary>
/// The DLL's entry points that Windows calls to create "Compress with Kipo".
/// </summary>
/// <remarks>
/// Explorer (through a COM surrogate) calls <see cref="DllGetClassObject"/> with Kipo's
/// class ID, gets a <see cref="ClassFactory"/>, and asks it for the root command. Every
/// path returns an HRESULT; nothing is allowed to throw back into Explorer.
/// </remarks>
internal static unsafe class Exports
{
    public static readonly Guid Clsid = new("6B1F4A52-8C3E-4E7B-9A2D-5C0F1E3B7A94");

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* clsid, Guid* riid, nint* ppv)
    {
        try
        {
            if (ppv == null)
            {
                return HResult.Pointer;
            }

            *ppv = 0;

            if (clsid == null || riid == null)
            {
                return HResult.InvalidArg;
            }

            if (*clsid != Clsid)
            {
                return HResult.ClassNotAvailable;
            }

            var hr = Com.Pointer(new ClassFactory(), *riid, out var pointer);
            *ppv   = pointer;

            return hr;
        }
        catch (Exception ex)
        {
            ShellLog.Write("DllGetClassObject failed", ex);

            return HResult.Fail;
        }
    }

    // Stay loaded: the surrogate process is short-lived anyway
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => HResult.False;
}

/// <summary>Creates <see cref="RootCommand"/> objects for Explorer.</summary>
[GeneratedComClass]
internal sealed partial class ClassFactory : IClassFactory
{
    public int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject)
    {
        ppvObject = 0;

        try
        {
            if (pUnkOuter != 0)
            {
                return HResult.NoAggregation;
            }

            return Com.Pointer(new RootCommand(), riid, out ppvObject);
        }
        catch (Exception ex)
        {
            ShellLog.Write("CreateInstance failed", ex);

            return HResult.Fail;
        }
    }

    public int LockServer(bool fLock) => HResult.Ok;
}
