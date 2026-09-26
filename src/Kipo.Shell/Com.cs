using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Kipo.Shell;

/// <summary>HRESULT values the add-in returns.</summary>
internal static class HResult
{
    public const int Ok                = 0;
    public const int False             = 1;
    public const int NotImplemented    = unchecked((int)0x80004001);
    public const int Pointer           = unchecked((int)0x80004003);
    public const int Fail              = unchecked((int)0x80004005);
    public const int InvalidArg        = unchecked((int)0x80070057);
    public const int NoAggregation     = unchecked((int)0x80040110);
    public const int ClassNotAvailable = unchecked((int)0x80040111);
}

/// <summary>
/// Small COM helpers shared by the commands.
/// </summary>
/// <remarks>
/// One <see cref="StrategyBasedComWrappers"/> instance turns our C# objects into COM
/// pointers for Explorer and wraps Explorer's pointers so we can call them.
/// </remarks>
internal static class Com
{
    public static StrategyBasedComWrappers Wrappers { get; } = new();

    /// <summary>Gets a COM pointer for <paramref name="instance"/> as interface <paramref name="iid"/>.</summary>
    /// <param name="instance">A [GeneratedComClass] object.</param>
    /// <param name="iid">Interface requested.</param>
    /// <param name="ppv">The pointer, or 0 on failure.</param>
    /// <returns>HRESULT from QueryInterface.</returns>
    public static int Pointer(object instance, in Guid iid, out nint ppv)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);

        try
        {
            return Marshal.QueryInterface(unknown, in iid, out ppv);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Hands a string to Explorer as a CoTaskMem allocation (Explorer frees it).</summary>
    /// <param name="value">Text.</param>
    /// <param name="ppsz">Receives the allocation.</param>
    /// <returns>S_OK.</returns>
    public static int String(string value, out nint ppsz)
    {
        ppsz = Marshal.StringToCoTaskMemUni(value);

        return HResult.Ok;
    }

    /// <summary>Wraps a borrowed COM pointer as <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Interface to wrap as.</typeparam>
    /// <param name="pointer">Pointer, or 0.</param>
    /// <returns>Wrapper, or null for 0.</returns>
    public static T? Wrap<T>(nint pointer)
        where T : class =>
        pointer == 0 ? null : (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
}

/// <summary>
/// Finds the folder this DLL sits in, where Kipo.exe and its icon live too.
/// </summary>
internal static unsafe partial class ModuleInfo
{
    private const uint FromAddress         = 0x4;
    private const uint UnchangedRefCount   = 0x2;

    private static readonly Lazy<string> LazyFolder = new(Find);

    public static string Folder => LazyFolder.Value;

    public static string KipoExe => Path.Combine(Folder, "Kipo.exe");

    private static string Find()
    {
        var address = (nint)(delegate* unmanaged<Guid*, Guid*, nint*, int>)&Exports.DllGetClassObject;

        if (!GetModuleHandleExW(FromAddress | UnchangedRefCount, address, out var module))
        {
            return AppContext.BaseDirectory;
        }

        var buffer = stackalloc char[1024];
        var length = GetModuleFileNameW(module, buffer, 1024);

        return length == 0 ? AppContext.BaseDirectory : Path.GetDirectoryName(new string(buffer, 0, (int)length)) ?? AppContext.BaseDirectory;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetModuleFileNameW(nint module, char* buffer, uint size);
}

/// <summary>
/// Best-effort log for the add-in at <c>%LOCALAPPDATA%\Kipo\shell-log.txt</c>. Never throws.
/// </summary>
internal static class ShellLog
{
    private static readonly Lock Gate = new();

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kipo");
            var entry  = $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}" + (exception is null ? "" : exception + Environment.NewLine);

            lock (Gate)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "shell-log.txt"), entry);
            }
        }
#pragma warning disable CA1031 // Logging inside Explorer must never throw
        catch
#pragma warning restore CA1031
        {
            // Nowhere left to report this
        }
    }
}
