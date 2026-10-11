using MelonLoader.Bootstrap.Utils;
using System.Runtime.InteropServices;

namespace MelonLoader.Bootstrap.RuntimeHandlers.Il2Cpp;

internal static class Il2CppHandler
{
    private static Action? startFunc; // Prevent GC

    private static Il2CppLib il2cpp = null!;
#if ANDROID
    private static ClrMonoLib mono = null!;
#endif
    private static bool il2cppInitDone;
    private static bool invokeStarted;

    private const string DOTNET_VERSION =
#if !ANDROID
        "net6";
#else
        "net6";
#endif

    public static void Initialize(nint handle)
    {
        var il2cppLib = Il2CppLib.TryLoad(handle);
        if (il2cppLib is null)
        {
            Core.Logger.Error("Could not load il2cpp");
            return;
        }

        il2cpp = il2cppLib;

#if ANDROID
        var monoLib = ClrMonoLib.TryLoad();
        if (monoLib is null)
        {
            Core.Logger.Error("Could not load CoreCLR Mono");
            return;
        }
        mono = monoLib;
#endif
    }

    internal static nint InitDetour(nint a)
    {
        if (il2cppInitDone)
            return il2cpp.Init(a);

        ConsoleHandler.ResetHandles();
        MelonDebug.Log("In init detour");

        var domain = il2cpp.Init(a);

        InitializeManaged();
        il2cppInitDone = true;

        return domain;
    }

#if ANDROID
    [DllImport("libc.so", EntryPoint = "setenv")]
    private static extern int SetEnv(string name, string value, int overwrite);

    // Warpforge Revival change: settings for the loader's .NET memory, read before the runtime starts.
    // On a Pixel 6 Pro the runtime crashed now and then while allocating during start-up. The settings
    // come from UserData/runtime-gc.txt, which the mod writes, so a new setting can be tried with a
    // mod update instead of patching the game again; it takes effect at the next start. Only two
    // runtime variables are read from it (MONO_GC_PARAMS and MONO_GC_DEBUG), each line
    // "NAME=value" with a short plain value; anything else in the file is ignored. Without the
    // file the first memory area ("nursery") is made 64 MB instead of 4 MB, so the runtime has to
    // clean up far less often while the mod starts. A variable that is already set is kept.
    private const string DefaultGcParams = "nursery-size=64m";
    private static readonly System.Text.RegularExpressions.Regex SafeValue = new(@"^[A-Za-z0-9=,.\-]{1,200}$");

    private static void SetRuntimeMemory()
    {
        var wanted = new Dictionary<string, string> { ["MONO_GC_PARAMS"] = DefaultGcParams };
        string source = "built-in";
        try
        {
            string file = Path.Combine(LoaderConfig.Current.Loader.BaseDirectory, "UserData", "runtime-gc.txt");
            if (File.Exists(file) && new FileInfo(file).Length < 4096)
            {
                source = "UserData/runtime-gc.txt";
                wanted.Clear();
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.Length == 0 || line.StartsWith('#') || eq <= 0) continue;
                    string name = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    if ((name == "MONO_GC_PARAMS" || name == "MONO_GC_DEBUG") && SafeValue.IsMatch(value)) wanted[name] = value;
                    else Core.Logger.Msg($"Runtime memory settings: ignored line '{line}'");
                }
            }
        }
        catch (Exception e) { Core.Logger.Error("Could not read the runtime memory settings: " + e.Message); }

        foreach (var kv in wanted)
        {
            try
            {
                string? had = Environment.GetEnvironmentVariable(kv.Key);
                if (string.IsNullOrEmpty(had)) SetEnv(kv.Key, kv.Value, 0);
                Core.Logger.Msg($"Runtime memory settings ({source}): {kv.Key}={(string.IsNullOrEmpty(had) ? kv.Value : had + " (already set)")}");
            }
            catch (Exception e) { Core.Logger.Error("Could not set " + kv.Key + ": " + e.Message); }
        }
        if (wanted.Count == 0) Core.Logger.Msg($"Runtime memory settings ({source}): runtime defaults");
    }
#endif

    private static void InitializeManaged()
    {
#if ANDROID
        SetRuntimeMemory();
#endif
        var managedDir = Path.Combine(LoaderConfig.Current.Loader.BaseDirectory, "MelonLoader", DOTNET_VERSION);
        var runtimeConfigPath = Path.Combine(managedDir, "MelonLoader.runtimeconfig.json");
        var nativeHostPath = Path.Combine(managedDir, "MelonLoader.NativeHost.dll");

        if (!File.Exists(runtimeConfigPath))
        {
            Core.Logger.Error($"Runtime config not found at: '{runtimeConfigPath}'");
            return;
        }

        if (!File.Exists(nativeHostPath))
        {
            Core.Logger.Error($"NativeHost not found at: '{runtimeConfigPath}'");
            return;
        }

        // TODO: lazy workaround so I don't have to upgrade everything to net8
        string runtimeConfig = File.ReadAllText(runtimeConfigPath);
        if (runtimeConfig.Contains("net6"))
        {
            runtimeConfig = runtimeConfig.Replace("6.0", "8.0");
            File.WriteAllText(runtimeConfigPath, runtimeConfig);
        }

        MelonDebug.Log("Attempting to load Hostfxr");
        if (!Dotnet.LoadHostfxr())
        {
            DotnetInstaller.AttemptInstall();
            if (!Dotnet.LoadHostfxr())
            {
                Core.Logger.Error("Failed to load Hostfxr");
                return;
            }
        }

        MelonDebug.Log("Initializing domain");
        if (!Dotnet.InitializeForRuntimeConfig(runtimeConfigPath, out var context))
        {
            DotnetInstaller.AttemptInstall();
            if (!Dotnet.InitializeForRuntimeConfig(runtimeConfigPath, out context))
            {
                Core.Logger.Error($"Failed to initialize a .NET domain");
                return;
            }
        }

        MelonDebug.Log("Loading NativeHost assembly");
        var initialize = Dotnet.LoadAssemblyAndGetFunctionUco<InitializeFn>(context, nativeHostPath, "MelonLoader.NativeHost.NativeEntryPoint, MelonLoader.NativeHost", "NativeEntry");
        if (initialize == null)
        {
            Core.Logger.Error($"Failed to load assembly from: '{nativeHostPath}'");
            return;
        }

        var startFuncPtr = Core.LibraryHandle;

#if ANDROID
        ApplyMonoPatches();
#endif

        MelonDebug.Log("Invoking NativeHost entry");
        initialize(ref startFuncPtr);

        if (startFuncPtr == 0 || startFuncPtr == Core.LibraryHandle)
        {
            Core.Logger.Error($"Managed did not return the initial function pointer");
            return;
        }

        startFunc = Marshal.GetDelegateForFunctionPointer<Action>(startFuncPtr);
    }

    internal static nint InvokeDetour(nint method, nint obj, nint args, nint exc)
    {
        if (invokeStarted)
            return il2cpp.RuntimeInvoke(method, obj, args, exc);

        var result = il2cpp.RuntimeInvoke(method, obj, args, exc);

        var name = il2cpp.GetMethodName(method);
        if (name == null || !name.Contains("Internal_ActiveSceneChanged"))
            return result;

        invokeStarted = true;
        MelonDebug.Log("Invoke hijacked");

#if ANDROID
        mono.ThreadSuspendReload();
        MelonDebug.Log("Mono thread reset");
#endif

        Start();

        return result;
    }

    private static void Start()
    {
        startFunc?.Invoke();
    }

    private static unsafe void NativeHookAttachImpl(nint* target, nint detour)
    {
        *target = Dobby.HookAttach(*target, detour);
    }

    private static unsafe void NativeHookDetachImpl(nint* target, nint detour)
    {
        Dobby.HookDetach(*target);
    }

#if ANDROID
    private static void ApplyMonoPatches()
    {
        MelonDebug.Log("Applying Mono runtime patches");

        mono.SetLevelString("warning");
        mono.SetMaskString("all");

        MelonDebug.Log("Enabled Mono logging");

        mono.InstallUnhandledExceptionHook(MonoUnhandledExceptionHandler, IntPtr.Zero);

        MelonDebug.Log("Installed unhandled exception hook");

        mono.SetThreadChecker(MonoCheckThread);

        MelonDebug.Log("Installed thread checker");

        // Warpforge Revival change: without this the runtime answers a native crash by calling exit(),
        // which hangs the app with no report. With it, the crash is passed on to Android's own
        // crash reporter, so the app closes and leaves a report saying where it crashed.
        mono.SetSignalChaining?.Invoke(1);
        mono.SetCrashChaining?.Invoke(1);
        MelonDebug.Log($"Crash chaining {(mono.SetCrashChaining != null ? "enabled" : "not available")}");
    }

    private static void MonoUnhandledExceptionHandler(IntPtr exc, IntPtr userData)
    {
        if (exc == IntPtr.Zero)
            return;

        mono.PrintUnhandledException(exc);
    }

    private static bool MonoCheckThread(ulong threadId)
    {
        // Warpforge Revival change: this is always asked about the thread it is called on, so
        // "is this thread attached to IL2CPP" can be answered directly when the list is not exported.
        if (il2cpp.GetAllAttachedThreads == null)
            return il2cpp.ThreadCurrent!() == 0;

        UIntPtr size = 0;

        IntPtr threads = il2cpp.GetAllAttachedThreads(ref size);
        IntPtr[] threadsSlice = new IntPtr[(int)size];
        Marshal.Copy(threads, threadsSlice, 0, threadsSlice.Length);

        for (int i = 0; i < threadsSlice.Length; i++)
        {
            Il2CppThread thread = Marshal.PtrToStructure<Il2CppThread>(threadsSlice[i]);
            IntPtr internalThreadPtr = thread.internal_thread;

            if (internalThreadPtr != IntPtr.Zero)
            {
                Il2CppInternalThread internalThread = Marshal.PtrToStructure<Il2CppInternalThread>(internalThreadPtr);

                if (internalThread.tid == threadId)
                {
                    MelonDebug.Log($"Attached IL2CPP thread {internalThread.tid:X}");
                    return false;
                }
            }
        }

        return true;
    }
#endif

    // Requires the bootstrap handle to be passed first
    private delegate void InitializeFn(ref nint startFunc);
}
