using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

#pragma warning disable CA1031 // Code running inside Explorer catches everything and returns E_FAIL instead of crashing it

namespace Kipo.Shell;

/// <summary>
/// "Compress with Kipo": the top-level right-click entry, with the size submenu.
/// </summary>
[GeneratedComClass]
internal sealed partial class RootCommand : IExplorerCommand
{
    public static readonly Guid CanonicalName = new("6B1F4A52-8C3E-4E7B-9A2D-5C0F1E3B7A94");

    private const uint HasSubCommands = 0x1;

    public int GetTitle(nint psiItemArray, out nint ppszName)
    {
        nint text = 0;
        var hr    = Guard(() => Com.String(ShellMenu.RootTitle, out text));

        ppszName = text;

        return hr;
    }

    public int GetIcon(nint psiItemArray, out nint ppszIcon)
    {
        nint text = 0;
        var hr    = Guard(() => Com.String($"{ModuleInfo.KipoExe},0", out text));

        ppszIcon = text;

        return hr;
    }

    public int GetToolTip(nint psiItemArray, out nint ppszInfotip)
    {
        ppszInfotip = 0;

        return HResult.NotImplemented;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = CanonicalName;

        return HResult.Ok;
    }

    public int GetState(nint psiItemArray, bool fOkToBeSlow, out uint pCmdState)
    {
        pCmdState = 0;   // ECS_ENABLED

        return HResult.Ok;
    }

    public int Invoke(nint psiItemArray, nint pbc) => HResult.Ok;

    public int GetFlags(out uint pFlags)
    {
        pFlags = HasSubCommands;

        return HResult.Ok;
    }

    public int EnumSubCommands(out nint ppEnum)
    {
        ppEnum = 0;

        var commands = new List<IExplorerCommand>();

        foreach (var item in ShellMenu.Items)
        {
            if (item.SeparatorBefore)
            {
                commands.Add(new SeparatorCommand());
            }

            commands.Add(new SizeCommand(item));
        }

        nint result = 0;
        var hr      = Guard(() => Com.Pointer(new CommandEnumerator(commands), typeof(IEnumExplorerCommand).GUID, out result));

        ppEnum = result;

        return hr;
    }

    internal static int Guard(Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            ShellLog.Write("Explorer command failed", ex);

            return HResult.Fail;
        }
    }
}

/// <summary>
/// One size in the submenu. Clicking it starts Kipo.exe with the size and the selected files.
/// </summary>
/// <param name="item">Menu entry.</param>
[GeneratedComClass]
internal sealed partial class SizeCommand(ShellMenuItem item) : IExplorerCommand
{
    private const uint FileSystemPath = 0x80058000;   // SIGDN_FILESYSPATH

    public int GetTitle(nint psiItemArray, out nint ppszName)
    {
        nint text = 0;
        var hr    = RootCommand.Guard(() => Com.String(item.Title, out text));

        ppszName = text;

        return hr;
    }

    public int GetIcon(nint psiItemArray, out nint ppszIcon)
    {
        ppszIcon = 0;

        return HResult.NotImplemented;
    }

    public int GetToolTip(nint psiItemArray, out nint ppszInfotip)
    {
        ppszInfotip = 0;

        return HResult.NotImplemented;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = Guid.Empty;

        return HResult.NotImplemented;
    }

    public int GetState(nint psiItemArray, bool fOkToBeSlow, out uint pCmdState)
    {
        pCmdState = 0;

        return HResult.Ok;
    }

    public int Invoke(nint psiItemArray, nint pbc) => RootCommand.Guard(() =>
    {
        foreach (var arguments in ShellMenu.CommandLines(item.Size, SelectedPaths(psiItemArray)))
        {
            using var process = Process.Start(new ProcessStartInfo(ModuleInfo.KipoExe, arguments) { UseShellExecute = false });
        }

        return HResult.Ok;
    });

    public int GetFlags(out uint pFlags)
    {
        pFlags = 0;

        return HResult.Ok;
    }

    public int EnumSubCommands(out nint ppEnum)
    {
        ppEnum = 0;

        return HResult.NotImplemented;
    }

    private static List<string> SelectedPaths(nint psiItemArray)
    {
        var paths = new List<string>();

        if (Com.Wrap<IShellItemArray>(psiItemArray) is not { } items || items.GetCount(out var count) != HResult.Ok)
        {
            return paths;
        }

        for (uint i = 0; i < count; i++)
        {
            if (items.GetItemAt(i, out var itemPointer) != HResult.Ok || itemPointer == 0)
            {
                continue;
            }

            try
            {
                var shellItem = Com.Wrap<IShellItem>(itemPointer)!;

                if (shellItem.GetDisplayName(FileSystemPath, out var name) == HResult.Ok && name != 0)
                {
                    paths.Add(Marshal.PtrToStringUni(name) ?? "");
                    Marshal.FreeCoTaskMem(name);
                }
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
        }

        return paths;
    }
}

/// <summary>The divider above "Custom…".</summary>
[GeneratedComClass]
internal sealed partial class SeparatorCommand : IExplorerCommand
{
    private const uint IsSeparator = 0x8;

    public int GetTitle(nint psiItemArray, out nint ppszName)
    {
        ppszName = 0;

        return HResult.NotImplemented;
    }

    public int GetIcon(nint psiItemArray, out nint ppszIcon)
    {
        ppszIcon = 0;

        return HResult.NotImplemented;
    }

    public int GetToolTip(nint psiItemArray, out nint ppszInfotip)
    {
        ppszInfotip = 0;

        return HResult.NotImplemented;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = Guid.Empty;

        return HResult.NotImplemented;
    }

    public int GetState(nint psiItemArray, bool fOkToBeSlow, out uint pCmdState)
    {
        pCmdState = 0;

        return HResult.Ok;
    }

    public int Invoke(nint psiItemArray, nint pbc) => HResult.Ok;

    public int GetFlags(out uint pFlags)
    {
        pFlags = IsSeparator;

        return HResult.Ok;
    }

    public int EnumSubCommands(out nint ppEnum)
    {
        ppEnum = 0;

        return HResult.NotImplemented;
    }
}

/// <summary>Hands the submenu commands to Explorer one at a time.</summary>
/// <param name="commands">Submenu commands in order.</param>
[GeneratedComClass]
internal sealed unsafe partial class CommandEnumerator(IReadOnlyList<IExplorerCommand> commands) : IEnumExplorerCommand
{
    private int _index;

    public int Next(uint celt, nint* pUICommand, uint* pceltFetched)
    {
        try
        {
            if (pUICommand == null)
            {
                return HResult.Pointer;
            }

            uint fetched = 0;

            while (fetched < celt && _index < commands.Count)
            {
                if (Com.Pointer(commands[_index], typeof(IExplorerCommand).GUID, out var pointer) != HResult.Ok)
                {
                    break;
                }

                pUICommand[fetched] = pointer;
                fetched++;
                _index++;
            }

            if (pceltFetched != null)
            {
                *pceltFetched = fetched;
            }

            return fetched == celt ? HResult.Ok : HResult.False;
        }
        catch (Exception ex)
        {
            ShellLog.Write("Submenu enumeration failed", ex);

            return HResult.Fail;
        }
    }

    public int Skip(uint celt)
    {
        _index = (int)Math.Min(commands.Count, _index + celt);

        return _index < commands.Count ? HResult.Ok : HResult.False;
    }

    public int Reset()
    {
        _index = 0;

        return HResult.Ok;
    }

    public int Clone(out nint ppenum)
    {
        ppenum = 0;

        return HResult.NotImplemented;
    }
}
