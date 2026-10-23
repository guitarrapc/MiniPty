/*
 * Readiness probe for PTY masters: Phase 4 of .github/docs/plans/plan_thread_free_io.md.
 *
 * Answers, on the machine it runs on, whether one thread can wait for many PTY masters with kqueue, select, or
 * poll, before the Unix I/O loop is written. It checks level-triggered EVFILT_READ and EVFILT_WRITE, end of file
 * after the slave closes and after the session leader exits, the master's small buffer under a flood, and hundreds
 * of masters in one wait. It uses no MiniPty code. Every child is forked while the probe has one thread; the only
 * threads are the thread-per-session flood baseline, which stands for today's reader threads.
 *
 * The results and the choice they led to are recorded under "Phase 4 result" in that plan. Rerun it on a new macOS
 * release, or on FreeBSD, where the choice was made from source only.
 *
 * Build and run on macOS or FreeBSD (it takes about three minutes, most of it in the T5 floods):
 *   cc -O2 -Wall -Wextra -pthread -o pty-readiness-probe scripts/pty-readiness-probe.c
 *   ./pty-readiness-probe
 *
 * Options:
 *   --only T1,T5        run only the named tests (T0 to T6)
 *   --max-ptys N        how many PTYs T6 opens at most (default 1000)
 *   --flood-mib N       MiB per single-session flood (default 32; each of 8 concurrent floods gets a quarter)
 *   --rounds N          flood rounds per loop kind, alternating the kinds (default 3)
 *   --latency-rounds N  single-master wake-ups timed per mechanism in T6 (default 300)
 *
 * T6 opens PTYs until it reaches --max-ptys or the system refuses one. macOS allows kern.tty.ptmx_max PTYs in the
 * whole system (511 by default), so T6 stops 16 short of that, and a terminal window opened in that second may fail.
 *
 * Output: PASS and FAIL lines check what a level-triggered loop relies on. INFO lines are observations and
 * measurements. poll(2) results are INFO only: the macOS poll page says it does not support devices. The exit
 * status is the number of FAIL lines, at most 100.
 */

#if defined(__APPLE__)
/* Must precede every system header: lets select() take descriptors above FD_SETSIZE. */
#define _DARWIN_UNLIMITED_SELECT 1
#define _DARWIN_C_SOURCE 1
#endif

#if !defined(__APPLE__) && !defined(__FreeBSD__)
#error "This probe compares kqueue with select and poll: build it on macOS or FreeBSD."
#endif

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdarg.h>
#include <stdatomic.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/event.h>
#include <sys/ioctl.h>
#include <sys/resource.h>
#include <sys/select.h>
#include <sys/sysctl.h>
#include <sys/time.h>
#include <sys/types.h>
#include <sys/utsname.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#define READ_BUFFER 16384
#define FLOOD_MAX_SESSIONS 8
/* Children close every inherited descriptor below this; no test that forks holds more. */
#define CHILD_CLOSE_LIMIT 256
/* T6 moves masters to descriptors from here, so select() is exercised above FD_SETSIZE. */
#define HIGH_FD_BASE 4096

static int g_pass;
static int g_fail;
static const char *g_only;
static int g_max_ptys = 1000;
static int g_flood_mib = 32;
static int g_rounds = 3;
static int g_latency_rounds = 300;
static speed_t g_best_speed;

/* ---------------------------------------------------------------- reporting */

static void report(int ok, const char *fmt, ...) __attribute__((format(printf, 2, 3)));
static void report(int ok, const char *fmt, ...)
{
    va_list ap;

    printf("  %s ", ok ? "PASS" : "FAIL");
    va_start(ap, fmt);
    vprintf(fmt, ap);
    va_end(ap);
    putchar('\n');
    fflush(stdout);
    if (ok)
        g_pass++;
    else
        g_fail++;
}

static void info(const char *fmt, ...) __attribute__((format(printf, 1, 2)));
static void info(const char *fmt, ...)
{
    va_list ap;

    printf("  INFO ");
    va_start(ap, fmt);
    vprintf(fmt, ap);
    va_end(ap);
    putchar('\n');
    fflush(stdout);
}

/* Reports through PASS/FAIL, or as INFO for a mechanism that is only observed (poll). */
static void outcome(int checked, int ok, const char *fmt, ...) __attribute__((format(printf, 3, 4)));
static void outcome(int checked, int ok, const char *fmt, ...)
{
    char text[512];
    va_list ap;

    va_start(ap, fmt);
    vsnprintf(text, sizeof(text), fmt, ap);
    va_end(ap);
    if (checked)
        report(ok, "%s", text);
    else
        info("%s%s", ok ? "" : "(would fail) ", text);
}

static void section(const char *id, const char *title)
{
    printf("\n[%s] %s\n", id, title);
    fflush(stdout);
}

static int selected(const char *id)
{
    return g_only == NULL || strstr(g_only, id) != NULL;
}

static const char *errno_name(int e)
{
    switch (e) {
    case 0: return "0";
    case EAGAIN: return "EAGAIN";
    case EIO: return "EIO";
    case EBADF: return "EBADF";
    case EINVAL: return "EINVAL";
    case ENXIO: return "ENXIO";
    case EPIPE: return "EPIPE";
    case EINTR: return "EINTR";
    case EMFILE: return "EMFILE";
    case ENFILE: return "ENFILE";
    default: return strerror(e);
    }
}

/* "n" for a count, "0 (EOF)" or "-1 EIO" for the end of a read or write. */
static const char *io_result(ssize_t r, int e, char *buf, size_t len)
{
    if (r > 0)
        snprintf(buf, len, "%zd", r);
    else if (r == 0)
        snprintf(buf, len, "0 (end of file)");
    else
        snprintf(buf, len, "-1 %s", errno_name(e));
    return buf;
}

/* ---------------------------------------------------------------- clocks */

static uint64_t now_ns(void)
{
    struct timespec ts;

    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000000ull + (uint64_t)ts.tv_nsec;
}

static double ms_since(uint64_t start)
{
    return (double)(now_ns() - start) / 1e6;
}

static double cpu_ms(void)
{
    struct rusage ru;

    getrusage(RUSAGE_SELF, &ru);
    return (double)(ru.ru_utime.tv_sec + ru.ru_stime.tv_sec) * 1000.0
        + (double)(ru.ru_utime.tv_usec + ru.ru_stime.tv_usec) / 1000.0;
}

/* ---------------------------------------------------------------- descriptors and terminals */

static int set_nonblock(int fd, int on)
{
    int flags = fcntl(fd, F_GETFL);

    if (flags < 0)
        return -1;
    return fcntl(fd, F_SETFL, on ? (flags | O_NONBLOCK) : (flags & ~O_NONBLOCK));
}

enum { MODE_RAW, MODE_RAW_OPOST, MODE_CANON };

/*
 * MODE_RAW is cfmakeraw. MODE_RAW_OPOST adds MiniPty's output flags (OPOST ONLCR), which change nothing for output
 * without newlines. MODE_CANON is canonical input without echo, so input never turns into output.
 */
static int tty_mode(int fd, int mode, speed_t speed)
{
    struct termios t;

    if (tcgetattr(fd, &t) != 0)
        return -1;
    if (mode == MODE_CANON) {
        t.c_lflag |= ICANON;
        t.c_lflag &= ~(tcflag_t)(ECHO | ECHOE | ECHOK | ECHONL);
    } else {
        cfmakeraw(&t);
        if (mode == MODE_RAW_OPOST)
            t.c_oflag |= OPOST | ONLCR;
    }
    if (speed != 0) {
        cfsetispeed(&t, speed);
        cfsetospeed(&t, speed);
    }
    return tcsetattr(fd, TCSANOW, &t);
}

/* A master (non-blocking, close-on-exec) and its slave (blocking, close-on-exec), neither a controlling terminal. */
static int open_pair(int *master, int *slave)
{
    const char *name;
    int saved;
    int m;
    int s;

    m = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (m < 0)
        return -1;
    if (grantpt(m) != 0 || unlockpt(m) != 0 || (name = ptsname(m)) == NULL)
        goto fail;
    s = open(name, O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (s < 0)
        goto fail;
    if (set_nonblock(m, 1) != 0) {
        close(s);
        goto fail;
    }
    *master = m;
    *slave = s;
    return 0;

fail:
    saved = errno;
    close(m);
    errno = saved;
    return -1;
}

/*
 * The last close of a blocking slave waits until the master has read its pending output (BSD ttylclose waits in
 * ttywflush), so the probe closes its own slaves non-blocking: that discards pending output instead of hanging.
 */
static void close_slave(int slave)
{
    set_nonblock(slave, 1);
    close(slave);
}

/* Writes until EAGAIN, 64 bytes and then 1 byte at a time; stops at 1 MiB for a queue that never fills. */
static size_t fill(int fd)
{
    char chunk[64];
    size_t total = 0;
    ssize_t w;

    memset(chunk, 'y', sizeof(chunk));
    while (total < (1u << 20) && (w = write(fd, chunk, sizeof(chunk))) > 0)
        total += (size_t)w;
    while (total < (1u << 20) && write(fd, chunk, 1) == 1)
        total++;
    return total;
}

/* Reads until EAGAIN or end of file; returns the bytes read and leaves the last result in *last and *last_errno. */
static size_t drain(int fd, ssize_t *last, int *last_errno)
{
    char buf[READ_BUFFER];
    size_t total = 0;
    ssize_t r;

    for (;;) {
        r = read(fd, buf, sizeof(buf));
        if (r > 0) {
            total += (size_t)r;
            continue;
        }
        if (r < 0 && errno == EINTR)
            continue;
        break;
    }
    if (last != NULL)
        *last = r;
    if (last_errno != NULL)
        *last_errno = r < 0 ? errno : 0;
    return total;
}

/* In a forked child: start a session whose controlling terminal is slave, on stdin, stdout, and stderr. */
static void become_session_leader(int slave, int keep)
{
    if (setsid() < 0)
        _exit(120);
    if (ioctl(slave, TIOCSCTTY, 0) < 0)
        _exit(121);
    if (dup2(slave, STDIN_FILENO) < 0 || dup2(slave, STDOUT_FILENO) < 0 || dup2(slave, STDERR_FILENO) < 0)
        _exit(122);
    for (int fd = STDERR_FILENO + 1; fd < CHILD_CLOSE_LIMIT; fd++) {
        if (fd != keep)
            close(fd);
    }
}

static int write_full(int fd, const char *data, size_t len)
{
    while (len > 0) {
        ssize_t w = write(fd, data, len);
        if (w < 0) {
            if (errno == EINTR)
                continue;
            return -1;
        }
        data += w;
        len -= (size_t)w;
    }
    return 0;
}

/* ---------------------------------------------------------------- kqueue */

static int kq_ctl(int kq, int fd, short filter, unsigned short flags, unsigned int fflags, intptr_t udata)
{
    struct kevent change;

    EV_SET(&change, (uintptr_t)fd, filter, flags, fflags, 0, (void *)udata);
    return kevent(kq, &change, 1, NULL, 0, NULL);
}

static int kq_wait(int kq, struct kevent *events, int capacity, int timeout_ms)
{
    struct timespec ts;
    struct timespec *tp = NULL;
    int n;

    if (timeout_ms >= 0) {
        ts.tv_sec = timeout_ms / 1000;
        ts.tv_nsec = (long)(timeout_ms % 1000) * 1000000L;
        tp = &ts;
    }
    do {
        n = kevent(kq, NULL, 0, events, capacity, tp);
    } while (n < 0 && errno == EINTR);
    return n;
}

static const char *ev_text(const struct kevent *e, char *buf, size_t len)
{
    const char *filter = e->filter == EVFILT_READ ? "READ"
        : e->filter == EVFILT_WRITE ? "WRITE"
        : e->filter == EVFILT_PROC ? "PROC" : "OTHER";

    snprintf(buf, len, "%s%s%s data=%ld fflags=0x%x", filter, (e->flags & EV_EOF) ? "+EV_EOF" : "",
        (e->flags & EV_ERROR) ? "+EV_ERROR" : "", (long)e->data, (unsigned)e->fflags);
    return buf;
}

static const struct kevent *find_event(const struct kevent *events, int n, int fd, short filter)
{
    for (int i = 0; i < n; i++) {
        if ((int)events[i].ident == fd && events[i].filter == filter)
            return &events[i];
    }
    return NULL;
}

/* ---------------------------------------------------------------- select bit sets */

/*
 * Bit sets sized for the highest descriptor rather than FD_SETSIZE. 64-bit words on a little-endian machine have
 * the bit layout of both macOS's 32-bit and FreeBSD's 64-bit fd_set words.
 */
typedef struct {
    uint64_t *words;
    size_t count;
} bits_t;

static void bits_alloc(bits_t *b, int max_fd)
{
    b->count = (size_t)max_fd / 64 + 1;
    b->words = calloc(b->count, sizeof(uint64_t));
}

static void bits_free(bits_t *b)
{
    free(b->words);
    b->words = NULL;
}

static void bits_set(bits_t *b, int fd)
{
    b->words[fd / 64] |= 1ull << (fd % 64);
}

static void bits_unset(bits_t *b, int fd)
{
    b->words[fd / 64] &= ~(1ull << (fd % 64));
}

static int bits_has(const bits_t *b, int fd)
{
    return (int)((b->words[fd / 64] >> (fd % 64)) & 1u);
}

static void bits_copy(bits_t *to, const bits_t *from)
{
    memcpy(to->words, from->words, from->count * sizeof(uint64_t));
}

static int select_wait(int max_fd, bits_t *readable, bits_t *writable, int timeout_ms)
{
    struct timeval tv;
    struct timeval *tp = NULL;
    int n;

    if (timeout_ms >= 0) {
        tv.tv_sec = timeout_ms / 1000;
        tv.tv_usec = (timeout_ms % 1000) * 1000;
        tp = &tv;
    }
    do {
        n = select(max_fd + 1, readable != NULL ? (fd_set *)(void *)readable->words : NULL,
            writable != NULL ? (fd_set *)(void *)writable->words : NULL, NULL, tp);
    } while (n < 0 && errno == EINTR);
    return n;
}

/* ---------------------------------------------------------------- one-descriptor snapshot through every mechanism */

typedef struct {
    int kq_error;
    int kq_read;
    int kq_write;
    char kq_read_text[96];
    char kq_write_text[96];
    int sel_read;
    int sel_write;
    short poll_revents;
} snap_t;

/* What each mechanism reports for fd right now, through a fresh registration and a zero timeout. */
static void snapshot(int fd, snap_t *s)
{
    struct kevent changes[2];
    struct kevent events[4];
    struct timespec zero = { 0, 0 };
    const struct kevent *e;
    struct pollfd pfd;
    bits_t r;
    bits_t w;
    int kq;
    int n;

    memset(s, 0, sizeof(*s));
    kq = kqueue();
    EV_SET(&changes[0], (uintptr_t)fd, EVFILT_READ, EV_ADD, 0, 0, NULL);
    EV_SET(&changes[1], (uintptr_t)fd, EVFILT_WRITE, EV_ADD, 0, 0, NULL);
    n = kevent(kq, changes, 2, events, 4, &zero);
    for (int i = 0; i < n; i++) {
        if (events[i].flags & EV_ERROR && events[i].data != 0)
            s->kq_error = (int)events[i].data;
    }
    if ((e = find_event(events, n, fd, EVFILT_READ)) != NULL && !(e->flags & EV_ERROR)) {
        s->kq_read = 1;
        ev_text(e, s->kq_read_text, sizeof(s->kq_read_text));
    }
    if ((e = find_event(events, n, fd, EVFILT_WRITE)) != NULL && !(e->flags & EV_ERROR)) {
        s->kq_write = 1;
        ev_text(e, s->kq_write_text, sizeof(s->kq_write_text));
    }
    close(kq);

    bits_alloc(&r, fd);
    bits_alloc(&w, fd);
    bits_set(&r, fd);
    bits_set(&w, fd);
    if (select_wait(fd, &r, &w, 0) >= 0) {
        s->sel_read = bits_has(&r, fd);
        s->sel_write = bits_has(&w, fd);
    }
    bits_free(&r);
    bits_free(&w);

    pfd.fd = fd;
    pfd.events = POLLIN | POLLOUT;
    pfd.revents = 0;
    if (poll(&pfd, 1, 0) >= 0)
        s->poll_revents = pfd.revents;
}

static const char *poll_text(short revents, char *buf, size_t len)
{
    snprintf(buf, len, "%s%s%s%s%s", (revents & POLLIN) ? "IN " : "", (revents & POLLOUT) ? "OUT " : "",
        (revents & POLLHUP) ? "HUP " : "", (revents & POLLERR) ? "ERR " : "", (revents & POLLNVAL) ? "NVAL " : "");
    if (buf[0] == '\0')
        snprintf(buf, len, "none");
    return buf;
}

static void snapshot_info(const char *label, const snap_t *s)
{
    char p[48];

    info("%s: kqueue read={%s} write={%s}%s%s; select read=%d write=%d; poll {%s}", label,
        s->kq_read ? s->kq_read_text : "none", s->kq_write ? s->kq_write_text : "none",
        s->kq_error ? " registration error " : "", s->kq_error ? errno_name(s->kq_error) : "", s->sel_read,
        s->sel_write, poll_text(s->poll_revents, p, sizeof(p)));
}

/* ---------------------------------------------------------------- T0 environment */

static void t0_environment(void)
{
    struct utsname u;
    struct rlimit rl;
    struct termios t;
    int m;
    int s;

    section("T0", "Environment");
    uname(&u);
    info("%s %s %s", u.sysname, u.release, u.machine);
#if defined(__APPLE__)
    {
        char version[64];
        size_t len = sizeof(version);
        int ptmx_max = 0;

        if (sysctlbyname("kern.osproductversion", version, &len, NULL, 0) == 0)
            info("macOS %s", version);
        len = sizeof(ptmx_max);
        if (sysctlbyname("kern.tty.ptmx_max", &ptmx_max, &len, NULL, 0) == 0)
            info("kern.tty.ptmx_max=%d (PTYs allowed in the whole system)", ptmx_max);
    }
#endif
    if (getrlimit(RLIMIT_NOFILE, &rl) == 0)
        info("RLIMIT_NOFILE soft=%llu hard=%llu, FD_SETSIZE=%d", (unsigned long long)rl.rlim_cur,
            (unsigned long long)rl.rlim_max, FD_SETSIZE);
    if (open_pair(&m, &s) == 0) {
        if (tcgetattr(s, &t) == 0)
            info("a new PTY starts at %lu baud, %s mode", (unsigned long)cfgetospeed(&t),
                (t.c_lflag & ICANON) ? "canonical" : "raw");
        close_slave(s);
        close(m);
    }
}

/* ---------------------------------------------------------------- T1 level-triggered read */

static void t1_read_level(void)
{
    int rc;
    struct kevent ev[4];
    char text[96];
    char data[100];
    char buf[READ_BUFFER];
    snap_t snap;
    ssize_t r1;
    ssize_t r2;
    ssize_t r3;
    long data_after_write;
    int kq;
    int m;
    int s;
    int n;

    section("T1", "Level-triggered EVFILT_READ on a master (the probe holds both ends; raw mode)");
    if (open_pair(&m, &s) != 0) {
        report(0, "open a PTY: %s", strerror(errno));
        return;
    }
    tty_mode(s, MODE_RAW, 0);
    kq = kqueue();
    rc = kq_ctl(kq, m, EVFILT_READ, EV_ADD, 0, 0);
    report(rc == 0, "EVFILT_READ registers on a master (%s)", rc == 0 ? "ok" : errno_name(errno));

    n = kq_wait(kq, ev, 4, 0);
    report(n == 0, "an idle master reports nothing (events=%d)", n);

    memset(data, 'x', sizeof(data));
    write_full(s, data, sizeof(data));
    n = kq_wait(kq, ev, 4, 1000);
    data_after_write = n > 0 ? (long)ev[0].data : -1;
    report(n == 1 && ev[0].filter == EVFILT_READ, "readable once the slave writes 100 bytes (%s)",
        n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "no event in 1 s");

    n = kq_wait(kq, ev, 4, 0);
    report(n == 1, "level-triggered: reported again without a read (%s)",
        n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "no event");

    r1 = read(m, buf, 40);
    n = kq_wait(kq, ev, 4, 0);
    report(n == 1, "after reading %zd of them, still reported (%s)", r1,
        n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "no event");
    info("EVFILT_READ data: %ld after the write, %ld after the partial read (%zd bytes unread)", data_after_write,
        n > 0 ? (long)ev[0].data : -1L, (ssize_t)sizeof(data) - r1);

    r2 = read(m, buf, sizeof(buf));
    n = kq_wait(kq, ev, 4, 50);
    report(n == 0, "drained (read %zd more): not reported within 50 ms (events=%d)", r2, n);
    r3 = read(m, buf, sizeof(buf));
    report(r3 < 0 && errno == EAGAIN, "a read after the drain fails with EAGAIN (%s)",
        io_result(r3, errno, text, sizeof(text)));

    /* Edge-triggered for comparison: the current disposal discard registers the master this way. */
    kq_ctl(kq, m, EVFILT_READ, EV_DELETE, 0, 0);
    kq_ctl(kq, m, EVFILT_READ, EV_ADD | EV_CLEAR, 0, 0);
    write_full(s, data, 10);
    {
        int first = kq_wait(kq, ev, 4, 1000);
        int second = kq_wait(kq, ev, 4, 0);
        info("EV_CLEAR for comparison: first wait events=%d, second wait without a read events=%d", first, second);
    }
    drain(m, NULL, NULL);
    close(kq);

    write_full(s, data, 5);
    snapshot(m, &snap);
    snapshot_info("5 bytes unread", &snap);
    report(snap.sel_read, "select reports readable while output is unread");
    outcome(0, (snap.poll_revents & POLLIN) != 0, "poll reports POLLIN while output is unread");
    drain(m, NULL, NULL);
    snapshot(m, &snap);
    snapshot_info("drained", &snap);
    report(!snap.sel_read && !snap.kq_read, "kqueue and select report nothing readable once drained");
    outcome(0, (snap.poll_revents & POLLIN) == 0, "poll reports no POLLIN once drained");
    close_slave(s);
    close(m);

    /* Output stopped by ^S (IXON, which MiniPty sets): queued output must not look readable until ^Q. */
    if (open_pair(&m, &s) == 0) {
        struct termios t;
        snap_t stopped;

        tty_mode(s, MODE_RAW, 0);
        tcgetattr(s, &t);
        t.c_iflag |= IXON;
        tcsetattr(s, TCSANOW, &t);
        set_nonblock(s, 1);
        write(m, "\x13", 1);
        usleep(10000);
        r1 = write(s, "abc", 3);
        snapshot(m, &stopped);
        snapshot_info("stopped by ^S, 3 bytes queued", &stopped);
        report(!stopped.kq_read && !stopped.sel_read,
            "output stopped by ^S: neither kqueue nor select reports the queued bytes readable (write took %zd)", r1);
        write(m, "\x11", 1);
        usleep(10000);
        snapshot(m, &snap);
        report(snap.kq_read && snap.sel_read, "after ^Q the queued bytes are readable (%s)",
            snap.kq_read ? snap.kq_read_text : "kqueue reports nothing");
        close_slave(s);
        close(m);
    }
}

/* ---------------------------------------------------------------- T2 level-triggered write */

static void t2_write_level(void)
{
    int rc;
    struct kevent ev[4];
    char text[96];
    char buf[READ_BUFFER];
    snap_t snap;
    size_t accepted;
    size_t consumed;
    ssize_t r;
    int kq;
    int m;
    int s;
    int n;

    section("T2", "Level-triggered EVFILT_WRITE on a master (input toward a slave that does not read)");

    /* Before the slave is first opened: macOS 14 and earlier refused FIONBIO on such a master (Go #22099). */
    m = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (m >= 0 && grantpt(m) == 0 && unlockpt(m) == 0) {
        int nb = set_nonblock(m, 1);
        int nb_errno = errno;
        ssize_t w;

        info("O_NONBLOCK on a master whose slave was never opened: fcntl %s",
            nb == 0 ? "succeeded" : errno_name(nb_errno));
        snapshot(m, &snap);
        snapshot_info("slave never opened", &snap);
        w = write(m, "z", 1);
        info("slave never opened: a write to the master returns %s", io_result(w, errno, text, sizeof(text)));
    }
    if (m >= 0)
        close(m);

    if (open_pair(&m, &s) != 0) {
        report(0, "open a PTY: %s", strerror(errno));
        return;
    }
    tty_mode(s, MODE_RAW, 0);
    set_nonblock(s, 1);
    kq = kqueue();
    rc = kq_ctl(kq, m, EVFILT_WRITE, EV_ADD, 0, 0);
    report(rc == 0, "EVFILT_WRITE registers on a master (%s)", rc == 0 ? "ok" : errno_name(errno));
    n = kq_wait(kq, ev, 4, 0);
    report(n == 1, "an empty input queue reports writable (%s)", n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "none");

    accepted = fill(m);
    info("raw mode: the master took %zu bytes before EAGAIN", accepted);
    n = kq_wait(kq, ev, 4, 100);
    report(n == 0, "a full input queue is not reported writable within 100 ms (%s)",
        n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "no event");
    snapshot(m, &snap);
    report(!snap.sel_write, "select does not report a full input queue writable");
    outcome(0, (snap.poll_revents & POLLOUT) == 0, "poll reports no POLLOUT for a full input queue");

    /* How much the slave must read before the master is reported writable again. */
    consumed = 0;
    n = 0;
    while (consumed < accepted) {
        r = read(s, buf, 1);
        if (r <= 0)
            break;
        consumed += (size_t)r;
        n = kq_wait(kq, ev, 4, 0);
        if (n > 0)
            break;
    }
    info("writable again after the slave read %zu of the %zu queued bytes (%s)", consumed, accepted,
        n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "never");
    drain(s, NULL, NULL);
    n = kq_wait(kq, ev, 4, 0);
    report(n == 1, "once the slave drains, writable again (%s)", n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "none");
    snapshot(m, &snap);
    report(snap.sel_write, "select reports the drained queue writable");
    close(kq);
    close_slave(s);
    close(m);

    /* Canonical mode: a line without a newline past MAX_INPUT is dropped, not refused. */
    if (open_pair(&m, &s) == 0) {
        char line[2000];

        tty_mode(s, MODE_CANON, 0);
        memset(line, 'z', sizeof(line));
        r = write(m, line, sizeof(line));
        snapshot(m, &snap);
        info("canonical, 2,000 bytes without a newline: write returned %s; writable afterwards: kqueue=%d select=%d",
            io_result(r, errno, text, sizeof(text)), snap.kq_write, snap.sel_write);
        close_slave(s);
        close(m);
    }
    if (open_pair(&m, &s) == 0) {
        char line[64];
        size_t total = 0;
        ssize_t w;

        tty_mode(s, MODE_CANON, 0);
        memset(line, 'z', sizeof(line) - 1);
        line[sizeof(line) - 1] = '\n';
        while (total < (1u << 20) && (w = write(m, line, sizeof(line))) > 0)
            total += (size_t)w;
        snapshot(m, &snap);
        info("canonical, 64-byte lines: the master took %zu bytes before %s", total,
            total >= (1u << 20) ? "the probe stopped at 1 MiB" : "EAGAIN");
        report(!snap.kq_write && !snap.sel_write,
            "canonical with complete lines: a full queue is not reported writable (kqueue=%d select=%d)",
            snap.kq_write, snap.sel_write);
        close_slave(s);
        close(m);
    }
}

/* ---------------------------------------------------------------- T3 end of file after the slave closes */

/* Checks a master whose slave is gone; pending is how many bytes the slave left. */
static void t3_after_close(int m, size_t pending, size_t expect_readable, const char *label)
{
    struct kevent changes[2];
    struct kevent ev[8];
    struct timespec zero = { 0, 0 };
    const struct kevent *e;
    char text[96];
    char t2[48];
    snap_t snap;
    ssize_t last;
    ssize_t w;
    size_t got;
    int last_errno;
    int repeats = 0;
    int kq;
    int n;

    snapshot(m, &snap);
    snapshot_info(label, &snap);
    report(snap.sel_read, "%s: select reports readable", label);

    kq = kqueue();
    EV_SET(&changes[0], (uintptr_t)m, EVFILT_READ, EV_ADD, 0, 0, NULL);
    EV_SET(&changes[1], (uintptr_t)m, EVFILT_WRITE, EV_ADD, 0, 0, NULL);
    n = kevent(kq, changes, 2, ev, 8, &zero);
    e = find_event(ev, n, m, EVFILT_READ);
    report(e != NULL && (e->flags & EV_EOF) != 0, "%s: EVFILT_READ reports EV_EOF (%s)", label,
        e != NULL ? ev_text(e, text, sizeof(text)) : "not reported");
    e = find_event(ev, n, m, EVFILT_WRITE);
    info("%s: EVFILT_WRITE %s", label, e != NULL ? ev_text(e, text, sizeof(text)) : "not reported");

    got = drain(m, &last, &last_errno);
    if (pending > 0) {
        report(got == expect_readable, "%s: %zu of the %zu pending bytes are readable (expected %zu)", label, got,
            pending, expect_readable);
    }
    report(last == 0 || (last < 0 && last_errno == EIO), "%s: then read ends with %s", label,
        io_result(last, last_errno, text, sizeof(text)));

    for (int i = 0; i < 5; i++) {
        n = kq_wait(kq, ev, 8, 0);
        if (find_event(ev, n, m, EVFILT_READ) != NULL)
            repeats++;
    }
    info("%s: after the end was read, EVFILT_READ was reported again on %d of 5 waits", label, repeats);
    kq_ctl(kq, m, EVFILT_READ, EV_DELETE, 0, 0);
    kq_ctl(kq, m, EVFILT_WRITE, EV_DELETE, 0, 0);
    n = kq_wait(kq, ev, 8, 50);
    report(n == 0, "%s: after EV_DELETE nothing is reported (events=%d)", label, n);
    close(kq);

    w = write(m, "z", 1);
    info("%s: a write to the master returns %s", label, io_result(w, errno, t2, sizeof(t2)));
}

static void t3_slave_close(void)
{
    char data[50];
    int m;
    int s;

    section("T3", "End of file after the slave closes (no session)");
    memset(data, 'x', sizeof(data));

    /* a) Nothing pending: the probe closes its slave. */
    if (open_pair(&m, &s) == 0) {
        tty_mode(s, MODE_RAW, 0);
        close(s);
        t3_after_close(m, 0, 0, "slave closed, nothing pending");
        close(m);
    }

    /* b) Pending output, closed non-blocking: BSD flushes the queues on such a close. */
    if (open_pair(&m, &s) == 0) {
        tty_mode(s, MODE_RAW, 0);
        write_full(s, data, sizeof(data));
        close_slave(s);
        t3_after_close(m, sizeof(data), 0, "slave closed non-blocking with 50 bytes pending");
        close(m);
    }

    /* c) Pending output, last close blocking, by a child that is not a session leader: does it wait for the read? */
    if (open_pair(&m, &s) == 0) {
        struct kevent ev[2];
        uint64_t start;
        double exit_ms = -1;
        int exited_unread;
        int status;
        pid_t pid;
        int kq;

        tty_mode(s, MODE_RAW, 0);
        fflush(stdout);
        pid = fork();
        if (pid == 0) {
            if (write_full(s, data, sizeof(data)) != 0)
                _exit(2);
            close(s);
            _exit(0);
        }
        close(s);
        kq = kqueue();
        kq_ctl(kq, pid, EVFILT_PROC, EV_ADD | EV_ONESHOT, NOTE_EXIT, 0);
        start = now_ns();
        exited_unread = kq_wait(kq, ev, 2, 500) > 0;
        info("a child's blocking last close with 50 bytes pending: the child %s within 500 ms while the master was "
             "not read",
            exited_unread ? "exited" : "did not exit");
        if (!exited_unread) {
            uint64_t read_at = now_ns();
            size_t got;
            snap_t snap;

            snapshot(m, &snap);
            snapshot_info("pending output, child blocked in close", &snap);
            got = drain(m, NULL, NULL);
            if (kq_wait(kq, ev, 2, 1000) > 0)
                exit_ms = ms_since(read_at);
            report(got == sizeof(data), "the child's blocking close waited until the master read its %zu of 50 bytes, "
                   "and it exited %.2f ms after the read", got, exit_ms);
        } else {
            info("the child exited %.1f ms after the fork with its output unread; %zu of 50 bytes are readable",
                ms_since(start), drain(m, NULL, NULL));
        }
        t3_after_close(m, 0, 0, "after the child's blocking close");
        waitpid(pid, &status, 0);
        close(kq);
        close(m);
    }

    /* d) The slave closes and opens again: does EV_EOF stay on the registration made before? */
    if (open_pair(&m, &s) == 0) {
        struct kevent ev[4];
        char name[128];
        char text[96];
        char t2[48];
        ssize_t r;
        int kq;
        int n;

        snprintf(name, sizeof(name), "%s", ptsname(m));
        tty_mode(s, MODE_RAW, 0);
        kq = kqueue();
        kq_ctl(kq, m, EVFILT_READ, EV_ADD, 0, 0);
        close(s);
        n = kq_wait(kq, ev, 4, 1000);
        info("slave closed: %s", n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "no event");
        drain(m, NULL, NULL);
        s = open(name, O_RDWR | O_NOCTTY | O_CLOEXEC);
        n = kq_wait(kq, ev, 4, 0);
        r = read(m, data, sizeof(data));
        info("slave opened again (%s): the old registration reports %s while read returns %s",
            s >= 0 ? "ok" : errno_name(errno), n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "nothing",
            io_result(r, errno, t2, sizeof(t2)));
        kq_ctl(kq, m, EVFILT_READ, EV_DELETE, 0, 0);
        kq_ctl(kq, m, EVFILT_READ, EV_ADD, 0, 0);
        n = kq_wait(kq, ev, 4, 0);
        info("slave opened again: after EV_DELETE and EV_ADD the registration reports %s",
            n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "nothing");
        if (s >= 0) {
            write_full(s, data, 5);
            n = kq_wait(kq, ev, 4, 1000);
            info("slave opened again and wrote 5 bytes: %s", n > 0 ? ev_text(&ev[0], text, sizeof(text)) : "nothing");
            close_slave(s);
        }
        close(kq);
        close(m);
    }
}

/* ---------------------------------------------------------------- T4 end of file after the session leader exits */

/* t4_exit_case variants. */
#define EXIT_PROBE_HOLDS_SLAVE 1 /* the probe keeps its own copy of the slave open, so the exit is not the last close */
#define EXIT_CHILD_NONBLOCK 2    /* the leader sets O_NONBLOCK on its stdout before it exits */

/*
 * The leader writes size bytes and exits. The probe leaves the master unread for read_after_ms (or until the exit,
 * if that comes first), then reads it with a level-triggered loop until end of file. checked: whether every byte
 * must arrive; reads that start late only measure how long the terminal keeps unread output after the exit.
 */
static void t4_exit_case(size_t size, int read_after_ms, int checked, int variant)
{
    struct kevent ev[4];
    static char data[65536];
    char buf[READ_BUFFER];
    char text[48];
    double exit_ms = -1;
    double eof_ms = -1;
    double read_ms;
    size_t got = 0;
    uint64_t start;
    int eof_errno = 0;
    int eof_flag = 0;
    int status;
    pid_t pid;
    int kq;
    int m;
    int s;

    if (open_pair(&m, &s) != 0) {
        report(0, "open a PTY: %s", strerror(errno));
        return;
    }
    tty_mode(s, MODE_RAW_OPOST, B38400);
    memset(data, 'a', sizeof(data));
    fflush(stdout);
    pid = fork();
    if (pid == 0) {
        become_session_leader(s, -1);
        if (write_full(STDOUT_FILENO, data, size) != 0)
            _exit(2);
        if (variant & EXIT_CHILD_NONBLOCK)
            set_nonblock(STDOUT_FILENO, 1);
        _exit(0);
    }
    if (!(variant & EXIT_PROBE_HOLDS_SLAVE))
        close(s);
    kq = kqueue();
    kq_ctl(kq, pid, EVFILT_PROC, EV_ADD | EV_ONESHOT, NOTE_EXIT, 0);
    start = now_ns();
    if (read_after_ms > 0 && kq_wait(kq, ev, 4, read_after_ms) > 0)
        exit_ms = ms_since(start);
    read_ms = ms_since(start);
    kq_ctl(kq, m, EVFILT_READ, EV_ADD, 0, 0);
    while (ms_since(start) < read_ms + 5000 && (eof_ms < 0 || exit_ms < 0)) {
        int n = kq_wait(kq, ev, 4, 100);

        for (int i = 0; i < n; i++) {
            ssize_t r;

            if (ev[i].filter == EVFILT_PROC) {
                exit_ms = ms_since(start);
                continue;
            }
            eof_flag |= (ev[i].flags & EV_EOF) != 0;
            r = read(m, buf, sizeof(buf));
            if (r > 0) {
                got += (size_t)r;
            } else if (r == 0 || errno != EAGAIN) {
                eof_ms = ms_since(start);
                eof_errno = r < 0 ? errno : 0;
                kq_ctl(kq, m, EVFILT_READ, EV_DELETE, 0, 0);
            }
        }
    }
    if (exit_ms < 0)
        kill(pid, SIGKILL);
    waitpid(pid, &status, 0);
    outcome(checked, got == size,
        "leader wrote %5zu bytes and exited%s; reading from %4.0f ms: %5zu bytes arrived, exit at %6.1f ms, end of "
        "file at %6.1f ms (%s%s)",
        size,
        (variant & EXIT_PROBE_HOLDS_SLAVE) ? " (probe holds the slave)"
            : (variant & EXIT_CHILD_NONBLOCK) ? " (stdout O_NONBLOCK)" : "",
        read_ms, got, exit_ms, eof_ms, io_result(eof_errno == 0 ? 0 : -1, eof_errno, text, sizeof(text)),
        eof_flag ? ", EV_EOF" : "");
    if (variant & EXIT_PROBE_HOLDS_SLAVE)
        close_slave(s);
    close(kq);
    close(m);
}

/* The leader writes "L" and exits at once; a grandchild that ignores SIGHUP keeps the slave and writes "G" 200 ms later. */
static void t4_grandchild(void)
{
    struct kevent ev[8];
    char text[96];
    char buf[256];
    int report_pipe[2];
    int grandchild[3] = { -2, 0, 0 };
    size_t l_bytes = 0;
    size_t g_bytes = 0;
    double exit_ms = -1;
    double eof_ms = -1;
    double first_data_ms = -1;
    int eof_errno = 0;
    int eof_flag_seen = 0;
    int status;
    uint64_t start;
    pid_t pid;
    int kq;
    int m;
    int s;

    if (open_pair(&m, &s) != 0 || pipe(report_pipe) != 0) {
        report(0, "open a PTY and a pipe: %s", strerror(errno));
        return;
    }
    tty_mode(s, MODE_RAW_OPOST, B38400);
    fflush(stdout);
    pid = fork();
    if (pid == 0) {
        pid_t g;

        close(report_pipe[0]);
        become_session_leader(s, report_pipe[1]);
        /* Before the fork: the leader's exit sends SIGHUP to the foreground group at once. */
        signal(SIGHUP, SIG_IGN);
        g = fork();
        if (g == 0) {
            int msg[3];
            ssize_t w;

            usleep(200000);
            w = write(STDOUT_FILENO, "G", 1);
            msg[0] = (int)getpid();
            msg[1] = (int)w;
            msg[2] = w < 0 ? errno : 0;
            write(report_pipe[1], msg, sizeof(msg));
            _exit(0);
        }
        write(STDOUT_FILENO, "L", 1);
        _exit(0);
    }
    close(s);
    close(report_pipe[1]);
    kq = kqueue();
    kq_ctl(kq, pid, EVFILT_PROC, EV_ADD | EV_ONESHOT, NOTE_EXIT, 0);
    kq_ctl(kq, m, EVFILT_READ, EV_ADD, 0, 0);
    start = now_ns();
    while (ms_since(start) < 600) {
        int n = kq_wait(kq, ev, 8, 50);

        for (int i = 0; i < n; i++) {
            if (ev[i].filter == EVFILT_PROC) {
                exit_ms = ms_since(start);
                continue;
            }
            if (ev[i].flags & EV_EOF)
                eof_flag_seen = 1;
            for (;;) {
                ssize_t r = read(m, buf, sizeof(buf));

                if (r > 0) {
                    if (first_data_ms < 0)
                        first_data_ms = ms_since(start);
                    for (ssize_t j = 0; j < r; j++) {
                        if (buf[j] == 'L')
                            l_bytes++;
                        else if (buf[j] == 'G')
                            g_bytes++;
                    }
                    continue;
                }
                if (r < 0 && errno == EAGAIN)
                    break;
                eof_ms = ms_since(start);
                eof_errno = r < 0 ? errno : 0;
                kq_ctl(kq, m, EVFILT_READ, EV_DELETE, 0, 0);
                break;
            }
        }
    }
    {
        struct pollfd pfd = { report_pipe[0], POLLIN, 0 };

        if (poll(&pfd, 1, 500) > 0 && read(report_pipe[0], grandchild, sizeof(grandchild)) != (ssize_t)sizeof(grandchild))
            grandchild[0] = -2;
    }
    info("grandchild: leader exit at %.1f ms, 'L' read at %.1f ms, end of output at %.1f ms (%s)%s", exit_ms,
        first_data_ms, eof_ms, eof_ms < 0 ? "none" : io_result(eof_errno == 0 ? 0 : -1, eof_errno, text, sizeof(text)),
        eof_flag_seen ? ", EV_EOF seen" : "");
    report(l_bytes == 1, "grandchild: the leader's output written before its exit arrived (%zu byte)", l_bytes);
    report(eof_ms >= 0 && eof_ms < 150,
        "grandchild: output ended when the leader exited, although the grandchild still held the slave (%.1f ms)",
        eof_ms);
    info("grandchild: 'G' %s; the grandchild's write 200 ms after the leader's exit returned %s", g_bytes ? "arrived" : "never arrived",
        grandchild[0] == -2 ? "(no report)" : io_result(grandchild[1], grandchild[2], text, sizeof(text)));
    waitpid(pid, &status, 0);
    close(report_pipe[0]);
    close(kq);
    close(m);
}

static void t4_leader_exit(void)
{
    section("T4", "End of file after the session leader exits (the slave is its controlling terminal)");
    /* A loop that reads at once gets everything, also output larger than the terminal holds. */
    t4_exit_case(100, 0, 1, 0);
    t4_exit_case(65536, 0, 1, 0);
    /* Output left unread when the exit is the slave's last close: how long the terminal keeps it. */
    t4_exit_case(900, 300, 1, 0);
    t4_exit_case(900, 550, 0, 0);
    t4_exit_case(900, 650, 0, 0);
    t4_exit_case(100, 1500, 0, 0);
    /* More than the terminal holds: the leader cannot exit until the probe reads, so nothing is lost. */
    t4_exit_case(4096, 1500, 1, 0);
    /* Not the last close: the exit waits for the master without a bound. */
    t4_exit_case(900, 1500, 0, EXIT_PROBE_HOLDS_SLAVE);
    /* A non-blocking last close discards at once. */
    t4_exit_case(900, 300, 0, EXIT_CHILD_NONBLOCK);
    t4_grandchild();
}

/* ---------------------------------------------------------------- T5 buffer and floods */

static void t5_capacity(void)
{
    static const speed_t speeds[] = { B9600, B38400, B115200, B230400 };
    size_t best = 0;
    size_t at_38400 = 0;

    for (size_t i = 0; i < sizeof(speeds) / sizeof(speeds[0]); i++) {
        struct kevent ev[2];
        char buf[READ_BUFFER];
        size_t out_capacity;
        size_t in_capacity;
        size_t resume;
        long data = -1;
        ssize_t r;
        int kq;
        int m;
        int s;

        if (open_pair(&m, &s) != 0)
            continue;
        tty_mode(s, MODE_RAW, speeds[i]);
        set_nonblock(s, 1);
        out_capacity = fill(s);
        kq = kqueue();
        kq_ctl(kq, m, EVFILT_READ, EV_ADD, 0, 0);
        if (kq_wait(kq, ev, 2, 0) > 0)
            data = (long)ev[0].data;
        close(kq);
        /* How much the master must read before a non-blocking slave write is accepted again. */
        resume = 0;
        while (resume < out_capacity && (r = read(m, buf, 1)) == 1) {
            resume++;
            if (write(s, "y", 1) == 1)
                break;
        }
        drain(m, NULL, NULL);
        in_capacity = fill(m);
        info("%6lu baud: the slave wrote %zu bytes before EAGAIN (master EVFILT_READ data=%ld); a write was accepted "
             "again after the master read %zu; the master wrote %zu bytes of input before EAGAIN",
            (unsigned long)speeds[i], out_capacity, data, resume, in_capacity);
        if (speeds[i] == B38400)
            at_38400 = out_capacity;
        if (out_capacity > best) {
            best = out_capacity;
            g_best_speed = speeds[i];
        }
        close_slave(s);
        close(m);
    }
    /* A faster line only earns its own flood series if it holds more than MiniPty's 38400 baud. */
    if (best <= at_38400)
        g_best_speed = 0;
}

enum { KIND_THREADS, KIND_SELECT, KIND_KQUEUE, KIND_COUNT };
static const char *const kind_names[KIND_COUNT] = { "threads", "select", "kqueue" };

typedef struct {
    int master;
    pid_t pid;
    _Atomic uint64_t bytes;
    uint64_t reads;
    uint64_t max_read;
    int done;
    int end_errno;
} flood_session_t;

typedef struct {
    double seconds;
    double mbps;
    double cpu_ms;
    double fair_min;
    double fair_max;
    uint64_t bytes;
    uint64_t expected;
    uint64_t wakes;
    uint64_t reads;
    uint64_t eagain;
    uint64_t max_read;
    int stalled;
    int children_ok;
    int end_errno;
} flood_result_t;

static flood_session_t *g_fs;
static int g_nfs;
static uint64_t g_bytes_each;
static atomic_int g_first_done;
static double g_fair_min;
static double g_fair_max;

/* When the first session ends, records how far the others were, as a share of their output. */
static void flood_session_end(int index, int err)
{
    g_fs[index].done = 1;
    g_fs[index].end_errno = err;
    if (atomic_exchange(&g_first_done, 1) == 0) {
        double lo = 1.0;
        double hi = 1.0;

        if (g_nfs > 1) {
            lo = 1e9;
            hi = 0;
            for (int i = 0; i < g_nfs; i++) {
                double share;

                if (i == index)
                    continue;
                share = (double)atomic_load(&g_fs[i].bytes) / (double)g_bytes_each;
                lo = share < lo ? share : lo;
                hi = share > hi ? share : hi;
            }
        }
        g_fair_min = lo;
        g_fair_max = hi;
    }
}

static void *flood_reader_thread(void *arg)
{
    flood_session_t *fs = arg;
    char buf[READ_BUFFER];

    for (;;) {
        ssize_t r = read(fs->master, buf, sizeof(buf));

        if (r > 0) {
            atomic_fetch_add(&fs->bytes, (uint64_t)r);
            fs->reads++;
            if ((uint64_t)r > fs->max_read)
                fs->max_read = (uint64_t)r;
            continue;
        }
        if (r < 0 && errno == EINTR)
            continue;
        fs->reads++;
        flood_session_end((int)(fs - g_fs), r == 0 ? 0 : errno);
        return NULL;
    }
}

/* One read per readiness report. Returns 1 when the session ended. */
static int flood_read_once(int index, char *buf, flood_result_t *res)
{
    ssize_t r = read(g_fs[index].master, buf, READ_BUFFER);

    res->reads++;
    if (r > 0) {
        atomic_fetch_add(&g_fs[index].bytes, (uint64_t)r);
        if ((uint64_t)r > res->max_read)
            res->max_read = (uint64_t)r;
        return 0;
    }
    if (r < 0 && (errno == EAGAIN || errno == EINTR)) {
        res->eagain++;
        return 0;
    }
    flood_session_end(index, r == 0 ? 0 : errno);
    return 1;
}

static void flood_child(uint64_t total)
{
    static char chunk[4096];

    memset(chunk, 'x', sizeof(chunk));
    while (total > 0) {
        size_t n = total < sizeof(chunk) ? (size_t)total : sizeof(chunk);
        ssize_t w = write(STDOUT_FILENO, chunk, n);

        if (w < 0) {
            if (errno == EINTR)
                continue;
            _exit(3);
        }
        total -= (uint64_t)w;
    }
    _exit(0);
}

/* Floods nsess children of bytes_each and reads them with one loop kind. */
static void flood_run(int kind, int nsess, uint64_t bytes_each, speed_t speed, flood_result_t *res)
{
    flood_session_t fs[FLOOD_MAX_SESSIONS];
    int slaves[FLOOD_MAX_SESSIONS];
    char buf[READ_BUFFER];
    uint64_t start;
    double cpu_start;
    int active = nsess;

    memset(res, 0, sizeof(*res));
    memset(fs, 0, sizeof(fs));
    g_fs = fs;
    g_nfs = nsess;
    g_bytes_each = bytes_each;
    atomic_store(&g_first_done, 0);
    for (int i = 0; i < nsess; i++) {
        if (open_pair(&fs[i].master, &slaves[i]) != 0) {
            report(0, "open a PTY: %s", strerror(errno));
            for (int j = 0; j < i; j++) {
                close_slave(slaves[j]);
                close(fs[j].master);
            }
            res->stalled = 1;
            return;
        }
        tty_mode(slaves[i], MODE_RAW_OPOST, speed);
    }
    fflush(stdout);
    for (int i = 0; i < nsess; i++) {
        pid_t pid = fork();

        if (pid == 0) {
            become_session_leader(slaves[i], -1);
            flood_child(bytes_each);
        }
        fs[i].pid = pid;
    }
    for (int i = 0; i < nsess; i++)
        close(slaves[i]);

    start = now_ns();
    cpu_start = cpu_ms();
    if (kind == KIND_THREADS) {
        pthread_t threads[FLOOD_MAX_SESSIONS];

        for (int i = 0; i < nsess; i++) {
            set_nonblock(fs[i].master, 0);
            pthread_create(&threads[i], NULL, flood_reader_thread, &fs[i]);
        }
        for (int i = 0; i < nsess; i++)
            pthread_join(threads[i], NULL);
        for (int i = 0; i < nsess; i++) {
            res->reads += fs[i].reads;
            if (fs[i].max_read > res->max_read)
                res->max_read = fs[i].max_read;
        }
        res->wakes = res->reads;
    } else if (kind == KIND_SELECT) {
        bits_t all;
        bits_t ready;
        int max_fd = 0;

        for (int i = 0; i < nsess; i++)
            max_fd = fs[i].master > max_fd ? fs[i].master : max_fd;
        bits_alloc(&all, max_fd);
        bits_alloc(&ready, max_fd);
        for (int i = 0; i < nsess; i++)
            bits_set(&all, fs[i].master);
        while (active > 0) {
            int n;

            bits_copy(&ready, &all);
            n = select_wait(max_fd, &ready, NULL, 10000);
            if (n <= 0) {
                res->stalled = 1;
                break;
            }
            res->wakes++;
            for (int i = 0; i < nsess; i++) {
                if (fs[i].done || !bits_has(&ready, fs[i].master))
                    continue;
                if (flood_read_once(i, buf, res)) {
                    active--;
                    bits_unset(&all, fs[i].master);
                }
            }
        }
        bits_free(&all);
        bits_free(&ready);
    } else {
        struct kevent ev[64];
        int kq = kqueue();

        for (int i = 0; i < nsess; i++)
            kq_ctl(kq, fs[i].master, EVFILT_READ, EV_ADD, 0, i);
        while (active > 0) {
            int n = kq_wait(kq, ev, 64, 10000);

            if (n <= 0) {
                res->stalled = 1;
                break;
            }
            res->wakes++;
            for (int j = 0; j < n; j++) {
                int i = (int)(intptr_t)ev[j].udata;

                if (fs[i].done)
                    continue;
                if (flood_read_once(i, buf, res)) {
                    active--;
                    kq_ctl(kq, fs[i].master, EVFILT_READ, EV_DELETE, 0, 0);
                }
            }
        }
        close(kq);
    }
    res->seconds = (double)(now_ns() - start) / 1e9;
    res->cpu_ms = cpu_ms() - cpu_start;

    res->children_ok = 1;
    for (int i = 0; i < nsess; i++) {
        int status = 0;

        if (res->stalled)
            kill(fs[i].pid, SIGKILL);
        waitpid(fs[i].pid, &status, 0);
        if (!WIFEXITED(status) || WEXITSTATUS(status) != 0)
            res->children_ok = 0;
        res->bytes += atomic_load(&fs[i].bytes);
        if (fs[i].end_errno != 0)
            res->end_errno = fs[i].end_errno;
        close(fs[i].master);
    }
    res->expected = (uint64_t)nsess * bytes_each;
    res->mbps = (double)res->bytes / res->seconds / 1e6;
    res->fair_min = g_fair_min;
    res->fair_max = g_fair_max;
}

static void flood_series(const char *label, int nsess, uint64_t bytes_each, speed_t speed, int kinds_mask)
{
    flood_result_t sum[KIND_COUNT];
    int runs[KIND_COUNT] = { 0 };
    int all_ok = 1;

    memset(sum, 0, sizeof(sum));
    for (int round = 0; round < g_rounds; round++) {
        for (int kind = 0; kind < KIND_COUNT; kind++) {
            flood_result_t r;
            char text[48];

            if (!(kinds_mask & (1 << kind)))
                continue;
            flood_run(kind, nsess, bytes_each, speed, &r);
            info("%s %-7s round %d: %.1f MB/s, %llu wakes, %llu reads (%.0f B/read, max %llu), %llu EAGAIN, "
                 "%.1f ms CPU (%.2f ms/MiB), ended by %s",
                label, kind_names[kind], round + 1, r.mbps, (unsigned long long)r.wakes, (unsigned long long)r.reads,
                r.reads ? (double)r.bytes / (double)r.reads : 0.0, (unsigned long long)r.max_read,
                (unsigned long long)r.eagain, r.cpu_ms, r.cpu_ms / ((double)r.bytes / 1048576.0),
                io_result(r.end_errno == 0 ? 0 : -1, r.end_errno, text, sizeof(text)));
            if (nsess > 1)
                info("%s %-7s round %d: when the first session ended, the others had %.0f%%-%.0f%% of their output",
                    label, kind_names[kind], round + 1, r.fair_min * 100, r.fair_max * 100);
            if (r.stalled || r.bytes != r.expected || !r.children_ok) {
                all_ok = 0;
                report(0, "%s %s round %d: received %llu of %llu bytes%s%s", label, kind_names[kind], round + 1,
                    (unsigned long long)r.bytes, (unsigned long long)r.expected, r.stalled ? ", stalled 10 s" : "",
                    r.children_ok ? "" : ", a child failed to write");
            }
            sum[kind].mbps += r.mbps;
            sum[kind].cpu_ms += r.cpu_ms / ((double)r.bytes / 1048576.0);
            sum[kind].reads += r.reads;
            sum[kind].bytes += r.bytes;
            sum[kind].wakes += r.wakes;
            runs[kind]++;
        }
    }
    for (int kind = 0; kind < KIND_COUNT; kind++) {
        if (runs[kind] == 0)
            continue;
        info("%s %-7s mean of %d: %.1f MB/s, %.0f B/read, %.2f reads/wake, %.2f ms CPU/MiB", label, kind_names[kind],
            runs[kind], sum[kind].mbps / runs[kind], (double)sum[kind].bytes / (double)sum[kind].reads,
            (double)sum[kind].reads / (double)sum[kind].wakes, sum[kind].cpu_ms / runs[kind]);
    }
    report(all_ok, "%s: every byte of every flood arrived and every child exited 0", label);
}

static void t5_floods(void)
{
    uint64_t single = (uint64_t)g_flood_mib << 20;
    uint64_t each = single / 4;
    char label[64];

    section("T5", "The master's buffer, and floods read by threads, one select loop, and one kqueue loop");
    t5_capacity();
    flood_series("1x flood @38400", 1, single, B38400, 7);
    snprintf(label, sizeof(label), "%dx flood @38400", FLOOD_MAX_SESSIONS);
    flood_series(label, FLOOD_MAX_SESSIONS, each, B38400, 7);
    if (g_best_speed != 0 && g_best_speed != B38400) {
        snprintf(label, sizeof(label), "1x flood @%lu", (unsigned long)g_best_speed);
        flood_series(label, 1, single, g_best_speed, 7);
    }
}

/* ---------------------------------------------------------------- T6 many masters */

static int cmp_u64(const void *a, const void *b)
{
    uint64_t x = *(const uint64_t *)a;
    uint64_t y = *(const uint64_t *)b;

    return x < y ? -1 : x > y;
}

static void latency_info(const char *mech, int n, uint64_t *lat, int rounds, int wrong, int checked)
{
    qsort(lat, (size_t)rounds, sizeof(uint64_t), cmp_u64);
    info("%-6s N=%-4d write-to-ready p50 %.1f us, p99 %.1f us, max %.1f us", mech, n, (double)lat[rounds / 2] / 1e3,
        (double)lat[(rounds * 99) / 100] / 1e3, (double)lat[rounds - 1] / 1e3);
    outcome(checked, wrong == 0, "%-6s N=%-4d each of %d waits reported exactly the master written (%d wrong)", mech, n,
        rounds, wrong);
}

enum { MECH_KQUEUE, MECH_SELECT, MECH_POLL };

/* Idle wait, one active master at a time, and all active at once, through one mechanism over masters[0..n). */
static void t6_mechanism(int mech, const int *masters, const int *slaves, int n)
{
    static const char *const names[] = { "kqueue", "select", "poll" };
    const char *name = names[mech];
    int checked = mech != MECH_POLL;
    uint64_t *lat = calloc((size_t)g_latency_rounds, sizeof(uint64_t));
    unsigned char *seen = calloc((size_t)n, 1);
    struct pollfd *pfds = NULL;
    struct kevent ev[64];
    char buf[64];
    bits_t all = { 0 };
    bits_t ready = { 0 };
    double cpu_start;
    uint64_t start;
    int registered = 0;
    int max_fd = 0;
    int wrong = 0;
    int got = 0;
    int dup = 0;
    int waits = 0;
    int kq = -1;
    int k;

    for (int i = 0; i < n; i++)
        max_fd = masters[i] > max_fd ? masters[i] : max_fd;
    if (mech == MECH_KQUEUE) {
        kq = kqueue();
        for (int i = 0; i < n; i++)
            registered += kq_ctl(kq, masters[i], EVFILT_READ, EV_ADD, 0, i) == 0;
        if (n == 1 || registered != n)
            report(registered == n, "kqueue N=%-4d all masters register (%d)", n, registered);
    } else if (mech == MECH_SELECT) {
        bits_alloc(&all, max_fd);
        bits_alloc(&ready, max_fd);
        for (int i = 0; i < n; i++)
            bits_set(&all, masters[i]);
    } else {
        pfds = calloc((size_t)n, sizeof(struct pollfd));
        for (int i = 0; i < n; i++) {
            pfds[i].fd = masters[i];
            pfds[i].events = POLLIN;
        }
    }

/* Waits and leaves the indexes of the ready masters in ready_idx[0..count). */
#define T6_WAIT(timeout_ms, ready_idx, count)                                                                          \
    do {                                                                                                               \
        count = 0;                                                                                                     \
        if (mech == MECH_KQUEUE) {                                                                                     \
            k = kq_wait(kq, ev, 64, timeout_ms);                                                                       \
            for (int q = 0; q < k; q++)                                                                                \
                ready_idx[count++] = (int)(intptr_t)ev[q].udata;                                                       \
        } else if (mech == MECH_SELECT) {                                                                              \
            bits_copy(&ready, &all);                                                                                   \
            k = select_wait(max_fd, &ready, NULL, timeout_ms);                                                         \
            for (int q = 0; k > 0 && q < n; q++) {                                                                     \
                if (bits_has(&ready, masters[q]))                                                                      \
                    ready_idx[count++] = q;                                                                            \
            }                                                                                                          \
        } else {                                                                                                       \
            k = poll(pfds, (nfds_t)n, timeout_ms);                                                                     \
            for (int q = 0; k > 0 && q < n; q++) {                                                                     \
                if (pfds[q].revents != 0)                                                                              \
                    ready_idx[count++] = q;                                                                            \
            }                                                                                                          \
        }                                                                                                              \
    } while (0)

    {
        int *idx = calloc((size_t)n, sizeof(int));
        int count;

        cpu_start = cpu_ms();
        start = now_ns();
        T6_WAIT(500, idx, count);
        outcome(checked, count == 0, "%-6s N=%-4d idle 500 ms: no master reported (%d), %.2f ms CPU in %.0f ms", name,
            n, count, cpu_ms() - cpu_start, ms_since(start));

        for (int r = 0; r < g_latency_rounds; r++) {
            int target = rand() % n;
            uint64_t t0 = now_ns();

            write(slaves[target], "x", 1);
            T6_WAIT(1000, idx, count);
            lat[r] = now_ns() - t0;
            if (count != 1 || idx[0] != target)
                wrong++;
            for (int q = 0; q < count; q++)
                (void)read(masters[idx[q]], buf, sizeof(buf));
            if (count == 0)
                (void)read(masters[target], buf, sizeof(buf));
        }
        latency_info(name, n, lat, g_latency_rounds, wrong, checked);

        for (int i = 0; i < n; i++)
            write(slaves[i], "x", 1);
        start = now_ns();
        while (got < n) {
            T6_WAIT(1000, idx, count);
            if (count == 0)
                break;
            waits++;
            for (int q = 0; q < count; q++) {
                if (seen[idx[q]])
                    dup++;
                else
                    got++;
                seen[idx[q]] = 1;
                (void)read(masters[idx[q]], buf, sizeof(buf));
            }
        }
        outcome(checked, got == n && dup == 0,
            "%-6s N=%-4d all active: %d of %d reported once each (%d repeats) in %d waits, %.2f ms", name, n, got, n, dup,
            waits, ms_since(start));
        free(idx);
    }
#undef T6_WAIT

    if (kq >= 0)
        close(kq);
    bits_free(&all);
    bits_free(&ready);
    free(pfds);
    free(seen);
    free(lat);
}

/* Drains every reported master once per wait; counts reports per parity of the index. */
static void t6_drain_parity(int kq, const int *masters, int *even, int *odd)
{
    struct kevent ev[64];
    char buf[64];
    int n;

    *even = 0;
    *odd = 0;
    while ((n = kq_wait(kq, ev, 64, 0)) > 0) {
        for (int j = 0; j < n; j++) {
            int i = (int)(intptr_t)ev[j].udata;

            if (i % 2)
                (*odd)++;
            else
                (*even)++;
            (void)read(masters[i], buf, sizeof(buf));
        }
    }
}

/* Interest that follows state: disable and re-enable, delete and re-add, and write interest. */
static void t6_interest(const int *masters, const int *slaves, int n)
{
    struct kevent ev[64];
    int kq = kqueue();
    int evens = (n + 1) / 2;
    int odds = n / 2;
    int even;
    int odd;
    int writable = 0;
    int k;

    for (int i = 0; i < n; i++)
        kq_ctl(kq, masters[i], EVFILT_READ, EV_ADD, 0, i);
    for (int i = 1; i < n; i += 2)
        kq_ctl(kq, masters[i], EVFILT_READ, EV_DISABLE, 0, i);
    for (int i = 0; i < n; i++)
        write(slaves[i], "x", 1);
    t6_drain_parity(kq, masters, &even, &odd);
    report(even == evens && odd == 0, "kqueue N=%-4d EV_DISABLE on half: only the %d enabled masters reported (%d, %d disabled)",
        n, evens, even, odd);
    for (int i = 1; i < n; i += 2)
        kq_ctl(kq, masters[i], EVFILT_READ, EV_ENABLE, 0, i);
    t6_drain_parity(kq, masters, &even, &odd);
    report(odd == odds && even == 0, "kqueue N=%-4d EV_ENABLE: output that arrived while disabled is reported at once (%d of %d)",
        n, odd, odds);

    for (int i = 1; i < n; i += 2)
        kq_ctl(kq, masters[i], EVFILT_READ, EV_DELETE, 0, 0);
    for (int i = 0; i < n; i++)
        write(slaves[i], "x", 1);
    t6_drain_parity(kq, masters, &even, &odd);
    for (int i = 1; i < n; i += 2)
        kq_ctl(kq, masters[i], EVFILT_READ, EV_ADD, 0, i);
    t6_drain_parity(kq, masters, &even, &odd);
    report(odd == odds && even == 0, "kqueue N=%-4d EV_DELETE then EV_ADD: output that arrived meanwhile is reported on the add (%d of %d)",
        n, odd, odds);

    for (int i = 0; i < n; i++)
        kq_ctl(kq, masters[i], EVFILT_WRITE, EV_ADD, 0, i);
    while ((k = kq_wait(kq, ev, 64, 0)) > 0) {
        for (int j = 0; j < k; j++) {
            if (ev[j].filter == EVFILT_WRITE) {
                writable++;
                kq_ctl(kq, (int)ev[j].ident, EVFILT_WRITE, EV_DELETE, 0, 0);
            }
        }
    }
    report(writable == n, "kqueue N=%-4d EVFILT_WRITE: every master with room reports writable (%d)", n, writable);
    close(kq);
}

/* Closes every slave and checks that each master reports end of file until it is deregistered. */
static void t6_eof(const int *masters, int *slaves, int n)
{
    struct kevent ev[64];
    char buf[64];
    uint64_t start;
    int first;
    int second;
    int ended = 0;
    int not_eof = 0;
    int kq = kqueue();
    int k;

    for (int i = 0; i < n; i++)
        kq_ctl(kq, masters[i], EVFILT_READ, EV_ADD, 0, i);
    for (int i = 0; i < n; i++) {
        close_slave(slaves[i]);
        slaves[i] = -1;
    }
    first = kq_wait(kq, ev, 64, 1000);
    second = kq_wait(kq, ev, 64, 0);
    info("kqueue N=%-4d after every slave closed, two waits without reads returned %d and %d events", n, first, second);
    start = now_ns();
    while (ended < n && (k = kq_wait(kq, ev, 64, 1000)) > 0) {
        for (int j = 0; j < k; j++) {
            int i = (int)(intptr_t)ev[j].udata;
            ssize_t r = read(masters[i], buf, sizeof(buf));

            if (!(ev[j].flags & EV_EOF))
                not_eof++;
            if (r == 0 || (r < 0 && errno == EIO)) {
                ended++;
                kq_ctl(kq, masters[i], EVFILT_READ, EV_DELETE, 0, 0);
            }
        }
    }
    report(ended == n && not_eof == 0, "kqueue N=%-4d every master reported EV_EOF and ended once read (%d, %d without EV_EOF), %.2f ms",
        n, ended, not_eof, ms_since(start));
    k = kq_wait(kq, ev, 64, 50);
    report(k == 0, "kqueue N=%-4d nothing is reported once every master is deregistered (%d)", n, k);
    close(kq);
}

static void t6_many(void)
{
    struct rlimit rl;
    int sizes[4];
    int nsizes = 0;
    int limit = g_max_ptys;
    int count = 0;
    int open_errno = 0;
    int max_fd = 0;
    int *masters;
    int *slaves;

    section("T6", "Many masters in one wait");
#if defined(__APPLE__)
    {
        int ptmx_max = 0;
        size_t len = sizeof(ptmx_max);

        if (sysctlbyname("kern.tty.ptmx_max", &ptmx_max, &len, NULL, 0) == 0 && limit > ptmx_max - 16) {
            info("kern.tty.ptmx_max=%d caps the target of %d at %d", ptmx_max, limit, ptmx_max - 16);
            limit = ptmx_max - 16;
        }
    }
#endif
    if (getrlimit(RLIMIT_NOFILE, &rl) == 0 && rl.rlim_cur < (rlim_t)(HIGH_FD_BASE + 2 * limit + 64)) {
        rl.rlim_cur = (rlim_t)(HIGH_FD_BASE + 2 * limit + 64);
        if (rl.rlim_cur > rl.rlim_max)
            rl.rlim_cur = rl.rlim_max;
        setrlimit(RLIMIT_NOFILE, &rl);
    }
    masters = calloc((size_t)limit, sizeof(int));
    slaves = calloc((size_t)limit, sizeof(int));
    while (count < limit) {
        int m;
        int s;
        int high;

        if (open_pair(&m, &s) != 0) {
            open_errno = errno;
            break;
        }
        high = fcntl(m, F_DUPFD_CLOEXEC, HIGH_FD_BASE);
        if (high < 0) {
            open_errno = errno;
            close_slave(s);
            close(m);
            break;
        }
        close(m);
        tty_mode(s, MODE_RAW, 0);
        masters[count] = high;
        slaves[count] = s;
        max_fd = high > max_fd ? high : max_fd;
        count++;
    }
    info("opened %d PTYs of the %d targeted%s%s; masters at descriptors %d-%d (FD_SETSIZE %d)", count, limit,
        open_errno ? ", then the next failed: " : "", open_errno ? errno_name(open_errno) : "",
        count ? masters[0] : -1, max_fd, FD_SETSIZE);
    if (count == 0) {
        report(0, "no PTY could be opened");
        free(masters);
        free(slaves);
        return;
    }

    sizes[nsizes++] = 1;
    if (count > 64)
        sizes[nsizes++] = 64;
    if (count > 256)
        sizes[nsizes++] = 256;
    if (count > 1)
        sizes[nsizes++] = count;
    for (int i = 0; i < nsizes; i++) {
        t6_mechanism(MECH_KQUEUE, masters, slaves, sizes[i]);
        t6_mechanism(MECH_SELECT, masters, slaves, sizes[i]);
        t6_mechanism(MECH_POLL, masters, slaves, sizes[i]);
    }
    if (count > 1)
        t6_interest(masters, slaves, count);
    t6_eof(masters, slaves, count);
    for (int i = 0; i < count; i++)
        close(masters[i]);
    free(masters);
    free(slaves);
}

/* ---------------------------------------------------------------- main */

int main(int argc, char **argv)
{
    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--only") == 0 && i + 1 < argc)
            g_only = argv[++i];
        else if (strcmp(argv[i], "--max-ptys") == 0 && i + 1 < argc)
            g_max_ptys = atoi(argv[++i]);
        else if (strcmp(argv[i], "--flood-mib") == 0 && i + 1 < argc)
            g_flood_mib = atoi(argv[++i]);
        else if (strcmp(argv[i], "--rounds") == 0 && i + 1 < argc)
            g_rounds = atoi(argv[++i]);
        else if (strcmp(argv[i], "--latency-rounds") == 0 && i + 1 < argc)
            g_latency_rounds = atoi(argv[++i]);
        else {
            fprintf(stderr,
                "usage: %s [--only T0,T1,...] [--max-ptys N] [--flood-mib N] [--rounds N] [--latency-rounds N]\n",
                argv[0]);
            return 2;
        }
    }
    if (g_max_ptys < 1 || g_flood_mib < 1 || g_rounds < 1 || g_latency_rounds < 1) {
        fprintf(stderr, "every option takes a positive number\n");
        return 2;
    }

    signal(SIGPIPE, SIG_IGN);
    srand(1);
    if (selected("T0"))
        t0_environment();
    if (selected("T1"))
        t1_read_level();
    if (selected("T2"))
        t2_write_level();
    if (selected("T3"))
        t3_slave_close();
    if (selected("T4"))
        t4_leader_exit();
    if (selected("T5"))
        t5_floods();
    if (selected("T6"))
        t6_many();
    printf("\n%d PASS, %d FAIL\n", g_pass, g_fail);
    return g_fail > 100 ? 100 : g_fail;
}
