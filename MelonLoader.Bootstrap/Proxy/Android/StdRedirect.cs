#if ANDROID
using System.Runtime.InteropServices;
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
    [DllImport("liblog.so")] private static extern int __android_log_write(int prio, string tag, string text);

    private const int SIGKILL = 9;
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
            var closer = new Thread(() =>
            {
                Thread.Sleep(CloseAfterMs);
                __android_log_write(6, "MelonLoader", $"[crash watch] closing the game now ({Volatile.Read(ref lines)} crash report lines, the first {KeepLines} kept)");
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
