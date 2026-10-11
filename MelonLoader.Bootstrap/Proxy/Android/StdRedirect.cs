#if ANDROID
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Text;

namespace MelonLoader.Bootstrap.Proxy.Android;

/// <summary>
/// Warpforge Revival change. When the runtime hits a native crash it prints a crash report and should then
/// pass the crash on to Android. On a Pixel 6 Pro the report itself kept crashing and starting over, for
/// minutes: the game froze on its loading screen and the phone's log filled with hundreds of megabytes of
/// the same report. Now the first crash line starts a short timer; the report gets a few seconds to be
/// written (it says where the crash happened), only the first part of it is kept, and then the game is
/// closed. A closed game can simply be started again.
/// </summary>
internal static class CrashWatch
{
    [DllImport("libc.so")] private static extern int getpid();
    [DllImport("libc.so")] private static extern int kill(int pid, int sig);
    [DllImport("libc.so")] private static extern int sigaction(int sig, IntPtr act, IntPtr oldact);
    [DllImport("libc.so")] private static extern void abort();
    [DllImport("libc.so")] private static extern int tgkill(int tgid, int tid, int sig);
    [DllImport("libc.so")] private static extern int gettid();

    // The thread that crashed keeps re-running the runtime's broken crash report, so it is the busiest
    // thread: the one whose CPU time grows most over a second. Android's report is made from the thread
    // that receives the abort, so the abort is sent to that one.
    private static (int tid, string name) BusiestThread(int skip1, int skip2)
    {
        static Dictionary<int, long> Times()
        {
            var d = new Dictionary<int, long>();
            foreach (var dir in Directory.GetDirectories("/proc/self/task"))
            {
                try
                {
                    int tid = int.Parse(Path.GetFileName(dir));
                    string stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var f = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                    d[tid] = long.Parse(f[11]) + long.Parse(f[12]);       // utime + stime
                }
                catch { }
            }
            return d;
        }
        try
        {
            var a = Times();
            Thread.Sleep(1000);
            var b = Times();
            int best = 0; long most = -1;
            foreach (var kv in b)
            {
                if (kv.Key == skip1 || kv.Key == skip2 || !a.TryGetValue(kv.Key, out long was)) continue;
                if (kv.Value - was > most) { most = kv.Value - was; best = kv.Key; }
            }
            string name = "";
            try { name = File.ReadAllText($"/proc/self/task/{best}/comm").Trim(); } catch { }
            return (best, name);
        }
        catch { return (0, ""); }
    }

    private static int watcherTid;
    [DllImport("liblog.so")] private static extern int __android_log_write(int prio, string tag, string text);

    private const int SIGKILL = 9, SIGABRT = 6;

    // Android's own crash handler for SIGABRT, saved before the game's runtimes put theirs in place.
    // Closing the game through it makes Android write its full crash report ("tombstone": every
    // thread's call stack, including the thread that crashed), which the log script collects from
    // the phone's crash reports. The bytes are kept as they are (no need to know the struct's layout).
    private static IntPtr androidAbortHandler, androidSegvHandler, androidBusHandler;
    private const int SIGBUS = 7, SIGSEGV = 11;

    private static IntPtr SaveHandler(int sig)
    {
        IntPtr buf = Marshal.AllocHGlobal(256);
        for (int i = 0; i < 256; i += 8) Marshal.WriteInt64(buf, i, 0);
        if (sigaction(sig, IntPtr.Zero, buf) == 0) return buf;
        Marshal.FreeHGlobal(buf);
        return IntPtr.Zero;
    }

    /// <summary>Called first thing at start-up, before IL2CPP and .NET install their own handlers.</summary>
    public static void SaveAndroidHandler()
    {
        try
        {
            androidAbortHandler = SaveHandler(SIGABRT);
            androidSegvHandler = SaveHandler(SIGSEGV);
            androidBusHandler = SaveHandler(SIGBUS);
        }
        catch { }
    }
    private const int KeepLines = 400;
    private const int CloseAfterMs = 4000;
    private static int crashed, lines;

    private static readonly string[] Markers =
    {
        "Got a SIGSEGV", "Got a SIGBUS", "Got a SIGILL", "Got a SIGFPE", "Got a SIGABRT",
        "a fatal error in the mono runtime", "* Assertion:",
    };

    /// <summary>True when the line should still go to the log.</summary>
    public static bool Line(string text)
    {
        if (Volatile.Read(ref crashed) != 0)
            return Interlocked.Increment(ref lines) <= KeepLines;
        foreach (var m in Markers)
        {
            if (!text.Contains(m, StringComparison.Ordinal)) continue;
            if (Interlocked.Exchange(ref crashed, 1) != 0) break;
            __android_log_write(6, "MelonLoader", "[crash watch] the loader's .NET crashed (" + m.Trim('*', ' ', ':') + "); the game will be closed in a few seconds");
            int logTid = 0;
            try { logTid = gettid(); } catch { }
            var closer = new Thread(() =>
            {
                int me = 0;
                try { me = gettid(); } catch { }
                var (crashedTid, crashedName) = BusiestThread(me, logTid);
                Thread.Sleep(Math.Max(0, CloseAfterMs - 1000));
                __android_log_write(6, "MelonLoader", $"[crash watch] closing the game now ({Volatile.Read(ref lines)} crash report lines, the first {KeepLines} kept)");
                // through Android's crash handler, so it writes its full crash report; killed outright if that
                // does not end the game within 5 seconds
                var fallback = new Thread(() => { Thread.Sleep(5000); kill(getpid(), SIGKILL); }) { IsBackground = true };
                fallback.Start();
                if (androidAbortHandler != IntPtr.Zero)
                {
                    // The crashed thread keeps re-running the runtime's broken crash report, faulting again on every
                    // round. With Android's own handlers for memory faults put back, its next fault goes to Android,
                    // which writes its full report with that thread's stack: the crash itself is further down it.
                    // (Sending the thread an abort instead ended the game at once with no report, 21:25.)
                    __android_log_write(6, "MelonLoader", $"[crash watch] the crashed thread looks like {crashedTid} ({crashedName}); Android's fault handlers put back so its next fault writes Android's crash report");
                    if (androidSegvHandler != IntPtr.Zero) sigaction(SIGSEGV, androidSegvHandler, IntPtr.Zero);
                    if (androidBusHandler != IntPtr.Zero) sigaction(SIGBUS, androidBusHandler, IntPtr.Zero);
                    sigaction(SIGABRT, androidAbortHandler, IntPtr.Zero);
                    Thread.Sleep(3000);
                    __android_log_write(6, "MelonLoader", "[crash watch] no fault came; closing from here instead");
                    abort();
                }
                kill(getpid(), SIGKILL);
            }) { IsBackground = true };
            closer.Start();
            break;
        }
        return true;
    }
}

internal static class StdRedirect
{
    private const int STDERR_FILENO = 2;
    private const int STDOUT_FILENO = 1;

    [DllImport("libc.so", SetLastError = true)]
    private static extern int pipe(int[] pipefd);

    [DllImport("libc.so", SetLastError = true)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc.so", SetLastError = true)]
    private static extern IntPtr fdopen(int fd, string mode);

    [DllImport("libc.so", SetLastError = true)]
    private static extern IntPtr fgets(byte[] buffer, int size, IntPtr stream);

    [DllImport("liblog.so", SetLastError = true)]
    private static extern int __android_log_write(int prio, string tag, string text);

    public static void RedirectStdErr()
    {
        Environment.SetEnvironmentVariable("COREHOST_TRACE", "1");
        Environment.SetEnvironmentVariable("COREHOST_TRACE_VERBOSITY", "3");

        RedirectStream(STDERR_FILENO, "MelonLoader");
    }

    public static void RedirectStdOut()
    {
        Environment.SetEnvironmentVariable("COREHOST_TRACE", "1");
        Environment.SetEnvironmentVariable("COREHOST_TRACE_VERBOSITY", "3");

        RedirectStream(STDOUT_FILENO, "MelonLoader");
    }

    private static void RedirectStream(int fileno, string tag)
    {
        int[] pipes = new int[2];
        if (pipe(pipes) != 0)
        {
            Console.WriteLine("Failed to create pipe");
            return;
        }

        dup2(pipes[1], fileno);
        IntPtr inputFile = fdopen(pipes[0], "r");

        Thread logThread = new Thread(() =>
        {
            byte[] buffer = new byte[512];
            while (true)
            {
                IntPtr result = fgets(buffer, buffer.Length, inputFile);
                if (result == IntPtr.Zero)
                    break;

                // only up to the line's end: the buffer still holds the rest of any longer line before it
                int len = Array.IndexOf(buffer, (byte)0);
                string logMsg = Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len).TrimEnd('\n', '\r');
                if (CrashWatch.Line(logMsg))
                    __android_log_write(3, tag, logMsg); // debug
            }
        });

        logThread.IsBackground = true;
        logThread.Start();
    }
}
#endif
