/* Must precede every system header: glibc declares pipe2 only with _GNU_SOURCE. */
#if defined(__linux__) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE 1
#endif

#include "minipty_unix_internal.h"

#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#ifndef PATH_MAX
#define PATH_MAX 4096
#endif

#if defined(__linux__)
#include <dirent.h>
#if defined(__has_include) && __has_include(<linux/close_range.h>)
#include <linux/close_range.h>
#endif
#include <sched.h>
#include <sys/mman.h>
#include <sys/syscall.h>
/* Linux 5.2, newer than the headers the shipped library is built against. Older kernels ignore the flag. */
#ifndef CLONE_PIDFD
#define CLONE_PIDFD 0x00001000
#endif
/* Linux 4.13. Older kernels refuse the request, and the slave is opened by its path instead. */
#ifndef TIOCGPTPEER
#define TIOCGPTPEER _IO('T', 0x41)
#endif
/*
 * The shipped library is built against old headers (glibc 2.27's, musl 1.2.3's), which predate close_range (Linux 5.9)
 * and its CLOEXEC flag (5.11). Without these the call would compile out and every start would scan the descriptor
 * table instead. A kernel without them fails the call, and the scan in minipty_scrub_inherited_fds takes over.
 */
#ifndef SYS_close_range
/* 436 on every architecture .NET runs on. Only alpha, ia64 and MIPS number it differently. */
#define SYS_close_range 436
#endif
#ifndef CLOSE_RANGE_CLOEXEC
#define CLOSE_RANGE_CLOEXEC (1U << 2)
#endif
#elif defined(__APPLE__)
#include <dlfcn.h>
#include <libgen.h>
#include <libproc.h>
#include <spawn.h>
#include <sys/proc.h>
#include <util.h>
#elif defined(__FreeBSD__)
#include <libutil.h>
#include <sys/param.h>
#include <sys/proc.h>
#include <sys/sysctl.h>
#include <sys/user.h>
#endif

/*
 * The line discipline every child starts with, set explicitly rather than taken from the kernel default. The
 * Linux and macOS defaults lack IUTF8, so erasing a multibyte character in canonical mode removed one byte and
 * left invalid UTF-8 on the line. Flags and control characters that a platform does not define are left out,
 * so ^T (VSTATUS) and ^Y (VDSUSP) work only where the kernel has them. A program that wants raw mode changes
 * the termios itself.
 */
static void minipty_default_termios(struct termios *term)
{
    memset(term, 0, sizeof(*term));
    /*
     * Zero is a valid control character on BSD, where _POSIX_VDISABLE is 0xff, so a slot left at zero would
     * make NUL act as that character. Every slot starts disabled and only the ones named below are enabled.
     */
    memset(term->c_cc, _POSIX_VDISABLE, sizeof(term->c_cc));

    term->c_iflag = ICRNL | IXON | IXANY | IMAXBEL | BRKINT;
#if defined(IUTF8)
    term->c_iflag |= IUTF8;
#endif
    term->c_oflag = OPOST | ONLCR;
    term->c_cflag = CREAD | CS8 | HUPCL;
    term->c_lflag = ICANON | ISIG | IEXTEN | ECHO | ECHOE | ECHOK;
#if defined(ECHOKE)
    term->c_lflag |= ECHOKE;
#endif
#if defined(ECHOCTL)
    term->c_lflag |= ECHOCTL;
#endif

    term->c_cc[VEOF] = 4;       /* ^D */
    term->c_cc[VERASE] = 0x7f;  /* DEL */
#if defined(VERASE2)
    term->c_cc[VERASE2] = 8;    /* ^H, the FreeBSD default second erase character */
#endif
    term->c_cc[VWERASE] = 23;   /* ^W */
    term->c_cc[VKILL] = 21;     /* ^U */
    term->c_cc[VREPRINT] = 18;  /* ^R */
    term->c_cc[VINTR] = 3;      /* ^C */
    term->c_cc[VQUIT] = 0x1c;   /* ^\ */
    term->c_cc[VSUSP] = 26;     /* ^Z */
    term->c_cc[VSTART] = 17;    /* ^Q */
    term->c_cc[VSTOP] = 19;     /* ^S */
    term->c_cc[VLNEXT] = 22;    /* ^V */
    term->c_cc[VDISCARD] = 15;  /* ^O */
    term->c_cc[VMIN] = 1;
    term->c_cc[VTIME] = 0;
#if defined(VDSUSP)
    term->c_cc[VDSUSP] = 25;    /* ^Y */
#endif
#if defined(VSTATUS)
    term->c_cc[VSTATUS] = 20;   /* ^T */
#endif

    cfsetispeed(term, B38400);
    cfsetospeed(term, B38400);
}

#if !defined(__linux__)

/*
 * The start report pipe of macOS and FreeBSD, whose children run in memory of their own. A Linux child shares the
 * host's memory until its exec and reports in it instead (struct minipty_clone_start).
 */

/*
 * How long a start waits for the child's report. A successful exec closes the pipe at once, so the bound matters only
 * when another process holds the write end: a child forked concurrently on another thread inherits it until its own
 * exec, and on macOS, which has no pipe2, a fork in the instant between pipe() and FD_CLOEXEC keeps it for good. The
 * start then gives up waiting and succeeds as it did before reports existed, so a failure shows as the exit status.
 */
#define MINIPTY_START_REPORT_TIMEOUT_MS 10000

/*
 * Opens the start report pipe, both ends close-on-exec, with the write end at a descriptor above stdio and above the
 * descriptor the macOS helper receives it on. A forkpty child replaces stdio with the terminal, and posix_spawn's
 * adddup2 onto the same descriptor would leave close-on-exec set, so neither may hold the write end.
 */
static int minipty_open_report_pipe(int fds[2])
{
    int write_fd;

#if defined(__APPLE__)
    if (pipe(fds) != 0)
        return errno > 0 ? errno : EMFILE;
    if (fcntl(fds[0], F_SETFD, FD_CLOEXEC) != 0 || fcntl(fds[1], F_SETFD, FD_CLOEXEC) != 0) {
        int err = errno > 0 ? errno : EINVAL;
        close(fds[0]);
        close(fds[1]);
        return err;
    }
#else
    if (pipe2(fds, O_CLOEXEC) != 0)
        return errno > 0 ? errno : EMFILE;
#endif

    if (fds[1] > MINIPTY_START_REPORT_FD)
        return 0;

    write_fd = fcntl(fds[1], F_DUPFD_CLOEXEC, MINIPTY_START_REPORT_FD + 1);
    if (write_fd < 0) {
        int err = errno > 0 ? errno : EMFILE;
        close(fds[0]);
        close(fds[1]);
        return err;
    }

    close(fds[1]);
    fds[1] = write_fd;
    return 0;
}

static long long minipty_monotonic_ms(void)
{
    struct timespec now;

    clock_gettime(CLOCK_MONOTONIC, &now);
    return (long long)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

/*
 * Waits on the read end of the start report pipe, after the parent closed its write end. Returns 1 with the stage and
 * errno when the child reported a failure. Returns 0 when the pipe closed without a report (the exec succeeded, or the
 * child ended in another way that its exit status reports) or when the wait gave up.
 */
static int minipty_await_start_report(int fd, int *stage_out, int *err_out)
{
    int report[2];
    size_t received = 0;
    long long deadline = minipty_monotonic_ms() + MINIPTY_START_REPORT_TIMEOUT_MS;

    for (;;) {
        struct pollfd pfd = { fd, POLLIN, 0 };
        long long remaining = deadline - minipty_monotonic_ms();
        int ready;
        ssize_t n;

        if (remaining <= 0)
            return 0;

        ready = poll(&pfd, 1, (int)remaining);
        if (ready < 0) {
            if (errno == EINTR)
                continue;
            return 0;
        }
        if (ready == 0)
            return 0;

        n = read(fd, (char *)report + received, sizeof(report) - received);
        if (n < 0) {
            if (errno == EINTR || errno == EAGAIN)
                continue;
            return 0;
        }
        if (n == 0)
            return 0;

        received += (size_t)n;
        if (received < sizeof(report))
            continue;

        if (report[0] != MINIPTY_START_STAGE_CHDIR && report[0] != MINIPTY_START_STAGE_EXEC)
            return 0;

        *stage_out = report[0];
        *err_out = report[1] > 0 ? report[1] : EINVAL;
        return 1;
    }
}

#endif /* !__linux__ */

#if !defined(__APPLE__)

#if !defined(__linux__)

#ifndef NSIG
#define NSIG 32
#endif

/* FreeBSD's forkpty child. The Linux child resets with the system call instead (minipty_reset_child_signals_to_default). */
static void minipty_reset_child_signals(void)
{
    struct sigaction sa;

    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = SIG_DFL;
    sa.sa_flags = 0;
    sigemptyset(&sa.sa_mask);

    for (int sig = 1; sig < NSIG; sig++)
        sigaction(sig, &sa, NULL);
}

#endif /* !__linux__ */

#if defined(__linux__)

#define MINIPTY_INHERITED_FD_SCAN_DEFAULT 4096
#define MINIPTY_INHERITED_FD_SCAN_CAP 65536

static int minipty_set_close_on_exec(int fd)
{
    int flags = fcntl(fd, F_GETFD, 0);

    if (flags == -1)
        return flags;
    if (flags & FD_CLOEXEC)
        return 0;

    return fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
}

static int minipty_compute_inherited_fd_scan_limit(void)
{
    long limit = sysconf(_SC_OPEN_MAX);

    if (limit < 0)
        return MINIPTY_INHERITED_FD_SCAN_DEFAULT;
    if (limit > MINIPTY_INHERITED_FD_SCAN_CAP)
        return MINIPTY_INHERITED_FD_SCAN_CAP;

    return (int)limit;
}

static void minipty_scrub_inherited_fds(int scan_limit)
{
#if defined(SYS_close_range) && defined(CLOSE_RANGE_CLOEXEC)
    if (syscall(SYS_close_range, 3, ~0U, CLOSE_RANGE_CLOEXEC) == 0)
        return;
#endif

    /*
     * Kernels before 5.11: set FD_CLOEXEC on fds >= 3 (does not close(2)). scan_limit is computed in the parent, so
     * the child makes no libc query.
     */
    for (int fd = 3; fd < scan_limit; fd++)
        minipty_set_close_on_exec(fd);
}

/* The bytes of the kernel's signal set: 64 signals on every architecture .NET runs on. */
#define MINIPTY_KERNEL_SIGSET_BYTES 8

/*
 * Sets every signal to its default action with the system call itself. The C library refuses to change its internal
 * signals (glibc's 32 and 33, musl's 32 to 34), and an ignored signal stays ignored across exec: a musl host had signal
 * 34 ignored, and its children started with it ignored. An all-zero record is SIG_DFL with no flags and an empty mask
 * in every architecture's layout; the kernel refuses SIGKILL and SIGSTOP, which are never anything else.
 */
static void minipty_reset_child_signals_to_default(void)
{
    unsigned long action[8] = { 0 };

    for (int sig = 1; sig <= MINIPTY_KERNEL_SIGSET_BYTES * 8; sig++)
        syscall(SYS_rt_sigaction, sig, action, NULL, MINIPTY_KERNEL_SIGSET_BYTES);
}

/*
 * The child's stack, not counting its guard page. The child's calls need a few kilobytes: the PATH lookup holds up to
 * two PATH_MAX buffers, and the sh fallback copies the argument vector, one pointer per argument, which the start adds
 * to this.
 */
#define MINIPTY_CLONE_STACK_BYTES (64 * 1024)

/*
 * What a Linux start hands its child, and what the child hands back. The child runs in the host's memory until its
 * exec, and the calling thread resumes only once the child has exec'd or exited (CLONE_VFORK), so a child that cannot
 * start writes its failure here, and the parent reads it when clone returns. The record lives on the calling thread's
 * stack, so concurrent starts never share one.
 */
struct minipty_clone_start {
    const char *cwd;
    const char *file;
    char *const *argv;
    char *const *envp;
    /* The calling thread's signal mask, which the child execs with. */
    const sigset_t *exec_mask;
    int slave;
    int fd_scan_limit;
    /* Written by a child that exits without exec: a stage of MINIPTY_START_STAGE_NONE is a failure to set up. */
    volatile int failed;
    volatile int stage;
    volatile int err;
};

/*
 * Opens both ends of a new terminal close-on-exec and sets MiniPty's termios and the initial window size on the slave.
 * glibc's openpty opened them without O_CLOEXEC, so a process the host started on another thread at that moment kept
 * the terminal open for as long as it ran. Opened this way, no process but the child gets them.
 */
static int minipty_open_terminal(int *master_out, int *slave_out, const struct winsize *winp)
{
    char name[64];
    struct termios term;
    int master;
    int slave;
    int err;

    *master_out = -1;
    *slave_out = -1;

    /* O_NOCTTY on both ends: a host that leads a session without a controlling terminal must not acquire this one. */
    master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (master < 0)
        return errno > 0 ? errno : EINVAL;

    if (grantpt(master) != 0 || unlockpt(master) != 0) {
        err = errno > 0 ? errno : EINVAL;
        close(master);
        return err;
    }

    /* Opens the slave from the master itself, which a mount namespace with another /dev/pts cannot misdirect. */
    slave = ioctl(master, TIOCGPTPEER, O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (slave < 0) {
        /* ptsname_r returns the error number; musl's does not set errno. */
        err = ptsname_r(master, name, sizeof(name));
        if (err != 0) {
            close(master);
            return err;
        }

        slave = open(name, O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (slave < 0) {
            err = errno > 0 ? errno : EINVAL;
            close(master);
            return err;
        }
    }

    minipty_default_termios(&term);
    if (tcsetattr(slave, TCSANOW, &term) != 0 || (winp != NULL && ioctl(slave, TIOCSWINSZ, winp) != 0)) {
        err = errno > 0 ? errno : EINVAL;
        close(slave);
        close(master);
        return err;
    }

    *master_out = master;
    *slave_out = slave;
    return 0;
}

/*
 * The child of a Linux start. It runs on a stack of its own, but in the host's memory, with every signal blocked and
 * the calling thread suspended until the exec, while the host's other threads keep running. So it only makes system
 * calls: no allocation, no lock, no stdio, and no write to memory but its stack and the start record. Everything it
 * reads was prepared before the clone. errno lands in the suspended thread's storage, which is harmless; the parent
 * reads the record, never errno. The library is linked with -z now, so no call here enters the dynamic linker.
 */
static int minipty_clone_child(void *arg)
{
    struct minipty_clone_start *start = arg;
    int slave = start->slave;
    int stage = MINIPTY_START_STAGE_NONE;
    int err;

    /* Without CLONE_SIGHAND the child has a handler table of its own, so this leaves the host's untouched. */
    minipty_reset_child_signals_to_default();

    /* A session of its own, whose controlling terminal is the slave and whose foreground process group is the child. */
    if (setsid() < 0 || ioctl(slave, TIOCSCTTY, 0) != 0)
        goto failed;

    /*
     * dup2 onto the slave's own descriptor would leave close-on-exec set, so a slave inside the stdio range (the host
     * had closed one of its own) is first copied above it. The copies dup2 makes onto stdio are inherited; the slave
     * itself and the master close at the exec.
     */
    if (slave <= STDERR_FILENO) {
        slave = fcntl(slave, F_DUPFD_CLOEXEC, STDERR_FILENO + 1);
        if (slave < 0)
            goto failed;
    }
    for (int fd = STDIN_FILENO; fd <= STDERR_FILENO; fd++) {
        if (dup2(slave, fd) < 0)
            goto failed;
    }

    minipty_scrub_inherited_fds(start->fd_scan_limit);

    if (start->cwd != NULL && start->cwd[0] != '\0' && chdir(start->cwd) != 0) {
        stage = MINIPTY_START_STAGE_CHDIR;
        goto failed;
    }

    /* Last, once every handler is the default: the program starts with the calling thread's mask, as after a fork. */
    syscall(SYS_rt_sigprocmask, SIG_SETMASK, start->exec_mask, NULL, MINIPTY_KERNEL_SIGSET_BYTES);
    minipty_execvpe(start->file, start->argv, start->envp);
    stage = MINIPTY_START_STAGE_EXEC;

failed:
    err = errno;
    start->err = err > 0 ? err : EINVAL;
    start->stage = stage;
    start->failed = 1;
    _exit(stage == MINIPTY_START_STAGE_CHDIR ? 126 : 127);
}

/*
 * Starts a Linux child with clone(CLONE_VM | CLONE_VFORK), as glibc's posix_spawn does, instead of forkpty. A fork
 * copies the host's page tables and marks every resident page copy-on-write before the child can exec, which cost
 * about 6 ms per GiB of the host's resident memory; here the child shares the host's memory until its exec, and a
 * start costs the same in any host. posix_spawn itself cannot run MiniPty's PATH lookup with its sh fallback, nor tell
 * a chdir failure from an exec failure.
 */
static int spawn_pty_child_linux(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    const char *file,
    char *const *argv,
    char *const *envp,
    pid_t *pid_out,
    int *pidfd_out,
    int *start_stage_out)
{
    struct minipty_clone_start start;
    char **child_envp = envp == NULL ? minipty_envp_for_child(NULL) : (char **)envp;
    size_t page = (size_t)sysconf(_SC_PAGESIZE);
    size_t argc = 0;
    size_t stack_bytes = 0;
    char *stack = MAP_FAILED;
    unsigned char all[MINIPTY_KERNEL_SIGSET_BYTES];
    sigset_t old;
    int slave = -1;
    /* Where CLONE_PIDFD writes the child's pidfd. A kernel before 5.2 ignores the flag and leaves it at -1. */
    int pidfd = -1;
    int clone_err;
    int err;
    pid_t pid;

    *master = -1;
    *pidfd_out = -1;
    if (child_envp == NULL)
        return ENOMEM;

    err = minipty_open_terminal(master, &slave, winp);
    if (err != 0)
        goto done;

    while (argv[argc] != NULL)
        argc++;
    stack_bytes = (MINIPTY_CLONE_STACK_BYTES + (argc + 2) * sizeof(char *) + page - 1) / page * page;

    /* One page more, below the stack and without access, so an overflow faults instead of writing the host's memory. */
    stack = mmap(NULL, page + stack_bytes, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_STACK, -1, 0);
    if (stack == MAP_FAILED || mprotect(stack + page, stack_bytes, PROT_READ | PROT_WRITE) != 0) {
        err = errno > 0 ? errno : ENOMEM;
        goto done;
    }

    memset(&start, 0, sizeof(start));
    start.cwd = cwd;
    start.file = file;
    start.argv = argv;
    start.envp = child_envp;
    start.exec_mask = &old;
    start.slave = slave;
    start.fd_scan_limit = minipty_compute_inherited_fd_scan_limit();

    /*
     * No handler may run in a child that shares the host's memory. sigfillset and glibc's mask calls leave out the C
     * library's internal signals, so every bit is set by hand and passed to the system call.
     */
    memset(all, 0xff, sizeof(all));
    sigemptyset(&old);
    syscall(SYS_rt_sigprocmask, SIG_BLOCK, all, &old, MINIPTY_KERNEL_SIGSET_BYTES);

    /*
     * SIGCHLD makes the child an ordinary one, which waitpid reaps and whose exit the host's SIGCHLD handler sees. The
     * thread pointer and child tid arguments are passed as NULL rather than left out: musl's wrapper reads all three
     * variadic arguments, and the flags use none but the pidfd's.
     */
    pid = clone(minipty_clone_child, stack + page + stack_bytes, CLONE_VM | CLONE_VFORK | CLONE_PIDFD | SIGCHLD, &start,
        &pidfd, NULL, NULL);
    if (pid < 0 && errno == EINVAL) {
        pidfd = -1;
        pid = clone(minipty_clone_child, stack + page + stack_bytes, CLONE_VM | CLONE_VFORK | SIGCHLD, &start, NULL, NULL,
            NULL);
    }
    clone_err = errno;

    syscall(SYS_rt_sigprocmask, SIG_SETMASK, &old, NULL, MINIPTY_KERNEL_SIGSET_BYTES);

    if (pid < 0) {
        err = clone_err > 0 ? clone_err : EINVAL;
        goto done;
    }

    if (start.failed) {
        int exit_code;
        int term_signal;

        /* The child exited right after it wrote the record, so this reaps it at once and leaves no zombie. */
        minipty_reap_child(pidfd, (int)pid, 1, &exit_code, &term_signal);
        if (pidfd >= 0)
            close(pidfd);
        *start_stage_out = start.stage;
        err = start.err;
        goto done;
    }

    /*
     * clone gave no pidfd: the kernel predates CLONE_PIDFD, or clone ran again without it. A pidfd opened by pid can
     * name another process that got a reused pid, so minipty_pidfd_open checks that it names a child of this one.
     */
    if (pidfd < 0)
        pidfd = minipty_pidfd_open((int)pid);

    *pid_out = pid;
    *pidfd_out = pidfd;
    err = 0;

done:
    /* The child has exec'd or exited by now, so neither its stack nor the parent's copy of the slave is in use. */
    if (stack != MAP_FAILED)
        munmap(stack, page + stack_bytes);
    if (slave >= 0)
        close(slave);
    if (err != 0 && *master >= 0) {
        close(*master);
        *master = -1;
    }
    if (envp == NULL)
        free(child_envp);
    return err;
}

#endif /* __linux__ */

#if !defined(__linux__)

/* FreeBSD (experimental, untested) keeps forkpty. */
static int spawn_pty_child_forkpty(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    const char *file,
    char *const *argv,
    char *const *envp,
    pid_t *pid_out,
    int *pidfd_out,
    int *start_stage_out)
{
    pid_t pid;
    sigset_t newmask;
    sigset_t oldmask;
    struct termios term;
    char **child_envp = envp == NULL ? minipty_envp_for_child(NULL) : (char **)envp;
    int report[2];
    int report_err;
    int stage = MINIPTY_START_STAGE_NONE;
    int child_err = 0;

    /* FreeBSD has no pidfd. */
    *pidfd_out = -1;
    if (child_envp == NULL) {
        errno = ENOMEM;
        return ENOMEM;
    }

    report_err = minipty_open_report_pipe(report);
    if (report_err != 0) {
        if (envp == NULL)
            free(child_envp);
        return report_err;
    }

    minipty_default_termios(&term);

    sigfillset(&newmask);
    pthread_sigmask(SIG_BLOCK, &newmask, &oldmask);

    pid = forkpty(master, NULL, &term, (struct winsize *)winp);

    if (pid == 0)
        minipty_reset_child_signals();

    pthread_sigmask(SIG_SETMASK, &oldmask, NULL);

    if (pid < 0) {
        int err = errno > 0 ? errno : EINVAL;
        close(report[0]);
        close(report[1]);
        if (envp == NULL)
            free(child_envp);
        return err;
    }

    if (pid == 0) {
        /* errno is saved first: the report's write could change it. Inherited descriptors are not scrubbed here yet. */
        int err;

        if (cwd != NULL && cwd[0] != '\0' && chdir(cwd) != 0) {
            err = errno;
            minipty_report_start_failure(report[1], MINIPTY_START_STAGE_CHDIR, err);
            _exit(126);
        }
        minipty_execvpe(file, argv, child_envp);
        err = errno;
        minipty_report_start_failure(report[1], MINIPTY_START_STAGE_EXEC, err);
        _exit(127);
    }

    /* Closed before anything else, so the pipe's end of file depends on the child's exec alone. */
    close(report[1]);

    if (envp == NULL)
        free(child_envp);

    /*
     * forkpty may open the master without O_CLOEXEC, so every child the host starts later would inherit it and keep the
     * terminal from hanging up. A fork on another thread before this call can still inherit it, and the slave inside
     * forkpty; the Linux and macOS paths open both ends close-on-exec instead.
     */
    if (fcntl(*master, F_SETFD, FD_CLOEXEC) != 0) {
        int err = errno > 0 ? errno : EINVAL;
        int exit_code;
        int term_signal;

        close(report[0]);
        minipty_signal_child(-1, (int)pid, SIGKILL);
        minipty_reap_child(-1, (int)pid, 1, &exit_code, &term_signal);
        close(*master);
        *master = -1;
        return err;
    }

    /*
     * The child never waits on this pipe, only the parent does. Holding a forked child until the parent signalled it
     * deadlocked concurrent spawns; here a concurrent spawn's child that inherited the write end closes it at its own
     * exec, which delays this read but cannot block it.
     */
    if (minipty_await_start_report(report[0], &stage, &child_err)) {
        int exit_code;
        int term_signal;

        close(report[0]);
        /* The child reported just before _exit, so this reaps it promptly and leaves no zombie. */
        minipty_reap_child(-1, (int)pid, 1, &exit_code, &term_signal);
        close(*master);
        *master = -1;
        *start_stage_out = stage;
        return child_err;
    }

    close(report[0]);
    *pid_out = pid;
    return 0;
}

#endif /* !__linux__ */

#endif /* !__APPLE__ */

#if defined(__APPLE__)
static int minipty_spawn_err_is_transient(int err)
{
    return err == EAGAIN || err == ENOMEM || err == ENXIO;
}

/*
 * Under concurrent PTY open/close, macOS fails posix_openpt with errno set to the negated ENXIO (-6) even far
 * below kern.tty.ptmx_max. The failure is momentary, so it is retried here on a short interval first: re-running
 * the whole spawn after its longer backoff still exhausted every attempt under heavy parallel spawning.
 */
#define MINIPTY_OPENPT_RETRY_MAX 8
#define MINIPTY_OPENPT_RETRY_US 1000

static int minipty_openpt(void)
{
    int fd;

    for (int attempt = 0;; attempt++) {
        /* Cleared so a failure that leaves errno untouched is not read as an earlier, unrelated error. */
        errno = 0;
        fd = posix_openpt(O_RDWR | O_CLOEXEC);
        if (fd >= 0 || errno > 0 || attempt + 1 >= MINIPTY_OPENPT_RETRY_MAX)
            return fd;
        usleep(MINIPTY_OPENPT_RETRY_US);
    }
}

/* Error for a failed minipty_openpt: a non-positive errno is reported as ENXIO, which the spawn retries. */
static int minipty_openpt_err(void)
{
    return errno > 0 ? errno : ENXIO;
}

static int minipty_resolve_helper_path(char *out, size_t out_len)
{
    Dl_info info;

    if (dladdr((void *)&minipty_fork_pty_exec, &info) == 0 || info.dli_fname == NULL)
        return -1;

    {
        char dir_buf[PATH_MAX];
        char *dir;
        int written;

        if (strlen(info.dli_fname) >= sizeof(dir_buf))
            return -1;

        memcpy(dir_buf, info.dli_fname, strlen(info.dli_fname) + 1);
        dir = dirname(dir_buf);
        written = snprintf(out, out_len, "%s/%s", dir, MINIPTY_SPAWN_HELPER_NAME);
        if (written < 0 || (size_t)written >= out_len)
            return -1;
    }

    if (access(out, X_OK) != 0)
        return -1;

    return 0;
}

static void minipty_free_spawn_env(char **spawn_envp, char **owned_inherited)
{
    char *cwd_entry = NULL;

    if (spawn_envp != NULL) {
        for (size_t i = 0; spawn_envp[i] != NULL; i++) {
            const char *entry = spawn_envp[i];
            if (strncmp(entry, MINIPTY_CWD_KEY "=", strlen(MINIPTY_CWD_KEY) + 1) == 0) {
                cwd_entry = spawn_envp[i];
                break;
            }
        }
    }

    free(cwd_entry);
    free(spawn_envp);
    free(owned_inherited);
}

static int minipty_spawn_darwin_once(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    char *const *argv,
    char *const *envp,
    pid_t *pid_out,
    int *slave_out,
    int *start_stage_out)
{
    char helper_path[PATH_MAX];
    char slave_name[128];
    struct termios term;
    char **spawn_envp = NULL;
    char **owned_inherited = NULL;
    char **helper_argv = NULL;
    size_t argc = 0;
    int low_fds[3] = {-1, -1, -1};
    size_t low_fd_opened = 0;
    int report[2] = {-1, -1};
    int stage = MINIPTY_START_STAGE_NONE;
    int child_err = 0;
    int slave = -1;
    int err = 0;
    int spawn_err = 0;
    int actions_initialized = 0;
    int attrs_initialized = 0;
    int success = 0;
    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attrs;
    sigset_t signal_set;

    *master = -1;

    for (size_t i = 0; i < 3; i++) {
        int fd = minipty_openpt();
        if (fd < 0) {
            err = minipty_openpt_err();
            goto done;
        }

        /* Reserve only vacant stdio slots (0/1/2). If fd >= 3, nothing to reserve. */
        if (fd >= (STDERR_FILENO + 1)) {
            close(fd);
            break;
        }

        low_fds[i] = fd;
        low_fd_opened = i + 1;
        if (fd >= STDERR_FILENO)
            break;
    }

    if (minipty_resolve_helper_path(helper_path, sizeof(helper_path)) != 0) {
        err = ENOENT;
        goto done;
    }

    err = minipty_open_report_pipe(report);
    if (err != 0)
        goto done;

    *master = minipty_openpt();
    if (*master < 0) {
        err = minipty_openpt_err();
        goto done;
    }

    if (grantpt(*master) != 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    if (unlockpt(*master) != 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    if (ioctl(*master, TIOCPTYGNAME, slave_name) != 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    slave = open(slave_name, O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (slave < 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    if (winp != NULL && ioctl(slave, TIOCSWINSZ, winp) != 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    /* forkpty takes the termios as an argument. Here the slave is already open in the parent, so it is set directly. */
    minipty_default_termios(&term);
    if (tcsetattr(slave, TCSANOW, &term) != 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    if (minipty_envp_append_cwd(envp, cwd, &spawn_envp, &owned_inherited) != 0) {
        err = errno > 0 ? errno : ENOMEM;
        goto done;
    }

    if (spawn_envp == NULL && envp == NULL) {
        spawn_envp = minipty_envp_for_child(NULL);
        if (spawn_envp == NULL) {
            err = ENOMEM;
            goto done;
        }
    }

    while (argv[argc] != NULL)
        argc++;

    helper_argv = malloc((argc + 2) * sizeof(char *));
    if (helper_argv == NULL) {
        err = ENOMEM;
        goto done;
    }

    helper_argv[0] = helper_path;
    for (size_t i = 0; i < argc; i++)
        helper_argv[i + 1] = argv[i];
    helper_argv[argc + 1] = NULL;

    spawn_err = posix_spawn_file_actions_init(&actions);
    if (spawn_err != 0) {
        err = spawn_err;
        goto done;
    }
    actions_initialized = 1;

    spawn_err = posix_spawnattr_init(&attrs);
    if (spawn_err != 0) {
        err = spawn_err;
        goto done;
    }
    attrs_initialized = 1;

    spawn_err = posix_spawn_file_actions_adddup2(&actions, slave, STDIN_FILENO);
    if (spawn_err != 0)
        goto spawn_setup_err;
    spawn_err = posix_spawn_file_actions_adddup2(&actions, slave, STDOUT_FILENO);
    if (spawn_err != 0)
        goto spawn_setup_err;
    spawn_err = posix_spawn_file_actions_adddup2(&actions, slave, STDERR_FILENO);
    if (spawn_err != 0)
        goto spawn_setup_err;
    spawn_err = posix_spawn_file_actions_addclose(&actions, slave);
    if (spawn_err != 0)
        goto spawn_setup_err;
    spawn_err = posix_spawn_file_actions_addclose(&actions, *master);
    if (spawn_err != 0)
        goto spawn_setup_err;
    /*
     * Last, so a slave or master that sat on the report descriptor is already closed. POSIX_SPAWN_CLOEXEC_DEFAULT
     * closes every descriptor the actions do not name, so the helper receives only stdio and this one, without
     * close-on-exec; the helper sets it before its exec.
     */
    spawn_err = posix_spawn_file_actions_adddup2(&actions, report[1], MINIPTY_START_REPORT_FD);
    if (spawn_err != 0)
        goto spawn_setup_err;

    spawn_err = posix_spawnattr_setflags(
        &attrs,
        POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSID);
    if (spawn_err != 0)
        goto spawn_setup_err;

    sigfillset(&signal_set);
    sigdelset(&signal_set, SIGKILL);
    sigdelset(&signal_set, SIGSTOP);
    spawn_err = posix_spawnattr_setsigdefault(&attrs, &signal_set);
    if (spawn_err != 0)
        goto spawn_setup_err;
    sigemptyset(&signal_set);
    spawn_err = posix_spawnattr_setsigmask(&attrs, &signal_set);
    if (spawn_err != 0)
        goto spawn_setup_err;

    do {
        spawn_err = posix_spawn(
            pid_out,
            helper_argv[0],
            &actions,
            &attrs,
            helper_argv,
            spawn_envp != NULL ? spawn_envp : (char **)envp);
    } while (spawn_err == EINTR);

    if (spawn_err != 0) {
        err = spawn_err;
        goto done;
    }

    close(report[1]);
    report[1] = -1;
    if (minipty_await_start_report(report[0], &stage, &child_err)) {
        int status;

        /* The helper reported just before _exit, so this reaps it promptly and leaves no zombie. */
        while (waitpid(*pid_out, &status, 0) < 0 && errno == EINTR) {
        }
        *start_stage_out = stage;
        err = child_err;
        goto done;
    }

    success = 1;
    goto done;

spawn_setup_err:
    err = spawn_err > 0 ? spawn_err : EINVAL;
    goto done;

done:
    if (actions_initialized)
        posix_spawn_file_actions_destroy(&actions);
    if (attrs_initialized)
        posix_spawnattr_destroy(&attrs);
    /*
     * The caller keeps the slave open until it closes the master, so the child's exit is never the terminal's last
     * close. On that last close XNU waits at most 0.6 s (ptsclose sets t_timeout to 60 ticks) for the master to read
     * what the terminal still holds, then discards it, and a session whose consumer had stopped reading lost up to
     * 1 KiB of the child's last output. With another descriptor open, a session leader's exit waits for the master
     * without a bound and then revokes the terminal, which still ends output at the exit.
     */
    if (success) {
        *slave_out = slave;
        slave = -1;
    }
    if (slave >= 0)
        close(slave);
    if (report[0] >= 0)
        close(report[0]);
    if (report[1] >= 0)
        close(report[1]);
    for (size_t i = 0; i < low_fd_opened; i++) {
        if (low_fds[i] >= 0)
            close(low_fds[i]);
    }

    free(helper_argv);
    minipty_free_spawn_env(spawn_envp, owned_inherited);

    if (!success && *master >= 0) {
        close(*master);
        *master = -1;
    }

    if (err != 0)
        return err;

    return 0;
}

static int spawn_pty_child_darwin(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    const char *file,
    char *const *argv,
    char *const *envp,
    pid_t *pid_out,
    int *slave_out,
    int *start_stage_out)
{
    int err = 0;

    *master = -1;

    (void)file;

    for (int attempt = 0; attempt < MINIPTY_SPAWN_RETRY_MAX; attempt++) {
        err = minipty_spawn_darwin_once(master, winp, cwd, argv, envp, pid_out, slave_out, start_stage_out);
        if (err == 0)
            return 0;
        /* A child that could not chdir or exec is the caller's error, not a transient failure to spawn. */
        if (*start_stage_out != MINIPTY_START_STAGE_NONE || !minipty_spawn_err_is_transient(err))
            return err;
        if (*master >= 0) {
            close(*master);
            *master = -1;
        }
        usleep(MINIPTY_SPAWN_RETRY_BASE_US * (unsigned int)(attempt + 1));
    }

    return err > 0 ? err : EINVAL;
}
#endif

static int spawn_pty_child(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    const char *file,
    char *const *argv,
    char *const *envp,
    pid_t *pid_out,
    int *pidfd_out,
    int *slave_out,
    int *start_stage_out)
{
    *slave_out = -1;
    *start_stage_out = MINIPTY_START_STAGE_NONE;
#if defined(__APPLE__)
    *pidfd_out = -1;
    return spawn_pty_child_darwin(master, winp, cwd, file, argv, envp, pid_out, slave_out, start_stage_out);
#elif defined(__linux__)
    return spawn_pty_child_linux(master, winp, cwd, file, argv, envp, pid_out, pidfd_out, start_stage_out);
#else
    return spawn_pty_child_forkpty(master, winp, cwd, file, argv, envp, pid_out, pidfd_out, start_stage_out);
#endif
}

int minipty_fork_pty_exec(
    int *master,
    const struct winsize *winp,
    const char *working_directory,
    const char *file,
    char *const *argv,
    char *const *envp,
    int *pid_out,
    int *pidfd_out,
    int *slave_out,
    int *start_stage_out)
{
    pid_t pid = -1;
    int err;

    err = spawn_pty_child(master, winp, working_directory, file, argv, envp, &pid, pidfd_out, slave_out, start_stage_out);
    if (err != 0)
        return err;

    *pid_out = (int)pid;
    return 0;
}

int minipty_set_winsize(
    int master,
    unsigned short rows,
    unsigned short cols,
    unsigned short pixel_width,
    unsigned short pixel_height)
{
    struct winsize ws = {
        .ws_row = rows,
        .ws_col = cols,
        .ws_xpixel = pixel_width,
        .ws_ypixel = pixel_height,
    };

    if (ioctl(master, TIOCSWINSZ, &ws) != 0)
        return -1;

    return 0;
}

int minipty_get_active_process_name(int master, char *buffer, int buffer_length)
{
    pid_t foreground;

    if (buffer == NULL || buffer_length <= 1)
        return 0;

    foreground = tcgetpgrp(master);
    if (foreground <= 0 || (kill(-foreground, 0) != 0 && errno != EPERM))
        return 0;

#if defined(__linux__)
    {
        DIR *directory;
        struct dirent *entry;
        char path[64];
        pid_t process = foreground;
        char leader_stat_path[64];
        char leader_stat_buffer[1024];
        char *leader_comm_end;
        long leader_group = 0;
        char leader_state = '\0';
        int leader_stat_fd;
        ssize_t leader_stat_length;

        /* The process-group leader is the common path. A pipeline can outlive its leader, so
           scan /proc only when the group id no longer names a live process. */
        if (snprintf(leader_stat_path, sizeof(leader_stat_path), "/proc/%ld/stat", (long)foreground)
                >= (int)sizeof(leader_stat_path))
            return 0;
        leader_stat_fd = open(leader_stat_path, O_RDONLY | O_CLOEXEC);
        if (leader_stat_fd >= 0) {
            do {
                leader_stat_length = read(leader_stat_fd, leader_stat_buffer, sizeof(leader_stat_buffer) - 1);
            } while (leader_stat_length < 0 && errno == EINTR);
            close(leader_stat_fd);
            if (leader_stat_length > 0) {
                leader_stat_buffer[leader_stat_length] = '\0';
                leader_comm_end = strrchr(leader_stat_buffer, ')');
                if (leader_comm_end != NULL)
                    (void)sscanf(leader_comm_end + 1, " %c %*d %ld", &leader_state, &leader_group);
            }
        }
        if (leader_state == 'Z' || (pid_t)leader_group != foreground) {
            process = 0;
            directory = opendir("/proc");
            if (directory == NULL)
                return 0;
            while ((entry = readdir(directory)) != NULL) {
                char *end;
                long candidate = strtol(entry->d_name, &end, 10);
                char stat_buffer[1024];
                char *comm_end;
                char state;
                long group;
                int stat_fd;
                ssize_t stat_length;

                if (*entry->d_name == '\0' || *end != '\0' || candidate <= 0 || candidate > INT_MAX)
                    continue;
                if (snprintf(path, sizeof(path), "/proc/%ld/stat", candidate) >= (int)sizeof(path))
                    continue;
                stat_fd = open(path, O_RDONLY | O_CLOEXEC);
                if (stat_fd < 0)
                    continue;
                do {
                    stat_length = read(stat_fd, stat_buffer, sizeof(stat_buffer) - 1);
                } while (stat_length < 0 && errno == EINTR);
                close(stat_fd);
                if (stat_length <= 0)
                    continue;
                stat_buffer[stat_length] = '\0';
                comm_end = strrchr(stat_buffer, ')');
                if (comm_end == NULL || sscanf(comm_end + 1, " %c %*d %ld", &state, &group) != 2)
                    continue;
                if ((pid_t)group == foreground && state != 'Z') {
                    process = (pid_t)candidate;
                    break;
                }
            }
            closedir(directory);
            if (process <= 0)
                return 0;
        }

        if (snprintf(path, sizeof(path), "/proc/%ld/comm", (long)process) < (int)sizeof(path)) {
            int fd = open(path, O_RDONLY | O_CLOEXEC);
            if (fd >= 0) {
                ssize_t length;
                do {
                    length = read(fd, buffer, (size_t)buffer_length - 1);
                } while (length < 0 && errno == EINTR);
                close(fd);
                if (length > 0) {
                    while (length > 0 && (buffer[length - 1] == '\n' || buffer[length - 1] == '\r'))
                        length--;
                    buffer[length] = '\0';
                    return (int)length;
                }
            }
        }
        return 0;
    }
#elif defined(__APPLE__)
    {
        struct proc_bsdinfo process_info;
        int info_length = proc_pidinfo(
            foreground,
            PROC_PIDTBSDINFO,
            0,
            &process_info,
            sizeof(process_info));
        int length;
        if (info_length == (int)sizeof(process_info) && process_info.pbi_status != SZOMB) {
            length = proc_name(foreground, buffer, (uint32_t)buffer_length);
            if (length > 0)
                return length;
        }

        pid_t processes[64];
        int bytes = proc_listpids(PROC_PGRP_ONLY, (uint32_t)foreground, processes, sizeof(processes));
        int count = bytes > 0 ? bytes / (int)sizeof(processes[0]) : 0;
        for (int i = 0; i < count; i++) {
            if (processes[i] <= 0 || processes[i] == foreground)
                continue;
            info_length = proc_pidinfo(
                processes[i],
                PROC_PIDTBSDINFO,
                0,
                &process_info,
                sizeof(process_info));
            if (info_length != (int)sizeof(process_info) || process_info.pbi_status == SZOMB)
                continue;
            length = proc_name(processes[i], buffer, (uint32_t)buffer_length);
            if (length > 0)
                return length;
        }
        return 0;
    }
#elif defined(__FreeBSD__)
    {
        int mib[4] = { CTL_KERN, KERN_PROC, KERN_PROC_PID, foreground };
        struct kinfo_proc process;
        size_t process_size = sizeof(process);
        size_t length;

        if (sysctl(mib, 4, &process, &process_size, NULL, 0) != 0
                || process_size == 0
                || process.ki_stat == SZOMB) {
            struct kinfo_proc processes[64];
            size_t processes_size = sizeof(processes);
            size_t count;
            int found = 0;

            mib[2] = KERN_PROC_PGRP;
            if (sysctl(mib, 4, processes, &processes_size, NULL, 0) != 0)
                return 0;
            count = processes_size / sizeof(processes[0]);
            for (size_t i = 0; i < count; i++) {
                if (processes[i].ki_stat != SZOMB) {
                    process = processes[i];
                    found = 1;
                    break;
                }
            }
            if (!found)
                return 0;
        }
        length = strnlen(process.ki_comm, sizeof(process.ki_comm));
        if (length >= (size_t)buffer_length)
            length = (size_t)buffer_length - 1;
        memcpy(buffer, process.ki_comm, length);
        buffer[length] = '\0';
        return (int)length;
    }
#else
    return 0;
#endif
}

#define MINIPTY_CONSOLE_TERMIOS_BLOB 128

typedef struct minipty_console_termios_blob {
    unsigned char bytes[MINIPTY_CONSOLE_TERMIOS_BLOB];
    int length;
} minipty_console_termios_blob;

int minipty_console_isatty(int fd)
{
    return isatty(fd);
}

int minipty_console_termios_save(int fd, minipty_console_termios_blob *out)
{
    struct termios term;

    if (out == NULL || tcgetattr(fd, &term) != 0)
        return -1;

    if (sizeof(term) > (size_t)MINIPTY_CONSOLE_TERMIOS_BLOB)
        return -1;

    memcpy(out->bytes, &term, sizeof(term));
    out->length = (int)sizeof(term);
    return 0;
}

int minipty_console_termios_restore(int fd, const minipty_console_termios_blob *snap)
{
    struct termios term;

    if (snap == NULL || snap->length != (int)sizeof(struct termios))
        return -1;

    memcpy(&term, snap->bytes, sizeof(term));
    return tcsetattr(fd, TCSANOW, &term);
}

int minipty_console_termios_set_raw_input(int fd)
{
    struct termios term;

    if (tcgetattr(fd, &term) != 0)
        return -1;

    term.c_lflag &= (tcflag_t)~(ICANON | ECHO | ISIG | IEXTEN);
    term.c_iflag &= (tcflag_t)~(BRKINT | ICRNL | INPCK | ISTRIP | IXON);
    term.c_cflag |= (tcflag_t)CS8;
    term.c_cc[VMIN] = 0;
    term.c_cc[VTIME] = 1;
    return tcsetattr(fd, TCSANOW, &term);
}

int minipty_console_termios_set_raw_output(int fd)
{
    struct termios term;

    if (tcgetattr(fd, &term) != 0)
        return -1;

    term.c_oflag &= (tcflag_t)~OPOST;
    return tcsetattr(fd, TCSANOW, &term);
}

int minipty_console_get_winsize(int fd, unsigned short *rows, unsigned short *cols)
{
    struct winsize ws;

    if (rows == NULL || cols == NULL)
        return -1;

    if (ioctl(fd, TIOCGWINSZ, &ws) != 0)
        return -1;

    *rows = ws.ws_row;
    *cols = ws.ws_col;
    return 0;
}
