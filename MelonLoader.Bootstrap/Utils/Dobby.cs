using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MelonLoader.Bootstrap.Utils;

public static partial class Dobby
{
    [LibraryImport("*", EntryPoint = "DobbyHook")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Hook(nint target, nint detour, ref nint original);

    [LibraryImport("*", EntryPoint = "DobbyDestroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Destroy(nint target);

#if ANDROID
    // Warpforge Revival change. By default a hook overwrites the first 16 bytes of the function it
    // is placed on. Some game methods on ARM64 are shorter than that (a single jump to the next
    // method, 4 bytes), so the hook spilled over into the following method and broke it. With this
    // switched on a hook only overwrites 4 bytes: a short jump to a small relay placed close by.
    [LibraryImport("*", EntryPoint = "dobby_enable_near_branch_trampoline")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void EnableNearBranchTrampoline();

    [LibraryImport("*", EntryPoint = "dobby_disable_near_branch_trampoline")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DisableNearBranchTrampoline();

    // Warpforge Revival change (2). The 4-byte kind of hook needs its relay within reach of a short
    // jump. When no free memory is that close, the hooking library writes the relay into a run of
    // zero bytes inside the game's own code area instead - and some of those zeros are in use, so
    // the game then failed in random ways at start-up (missing methods, crashes), differently on
    // each launch. So the 4-byte kind is now used only for the few functions that really are too
    // short for a normal hook; everything else gets the normal 16-byte hook, which needs no relay.
    private static int nearState = -1;      // -1 not set yet, 0 off, 1 on
    private static int shortHooks, normalHooks;

    /// <summary>True when the function ends (returns or jumps away) within its first three instructions.</summary>
    private static unsafe bool IsShortFunction(nint target)
    {
        try
        {
            uint* code = (uint*)target;
            for (int i = 0; i < 3; i++)
            {
                uint insn = code[i];
                if (insn == 0xD65F03C0) return true;                      // ret
                if ((insn & 0xFC000000) == 0x14000000) return true;       // b <somewhere>
                if ((insn & 0xFFFFFC1F) == 0xD61F0000) return true;       // br xN
            }
        }
        catch { }
        return false;
    }
#endif

    public static nint HookAttach(nint target, nint detour)
    {
#if ANDROID
        bool wantNear = IsShortFunction(target);
        if (wantNear && nearState != 1) { EnableNearBranchTrampoline(); nearState = 1; }
        else if (!wantNear && nearState != 0) { DisableNearBranchTrampoline(); nearState = 0; }
        if (wantNear) { shortHooks++; MelonDebug.Log($"Short (4-byte) hook used for the function at {target:X} ({shortHooks} so far, {normalHooks} normal)"); }
        else normalHooks++;
#endif
        nint original = 0;
        if (Hook(target, detour, ref original) != 0)
        {
            throw new AccessViolationException($"Could not prepare patch to target {target:X}");
        }
        return original;
    }

    public static void HookDetach(nint target)
    {
        var result = Destroy(target);
        if (result is not 0 and not -1)
        {
            throw new AccessViolationException($"Could not destroy patch for target {target:X}");
        }
    }

    public static Patch<TDelegate> CreatePatch<TDelegate>(nint target, TDelegate detour) where TDelegate : Delegate
    {
        var original = HookAttach(target, Marshal.GetFunctionPointerForDelegate(detour));

        var originalDel = Marshal.GetDelegateForFunctionPointer<TDelegate>(original);

        return new Patch<TDelegate>(target, detour, originalDel);
    }

    public static Patch<TDelegate>? CreatePatch<TDelegate>(nint hModule, string functionName, TDelegate detour) where TDelegate : Delegate
    {
        return !NativeLibrary.TryGetExport(hModule, functionName, out var func) ? null : CreatePatch(func, detour);
    }

    public static Patch<TDelegate>? CreatePatch<TDelegate>(string moduleName, string functionName, TDelegate detour) where TDelegate : Delegate
    {
        return !NativeLibrary.TryLoad(moduleName, out var hModule) ? null : CreatePatch(hModule, functionName, detour);
    }

    public class Patch<T> where T : Delegate
    {
        public nint Target { get; private set; }
        public T Detour { get; private set; }
        public T Original { get; private set; }

        internal Patch(nint target, T detour, T original)
        {
            Target = target;
            Detour = detour;
            Original = original;
        }

        public void Destroy()
        {
            if (Target == 0)
                return;

            HookDetach(Target);
            Original = Marshal.GetDelegateForFunctionPointer<T>(Target);
            Target = 0;
        }
    }
}