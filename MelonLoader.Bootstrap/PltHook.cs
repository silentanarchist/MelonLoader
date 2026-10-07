using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader.Bootstrap.Logging;

namespace MelonLoader.Bootstrap;

internal static partial class PltHook
{
    [LibraryImport("*", EntryPoint = "plthook_open", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int PlthookOpen(ref nint pltHookOut, string? filename);

    [LibraryImport("*", EntryPoint = "plthook_open_by_handle")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int PlthookOpenByHandle(ref nint pltHookOut, nint handle);

    [LibraryImport("*", EntryPoint = "plthook_open_by_address")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int PlthookOpenByAddress(ref nint pltHookOut, nint address);

    [LibraryImport("*", EntryPoint = "plthook_replace", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int PlthookReplace(nint pltHook, string funcName, nint funcAddr, nint oldFunc);

    [LibraryImport("*", EntryPoint = "plthook_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void PlthookClose(nint pltHook);

    [LibraryImport("*", EntryPoint = "plthook_enum", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int PlthookEnum(nint pltHook, ref uint pos, byte** name, byte*** addr);

    [LibraryImport("*", EntryPoint = "plthook_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint PlthookError();

#if !ANDROID
    private static readonly string? PlayerFileName = Process.GetCurrentProcess().Modules.OfType<ProcessModule>()
        .FirstOrDefault(x => x.FileName.Contains("UnityPlayer"))?.FileName;
#else
    private static readonly string? PlayerFileName = Process.GetCurrentProcess().Modules.OfType<ProcessModule>()
        .FirstOrDefault(x => x.FileName.Contains("libunity.so"))?.FileName;
#endif

    internal static void InstallHooks(List<(string functionName, nint hookFunctionPtr)> hooks)
    {
        nint pltHook = IntPtr.Zero;
        bool pltHookOpened;
#if OSX
        string parentPlayerPath = Path.GetDirectoryName(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName))!;
        string playerLibPath = Path.Combine(parentPlayerPath, "Frameworks", "UnityPlayer.dylib");
        if (File.Exists(playerLibPath))
        {
            nint libHandle = LibcNative.Dlopen(playerLibPath, LibcNative.RtldLazy | LibcNative.RtldNoLoad);
            if (libHandle == IntPtr.Zero)
            {
                Console.WriteLine($"Failed to dlopen {playerLibPath}, cannot apply plt hooks");
                return;
            }
            pltHookOpened = PlthookOpenByHandle(ref pltHook, libHandle) == 0;
        }
        else
        {
            pltHookOpened = PlthookOpen(ref pltHook, PlayerFileName) == 0;
        }
#elif ANDROID
        // Warpforge Revival change: on Android the library cannot be opened by name or handle (that
        // route looks for symbols libunity.so does not export), so open it by an address inside it.
        pltHookOpened = false;
        if (NativeLibrary.TryLoad("libunity.so", out nint unityLib)
            && NativeLibrary.TryGetExport(unityLib, "JNI_OnLoad", out nint insideUnity))
        {
            pltHookOpened = PlthookOpenByAddress(ref pltHook, insideUnity) == 0;
            if (!pltHookOpened)
                MelonLogger.LogError($"plthook_open_by_address error: {Marshal.PtrToStringAuto(PlthookError())}");
        }
        else
            MelonLogger.LogError("libunity.so or its JNI_OnLoad was not found; cannot hook it");
        if (!pltHookOpened)
            pltHookOpened = PlthookOpen(ref pltHook, PlayerFileName) == 0;
#else
        pltHookOpened = PlthookOpen(ref pltHook, PlayerFileName) == 0;
#endif

        if (!pltHookOpened)
        {
            MelonLogger.LogError($"plthook_open error: {Marshal.PtrToStringAuto(PlthookError())}");
            return;
        }

        foreach (var hook in hooks)
        {
            if (PlthookReplace(pltHook, hook.functionName, hook.hookFunctionPtr, IntPtr.Zero) != 0)
            {
                MelonLogger.LogError($"plthook_replace error when hooking {hook.functionName}: " +
                               $"{Marshal.PtrToStringAuto(PlthookError())}");
                continue;
            }

            MelonLogger.Log(MelonLoader.Logging.ColorARGB.Gray, $"Plt hooked {hook.functionName} successfully");
        }
        
        PlthookClose(pltHook);
    }
}