/*
 * Warpforge Revival change: a first look at memory faults (SIGSEGV, SIGBUS) on Android.
 *
 * The loader's .NET runtime (Mono) handles these signals itself: in managed code a fault is how a
 * NullReferenceException starts, so those must reach Mono. For a fault in native code Mono prints a
 * crash report instead, and on a Pixel 6 Pro that report kept crashing and restarting (hundreds of
 * thousands of lines), froze the game, and never said which address faulted. This handler sits in
 * front of Mono's:
 *   - fault in managed (JIT) code  -> passed straight to Mono's handler, as before;
 *   - fault in native code         -> one line to the log with the signal, the faulting address, the
 *                                     program counter and the memory mappings around both, then
 *                                     Android's own handler is put back and the fault re-runs, so
 *                                     Android writes its full crash report and ends the game at once.
 * Everything in here is written to be safe inside a signal handler: no allocation, no locks, plain
 * system calls only. The one Mono call made (is this address JIT code?) is the same one Mono's own
 * handler makes.
 */
#define _GNU_SOURCE
#include <signal.h>
#include <string.h>
#include <unistd.h>
#include <fcntl.h>
#include <stdint.h>
#include <sys/syscall.h>
#include <sys/ucontext.h>
#include <dlfcn.h>

typedef void *(*domain_get_fn)(void);
typedef void *(*jit_find_fn)(void *, void *);

static domain_get_fn mono_domain_get_p;
static jit_find_fn mono_jit_info_table_find_p;
static struct sigaction prev_segv, prev_bus;          /* Mono's handlers, installed before this one */
static struct sigaction android_segv, android_bus;    /* Android's, saved before any runtime started */
static int have_android_segv, have_android_bus;
static volatile int reported;
static void (*log_write)(int, const char *, const char *);   /* liblog, looked up at install time */

static void out(const char *s)
{
    if (log_write) log_write(6 /* error */, "MelonLoader", s);
    write(2, s, strlen(s));
    write(2, "\n", 1);
}

static char *put(char *p, const char *s) { while (*s) *p++ = *s++; return p; }
static char *put_hex(char *p, uint64_t v)
{
    char tmp[17]; int n = 0;
    do { int d = v & 15; tmp[n++] = (char)(d < 10 ? '0' + d : 'a' + d - 10); v >>= 4; } while (v);
    *p++ = '0'; *p++ = 'x';
    while (n) *p++ = tmp[--n];
    return p;
}
static char *put_dec(char *p, int64_t v)
{
    char tmp[21]; int n = 0; uint64_t u = v < 0 ? (uint64_t)(-v) : (uint64_t)v;
    if (v < 0) *p++ = '-';
    do { tmp[n++] = (char)('0' + u % 10); u /= 10; } while (u);
    while (n) *p++ = tmp[--n];
    return p;
}

static uint64_t parse_hex(const char **pp)
{
    const char *p = *pp; uint64_t v = 0;
    for (;;) {
        char c = *p;
        if (c >= '0' && c <= '9') v = v * 16 + (c - '0');
        else if (c >= 'a' && c <= 'f') v = v * 16 + (c - 'a' + 10);
        else break;
        p++;
    }
    *pp = p;
    return v;
}

/* Logs the /proc/self/maps lines that hold `addr` and `pc`, and the lines just below and above `addr`. */
static void show_maps(uint64_t addr, uint64_t pc)
{
    int fd = open("/proc/self/maps", O_RDONLY | O_CLOEXEC);
    if (fd < 0) { out("[fault] /proc/self/maps could not be opened"); return; }
    static char buf[8192], line[512], prev[512], msg[640];
    int lpos = 0, found_addr = 0, prev_valid = 0, shown = 0;
    for (;;) {
        int n = read(fd, buf, sizeof buf);
        if (n <= 0) break;
        for (int i = 0; i < n; i++) {
            char c = buf[i];
            if (c != '\n') { if (lpos < (int)sizeof line - 1) line[lpos++] = c; continue; }
            line[lpos] = 0;
            const char *p = line;
            uint64_t start = parse_hex(&p);
            uint64_t end = (*p == '-') ? (p++, parse_hex(&p)) : 0;
            int holds_addr = start <= addr && addr < end, holds_pc = start <= pc && pc < end;
            const char *why = 0;
            if (holds_addr && holds_pc) why = "fault address and code";
            else if (holds_addr) why = "fault address";
            else if (holds_pc) why = "code";
            else if (!found_addr && start > addr) why = "first mapping above the fault address";
            if (holds_addr) found_addr = 1;
            if (why && shown < 8) {
                if (why[0] == 'f' && why[1] == 'i' && prev_valid) {      /* the gap: show the mapping below too */
                    char *m = put(msg, "[fault]   below the fault address: "); m = put(m, prev); *m = 0; out(msg); shown++;
                }
                char *m = put(msg, "[fault]   "); m = put(m, why); m = put(m, ": "); m = put(m, line); *m = 0; out(msg); shown++;
                if (why[0] == 'f' && why[1] == 'i') found_addr = 1;      /* reported the gap; do not repeat */
            }
            memcpy(prev, line, lpos + 1); prev_valid = 1;
            lpos = 0;
        }
    }
    close(fd);
    if (!found_addr) out("[fault]   the fault address is above every mapping");
}

static int in_managed_code(void *pc)
{
    if (!mono_domain_get_p || !mono_jit_info_table_find_p) return 0;
    void *domain = mono_domain_get_p();
    if (!domain) return 0;
    return mono_jit_info_table_find_p(domain, pc) != 0;
}

static void chain(const struct sigaction *prev, int sig, siginfo_t *si, void *ctx)
{
    if (prev->sa_flags & SA_SIGINFO) { if (prev->sa_sigaction) prev->sa_sigaction(sig, si, ctx); }
    else if (prev->sa_handler != SIG_DFL && prev->sa_handler != SIG_IGN) prev->sa_handler(sig);
}

static void handler(int sig, siginfo_t *si, void *ctx)
{
    ucontext_t *uc = (ucontext_t *)ctx;
    uint64_t pc = uc->uc_mcontext.pc, sp = uc->uc_mcontext.sp, lr = uc->uc_mcontext.regs[30];
    uint64_t addr = (uint64_t)(uintptr_t)si->si_addr;

    if (in_managed_code((void *)(uintptr_t)pc)) {
        chain(sig == SIGSEGV ? &prev_segv : &prev_bus, sig, si, ctx);
        return;
    }

    if (__sync_lock_test_and_set(&reported, 1) == 0) {
        static char msg[256];
        char *m = put(msg, "[fault] ");
        m = put(m, sig == SIGSEGV ? "SIGSEGV" : "SIGBUS");
        m = put(m, " (code "); m = put_dec(m, si->si_code); m = put(m, ") at address "); m = put_hex(m, addr);
        m = put(m, ", code at "); m = put_hex(m, pc); m = put(m, ", lr "); m = put_hex(m, lr); m = put(m, ", sp "); m = put_hex(m, sp);
        m = put(m, ", thread "); m = put_dec(m, (int64_t)syscall(SYS_gettid));
        *m = 0; out(msg);
        show_maps(addr, pc);
        out("[fault] handing the fault to Android for its crash report");
    }

    /* Android's handler back in place; returning re-runs the faulting instruction, which faults again. */
    if (sig == SIGSEGV && have_android_segv) sigaction(SIGSEGV, &android_segv, 0);
    else if (sig == SIGBUS && have_android_bus) sigaction(SIGBUS, &android_bus, 0);
    else { signal(sig, SIG_DFL); }
}

/* android_* are the struct sigaction bytes saved at start-up (may be null); runtime is a dlopen handle of libcoreclr.so. */
int wf_faultprobe_install(const void *android_segv_bytes, const void *android_bus_bytes, void *runtime)
{
    void *liblog = dlopen("liblog.so", RTLD_NOW);
    if (liblog) log_write = (void (*)(int, const char *, const char *))dlsym(liblog, "__android_log_write");
    if (runtime) {
        mono_domain_get_p = (domain_get_fn)dlsym(runtime, "mono_domain_get");
        mono_jit_info_table_find_p = (jit_find_fn)dlsym(runtime, "mono_jit_info_table_find");
    }
    if (!mono_domain_get_p || !mono_jit_info_table_find_p) return -1;     /* cannot tell managed from native: stay out */
    if (android_segv_bytes) { memcpy(&android_segv, android_segv_bytes, sizeof android_segv); have_android_segv = 1; }
    if (android_bus_bytes) { memcpy(&android_bus, android_bus_bytes, sizeof android_bus); have_android_bus = 1; }

    struct sigaction sa; memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = handler;
    sa.sa_flags = SA_SIGINFO | SA_ONSTACK | SA_NODEFER;
    sigemptyset(&sa.sa_mask);
    if (sigaction(SIGSEGV, &sa, &prev_segv) != 0) return -2;
    if (sigaction(SIGBUS, &sa, &prev_bus) != 0) { sigaction(SIGSEGV, &prev_segv, 0); return -3; }
    return 0;
}
