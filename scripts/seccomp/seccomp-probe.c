/*
 * Checks that a seccomp profile in this directory took effect, before the test suite runs under it
 * (scripts/run-tests-under-seccomp.sh). The profiles make a current kernel refuse what older kernels lack, so that the
 * suite takes MiniPty's fallbacks for them. A profile that stopped matching, after a C library or Docker change, would
 * let the suite pass on the newer paths instead, so this probe fails first.
 *
 * For each call MiniPty's Linux start falls back from, the probe makes a call that cannot harm it and tells the
 * filter's refusal apart from the kernel's own answer. It then compares the calls refused with those the named
 * profile refuses. Each call the profile refuses must be refused. Each other call must work, which also shows that
 * the kernel has every call, so the fallbacks the suite takes come from the profile and not from the kernel.
 *
 * Build and run it in the container whose compiler builds the shim, under the profile it checks:
 *   cc -O2 -Wall -Wextra -o seccomp-probe scripts/seccomp/seccomp-probe.c
 *   ./seccomp-probe old-kernel
 *
 * Profiles: old-kernel, no-clone-pidfd, no-waitid-pidfd, and none, which expects every call to work.
 * Exit status: the number of calls whose result differs from the profile's, or 100 when the probe cannot run.
 */

#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <sched.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <unistd.h>

#if !defined(__linux__)
#error "The profiles stand in for older Linux kernels: build this probe on Linux."
#endif

/* The raw clone below passes the pidfd's address third, where x86-64 and arm64 take the parent tid. */
#if !defined(__x86_64__) && !defined(__aarch64__)
#error "Only x86-64 and arm64 are known to take clone's arguments in this order."
#endif

/* Old headers lack these. The numbers are the same on x86-64 and arm64. */
#ifndef SYS_pidfd_send_signal
#define SYS_pidfd_send_signal 424
#endif
#ifndef SYS_pidfd_open
#define SYS_pidfd_open 434
#endif
#ifndef SYS_clone3
#define SYS_clone3 435
#endif
#ifndef SYS_close_range
#define SYS_close_range 436
#endif
#ifndef CLONE_PIDFD
#define CLONE_PIDFD 0x00001000
#endif
#ifndef TIOCGPTPEER
#define TIOCGPTPEER _IO('T', 0x41)
#endif
#ifndef CLOSE_RANGE_CLOEXEC
#define CLOSE_RANGE_CLOEXEC (1U << 2)
#endif
#define PROBE_P_PIDFD 3
/* A descriptor number no process here has open. */
#define PROBE_UNUSED_FD 1000000

enum outcome { WORKS, REFUSED, UNEXPECTED };

struct result {
    enum outcome outcome;
    char detail[96];
};

enum call {
    CALL_TIOCGPTPEER,
    CALL_CLONE_PIDFD,
    CALL_WAITID_PIDFD,
    CALL_PIDFD_SEND_SIGNAL,
    CALL_PIDFD_OPEN,
    CALL_CLONE3,
    CALL_CLOSE_RANGE,
    CALL_COUNT
};

static const char *const call_names[CALL_COUNT] = {
    "ioctl TIOCGPTPEER",
    "clone with CLONE_PIDFD",
    "waitid with P_PIDFD",
    "pidfd_send_signal",
    "pidfd_open",
    "clone3",
    "close_range with CLOSE_RANGE_CLOEXEC",
};

static void set_detail(struct result *r, enum outcome outcome, const char *what, int err)
{
    r->outcome = outcome;
    if (err != 0)
        snprintf(r->detail, sizeof(r->detail), "%s, errno %d (%s)", what, err, strerror(err));
    else
        snprintf(r->detail, sizeof(r->detail), "%s", what);
}

/* A failure the filter returns is `refused`; the call's own failure with the kernel's error number is `works`. */
static void classify(struct result *r, long value, int err, int refused_errno, int works_errno)
{
    if (value >= 0 && works_errno == 0)
        set_detail(r, WORKS, "succeeded", 0);
    else if (value < 0 && err == refused_errno)
        set_detail(r, REFUSED, "refused", err);
    else if (value < 0 && works_errno != 0 && err == works_errno)
        set_detail(r, WORKS, "reached the kernel", err);
    else
        set_detail(r, UNEXPECTED, value < 0 ? "failed" : "succeeded", value < 0 ? err : 0);
}

static void probe_tiocgptpeer(struct result *r)
{
    int master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
    int slave;
    int err;

    if (master < 0 || grantpt(master) != 0 || unlockpt(master) != 0) {
        perror("seccomp-probe: opening a pseudo-terminal");
        exit(100);
    }

    slave = ioctl(master, TIOCGPTPEER, O_RDWR | O_NOCTTY | O_CLOEXEC);
    err = errno;
    classify(r, slave, err, ENOTTY, 0);
    if (slave >= 0)
        close(slave);
    close(master);
}

static void probe_clone_pidfd(struct result *r)
{
    int pidfd = -1;
    long pid;
    int err;

    /* Without CLONE_VM this is a fork: the child shares nothing, and only exits. */
    pid = syscall(SYS_clone, CLONE_PIDFD | SIGCHLD, 0, &pidfd, 0, 0);
    if (pid == 0)
        _exit(0);

    err = errno;
    if (pid < 0) {
        classify(r, pid, err, EINVAL, 0);
        return;
    }

    waitpid((pid_t)pid, NULL, 0);
    if (pidfd >= 0) {
        set_detail(r, WORKS, "succeeded with a pidfd", 0);
        close(pidfd);
    } else {
        /* A kernel before 5.2 ignores the flag. A filter cannot do that, so the kernel itself is too old. */
        set_detail(r, UNEXPECTED, "succeeded without a pidfd: the kernel ignores the flag", 0);
    }
}

static void probe_waitid_pidfd(struct result *r)
{
    siginfo_t info;
    long value;

    /* The kernel looks the descriptor up and fails with EBADF; the filter, like a kernel before 5.4, with EINVAL. */
    memset(&info, 0, sizeof(info));
    value = syscall(SYS_waitid, PROBE_P_PIDFD, PROBE_UNUSED_FD, &info, WEXITED | WNOHANG, NULL);
    classify(r, value, errno, EINVAL, EBADF);
}

static void probe_pidfd_send_signal(struct result *r)
{
    long value = syscall(SYS_pidfd_send_signal, -1, 0, NULL, 0);

    classify(r, value, errno, ENOSYS, EBADF);
}

static void probe_pidfd_open(struct result *r)
{
    long fd = syscall(SYS_pidfd_open, getpid(), 0);
    int err = errno;

    classify(r, fd, err, ENOSYS, 0);
    if (fd >= 0)
        close((int)fd);
}

static void probe_clone3(struct result *r)
{
    /* A size below the smallest clone_args fails with EINVAL before anything is created. */
    long value = syscall(SYS_clone3, NULL, 0);

    classify(r, value, errno, ENOSYS, EINVAL);
}

static void probe_close_range(struct result *r)
{
    /* Marks no descriptor: none is open at the top of the range. Before 5.11 the flag itself fails with EINVAL. */
    long value = syscall(SYS_close_range, ~0U, ~0U, CLOSE_RANGE_CLOEXEC);

    classify(r, value, errno, ENOSYS, 0);
}

int main(int argc, char **argv)
{
    static void (*const probes[CALL_COUNT])(struct result *) = {
        probe_tiocgptpeer,
        probe_clone_pidfd,
        probe_waitid_pidfd,
        probe_pidfd_send_signal,
        probe_pidfd_open,
        probe_clone3,
        probe_close_range,
    };
    int refuses[CALL_COUNT] = { 0 };
    const char *profile = argc == 2 ? argv[1] : "";
    int mismatches = 0;
    int i;

    if (strcmp(profile, "old-kernel") == 0) {
        for (i = 0; i < CALL_COUNT; i++)
            refuses[i] = 1;
    } else if (strcmp(profile, "no-clone-pidfd") == 0) {
        refuses[CALL_CLONE_PIDFD] = 1;
    } else if (strcmp(profile, "no-waitid-pidfd") == 0) {
        refuses[CALL_WAITID_PIDFD] = 1;
    } else if (strcmp(profile, "none") != 0) {
        fprintf(stderr, "Usage: seccomp-probe old-kernel|no-clone-pidfd|no-waitid-pidfd|none\n");
        return 100;
    }

    printf("seccomp-probe: profile %s\n", profile);
    for (i = 0; i < CALL_COUNT; i++) {
        struct result r;
        int ok;

        memset(&r, 0, sizeof(r));
        probes[i](&r);
        ok = r.outcome == (refuses[i] ? REFUSED : WORKS);
        if (!ok)
            mismatches++;
        printf("  %-4s %-37s expected %-8s got: %s\n", ok ? "ok" : "FAIL", call_names[i], refuses[i] ? "refused" : "works",
            r.detail);
    }

    if (mismatches != 0)
        printf("seccomp-probe: %d of %d calls differ from profile %s\n", mismatches, CALL_COUNT, profile);
    return mismatches;
}
