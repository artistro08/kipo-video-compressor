using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kipo.Shell;

// Windows Shell COM interfaces, declared with .NET's COM source generator so they work
// under Native AOT. Every method returns its HRESULT (PreserveSig) and strings come back
// as CoTaskMem pointers, exactly as Explorer expects.

/// <summary>A command in Explorer's right-click menu.</summary>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-iexplorercommand"/>
[GeneratedComInterface]
[Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
internal partial interface IExplorerCommand
{
    [PreserveSig]
    int GetTitle(nint psiItemArray, out nint ppszName);

    [PreserveSig]
    int GetIcon(nint psiItemArray, out nint ppszIcon);

    [PreserveSig]
    int GetToolTip(nint psiItemArray, out nint ppszInfotip);

    [PreserveSig]
    int GetCanonicalName(out Guid pguidCommandName);

    [PreserveSig]
    int GetState(nint psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCmdState);

    [PreserveSig]
    int Invoke(nint psiItemArray, nint pbc);

    [PreserveSig]
    int GetFlags(out uint pFlags);

    [PreserveSig]
    int EnumSubCommands(out nint ppEnum);
}

/// <summary>Enumerates the submenu commands.</summary>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ienumexplorercommand"/>
[GeneratedComInterface]
[Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8")]
internal unsafe partial interface IEnumExplorerCommand
{
    [PreserveSig]
    int Next(uint celt, nint* pUICommand, uint* pceltFetched);

    [PreserveSig]
    int Skip(uint celt);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(out nint ppenum);
}

/// <summary>The files the user right-clicked.</summary>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishellitemarray"/>
[GeneratedComInterface]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
internal partial interface IShellItemArray
{
    [PreserveSig]
    int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppvOut);

    [PreserveSig]
    int GetPropertyStore(int flags, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetPropertyDescriptionList(nint keyType, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int GetCount(out uint pdwNumItems);

    [PreserveSig]
    int GetItemAt(uint dwIndex, out nint ppsi);

    [PreserveSig]
    int EnumItems(out nint ppenumShellItems);
}

/// <summary>One right-clicked file.</summary>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishellitem"/>
[GeneratedComInterface]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig]
    int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);

    [PreserveSig]
    int GetParent(out nint ppsi);

    [PreserveSig]
    int GetDisplayName(uint sigdnName, out nint ppszName);

    [PreserveSig]
    int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

    [PreserveSig]
    int Compare(nint psi, uint hint, out int piOrder);
}

/// <summary>Creates the root command for Explorer.</summary>
[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint pUnkOuter, in Guid riid, out nint ppvObject);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}
