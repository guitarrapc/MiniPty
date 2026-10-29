/* Must precede every system header: glibc declares pipe2 only with _GNU_SOURCE. */
#if defined(__linux__) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE 1
#endif

#include "minipty_unix_internal.h"

#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <stdint.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#if defined(__linux__)
#include <sys/epoll.h>
#include <sys/syscall.h>
#ifndef SYS_pidfd_open
/* 434 on every architecture .NET runs on. Only alpha, ia64 and MIPS number it differently. */
#define SYS_pidfd_open 434
#endif
#ifndef SYS_pidfd_send_signal
/* 424 on every architecture .NET runs on. Only alpha, ia64 and MIPS number it differently. */
#define SYS_pidfd_send_signal 424
#endif
#ifndef P_PIDFD
/* waitid by pidfd, Linux 5.4 and later. */
#define P_PIDFD 3
#endif
#define MINIPTY_CHILD_WATCH_EPOLL 1
#elif defined(__APPLE__) || defined(__FreeBSD__)
#include <sys/event.h>
#include <sys/time.h>
#define MINIPTY_CHILD_WATCH_KQUEUE 1
#if defined(__APPLE__)
/* macOS has no pipe2, so the wake is an EVFILT_USER event on the kqueue itself; see minipty_child_watch_open. */
#define MINIPTY_CHILD_WATCH_USER_WAKE 1
#endif
#endif

/*
 * Child exit watcher, child signaling and reaping.
 *
 * One managed thread blocks in minipty_watch_wait for every child that one load of the managed assembly
 * starts, so observing exit adds no thread per session. The watcher keeps no state here: the caller holds its
 * descriptors and passes them back. Two loads in one process, such as two AssemblyLoadContexts, share this library,
 * and one shared epoll or kqueue handed one load's exits to the other load's thread. Linux watches pidfds with epoll.
 * macOS and FreeBSD watch EVFILT_PROC NOTE_EXIT with kqueue.
 *
 * The watcher only reports that a child exited. Reaping stays with the managed caller so the wait status is read
 * exactly once, by one owner. On Linux the caller also owns each child's pidfd, which the spawn opens right after the
 * fork. It signals and reaps the child through it, and once the child is reaped it removes the registration and then
 * closes the pidfd. A child that cannot be registered makes minipty_child_watch_add fail, and the caller polls that
 * child instead. On Linux that is a child without a pidfd, which the kernel did not give or the spawn refused. With
 * kqueue it is a child that already exited before its registration.
 *
 * A self-pipe wakes the wait when the caller's polling set changes. On macOS an EVFILT_USER event on the kqueue
 * does instead. Every descriptor is opened close-on-exec, so no later child ever keeps the watcher's descriptors
 * past exec: the pipe with pipe2 (macOS has none, hence the event there). Until a concurrently forked child execs,
 * though, it holds duplicates of them. epoll drops a registration only when every descriptor for the open file
 * description is closed, so pidfd registrations are one-shot and are removed explicitly before the pidfd closes.
 *
 * A PTY master that a disposal keeps open for its hang-up can also wake the wait when it has output, so the caller
 * discards a hung-up child's output as fast as the child writes it (minipty_child_watch_add_output). Such a wake reports
 * no child.
 *
 * The same wait is the I/O loop of every session: each session's master is registered with the interest its state needs
 * (minipty_watch_add), and minipty_watch_wait reports masters' readiness alongside children's exits.
 */

#define MINIPTY_CHILD_WATCH_MAX_EVENTS 16
/* Arms one wait applies before it blocks (minipty_watch_wait_arm): two kqueue changes each, within the event array's size. */
#define MINIPTY_WATCH_ARM_MAX 8
#define MINIPTY_CHILD_WATCH_WAKE_TOKEN UINT64_MAX
/* A pid token's upper half is a pidfd, never -1, so this never meets one. */
#define MINIPTY_CHILD_WATCH_OUTPUT_TOKEN (UINT64_MAX - 1)

#if defined(MINIPTY_CHILD_WATCH_USER_WAKE)
/* The EVFILT_USER event's identifier. Events are keyed by identifier and filter, so it never meets a pid. */
#define MINIPTY_CHILD_WATCH_WAKE_ID 0
#elif defined(MINIPTY_CHILD_WATCH_EPOLL) || defined(MINIPTY_CHILD_WATCH_KQUEUE)
static void minipty_drain_wake_pipe(int wake_read)
{
    char buffer[64];

    while (read(wake_read, buffer, sizeof(buffer)) > 0) {
    }
}
#endif

int minipty_child_watch_open(int *watch_fd, int *wake_read, int *wake_write)
{
#if defined(MINIPTY_CHILD_WATCH_USER_WAKE)
    struct kevent change;
    int saved_errno;
    int kq;

    if (watch_fd == NULL || wake_read == NULL || wake_write == NULL) {
        errno = EINVAL;
        return -1;
    }

    /*
     * A pipe would be marked close-on-exec only after it opened, and a child the host forks on another thread
     * in that instant would keep both ends. A kqueue is never inherited by a fork child.
     */
    kq = kqueue();
    if (kq < 0)
        return -1;

    /* EV_CLEAR: reporting the event resets it, so one report drains any number of wakes. */
    EV_SET(&change, MINIPTY_CHILD_WATCH_WAKE_ID, EVFILT_USER, EV_ADD | EV_CLEAR, 0, 0, NULL);
    if (kevent(kq, &change, 1, NULL, 0, NULL) != 0) {
        saved_errno = errno;
        close(kq);
        errno = saved_errno;
        return -1;
    }

    *watch_fd = kq;
    *wake_read = -1;
    *wake_write = -1;
    return 0;
#elif defined(MINIPTY_CHILD_WATCH_EPOLL) || defined(MINIPTY_CHILD_WATCH_KQUEUE)
    int fds[2] = { -1, -1 };
    int saved_errno;
    int watch;

    if (watch_fd == NULL || wake_read == NULL || wake_write == NULL) {
        errno = EINVAL;
        return -1;
    }

#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    watch = epoll_create1(EPOLL_CLOEXEC);
#else
    /* kqueue descriptors are not inherited by fork children. */
    watch = kqueue();
#endif
    if (watch < 0)
        return -1;

    /*
     * Both flags at open: a pipe marked afterwards would be inherited by a child the host forks on another
     * thread in between (.NET's Process.Start passes on every descriptor not marked).
     */
    if (pipe2(fds, O_CLOEXEC | O_NONBLOCK) != 0)
        goto fail;

#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    {
        struct epoll_event event;

        event.events = EPOLLIN;
        event.data.u64 = MINIPTY_CHILD_WATCH_WAKE_TOKEN;
        if (epoll_ctl(watch, EPOLL_CTL_ADD, fds[0], &event) != 0)
            goto fail;
    }
#else
    {
        struct kevent change;

        EV_SET(&change, (uintptr_t)fds[0], EVFILT_READ, EV_ADD, 0, 0, NULL);
        if (kevent(watch, &change, 1, NULL, 0, NULL) != 0)
            goto fail;
    }
#endif

    *watch_fd = watch;
    *wake_read = fds[0];
    *wake_write = fds[1];
    return 0;

fail:
    saved_errno = errno;
    if (fds[0] >= 0)
        close(fds[0]);
    if (fds[1] >= 0)
        close(fds[1]);
    close(watch);
    errno = saved_errno;
    return -1;
#else
    (void)watch_fd;
    (void)wake_read;
    (void)wake_write;
    errno = ENOTSUP;
    return -1;
#endif
}

int minipty_child_watch_add(int watch_fd, int pidfd, int pid)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    struct epoll_event event;

    if (pidfd < 0) {
        errno = EBADF;
        return -1;
    }

    /* One-shot: the exit is reported once even if a duplicate of the pidfd keeps the registration alive. */
    event.events = EPOLLIN | EPOLLONESHOT;
    event.data.u64 = ((uint64_t)(uint32_t)pidfd << 32) | (uint32_t)pid;
    return epoll_ctl(watch_fd, EPOLL_CTL_ADD, pidfd, &event) == 0 ? 0 : -1;
#elif defined(MINIPTY_CHILD_WATCH_KQUEUE)
    struct kevent change;

    (void)pidfd;
    EV_SET(&change, (uintptr_t)pid, EVFILT_PROC, EV_ADD | EV_ONESHOT, NOTE_EXIT, 0, NULL);
    return kevent(watch_fd, &change, 1, NULL, 0, NULL) == 0 ? 0 : -1;
#else
    (void)watch_fd;
    (void)pidfd;
    (void)pid;
    errno = ENOTSUP;
    return -1;
#endif
}

void minipty_child_watch_remove(int watch_fd, int pidfd)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    /* ENOENT, a pidfd never registered, is fine: the caller removes before every close. */
    if (watch_fd >= 0 && pidfd >= 0)
        epoll_ctl(watch_fd, EPOLL_CTL_DEL, pidfd, NULL);
#else
    /* A kqueue EVFILT_PROC registration is one-shot and is dropped when the process exits. */
    (void)watch_fd;
    (void)pidfd;
#endif
}

/*
 * Makes the wait return whenever master_fd has output. A macOS master buffers about 1 KiB, so a caller that read it
 * only on a timer would hold a writing child to about 1 KiB per tick. Edge-triggered (EPOLLET, EV_CLEAR): a master
 * whose every slave closed reports that once, not on every wait. Remove it with minipty_child_watch_remove_output
 * while the master is still open, because epoll keeps a registration as long as any process holds the description.
 */
int minipty_child_watch_add_output(int watch_fd, int master_fd)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    struct epoll_event event;

    event.events = EPOLLIN | EPOLLET;
    event.data.u64 = MINIPTY_CHILD_WATCH_OUTPUT_TOKEN;
    return epoll_ctl(watch_fd, EPOLL_CTL_ADD, master_fd, &event) == 0 ? 0 : -1;
#elif defined(MINIPTY_CHILD_WATCH_KQUEUE)
    struct kevent change;

    EV_SET(&change, (uintptr_t)master_fd, EVFILT_READ, EV_ADD | EV_CLEAR, 0, 0, NULL);
    return kevent(watch_fd, &change, 1, NULL, 0, NULL) == 0 ? 0 : -1;
#else
    (void)watch_fd;
    (void)master_fd;
    errno = ENOTSUP;
    return -1;
#endif
}

void minipty_child_watch_remove_output(int watch_fd, int master_fd)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    epoll_ctl(watch_fd, EPOLL_CTL_DEL, master_fd, NULL);
#elif defined(MINIPTY_CHILD_WATCH_KQUEUE)
    struct kevent change;

    EV_SET(&change, (uintptr_t)master_fd, EVFILT_READ, EV_DELETE, 0, 0, NULL);
    kevent(watch_fd, &change, 1, NULL, 0, NULL);
#else
    (void)watch_fd;
    (void)master_fd;
#endif
}

int minipty_child_watch_wake(int watch_fd, int wake_write)
{
#if defined(MINIPTY_CHILD_WATCH_USER_WAKE)
    struct kevent change;
    int result;

    (void)wake_write;
    EV_SET(&change, MINIPTY_CHILD_WATCH_WAKE_ID, EVFILT_USER, 0, NOTE_TRIGGER, 0, NULL);
    do {
        result = kevent(watch_fd, &change, 1, NULL, 0, NULL);
    } while (result < 0 && errno == EINTR);

    return result == 0 ? 0 : -1;
#else
    char byte = 1;
    ssize_t written;

    (void)watch_fd;
    do {
        written = write(wake_write, &byte, 1);
    } while (written < 0 && errno == EINTR);

    /* A full pipe already guarantees a pending wake-up. */
    return written == 1 || (written < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) ? 0 : -1;
#endif
}

/*
 * Session I/O on the watcher: a session's PTY master registered with the interest its state needs, so the one watcher
 * thread reads every session's output and writes every session's queued input. Keyed by a token the caller chooses,
 * whose top bit is set so that it never meets a child's report, the wake, or a hung-up master's.
 *
 * Each registration reports once and then stays silent until the caller sets its interest again (EPOLLONESHOT,
 * EV_DISPATCH). Readiness is checked when the interest is set, so output that arrived while it was off is reported at
 * once. A session's consumer reads the master on its own thread while output keeps coming, and arms the registration
 * only when it is about to wait, so the watcher is woken for output nobody is about to read rather than for every read.
 * Setting the interest may come from that consumer's thread while the watcher waits; both kernels allow that.
 *
 * epoll: a master whose every slave closed reports hang-up whatever the interest, so interest 0 still reports that once.
 *
 * kqueue (macOS, FreeBSD): read and write interest are two registrations of the master, EVFILT_READ and EVFILT_WRITE,
 * each enabled or disabled with the interest. A disabled filter reports nothing, end of file included, so interest 0 is
 * silent; enabling it again reports at once what arrived meanwhile. The readiness probe's rules (pty_crossplatform.md,
 * "Readiness on a pty master") apply: a master is registered only after its slave is open, because before that kqueue reports it writable
 * while writes fail with EAGAIN. EV_EOF is reported as hang-up, and the caller ends output only on a read that returns 0:
 * EV_EOF stays on a registration after a slave is opened again, while reads fail with EAGAIN, so the caller renews the
 * registration (minipty_watch_renew) when a hang-up report found nothing to read. The caller removes the master at end of
 * output, because EV_EOF repeats on every wait until then. A token needs 64 bits of udata, so a 32-bit build reports
 * ENOTSUP and its sessions fail to start.
 */
#define MINIPTY_WATCH_READ 1
#define MINIPTY_WATCH_WRITE 2

#if defined(MINIPTY_CHILD_WATCH_EPOLL)
static uint32_t minipty_watch_epoll_events(int interest)
{
    uint32_t events = 0;

    if (interest & MINIPTY_WATCH_READ)
        events |= EPOLLIN | EPOLLRDHUP;
    if (interest & MINIPTY_WATCH_WRITE)
        events |= EPOLLOUT;

    return events | EPOLLONESHOT;
}
#elif defined(MINIPTY_CHILD_WATCH_KQUEUE)
/*
 * Applies a master's registration in one kevent call: with renew, both filters are deleted first, which clears an EV_EOF
 * left from a slave closed earlier. EV_RECEIPT makes the kernel apply every change and return each one's result in order,
 * instead of stopping at the first failure, and return without waiting for events. Deleting a filter that is not there
 * fails with ENOENT, which is fine. EV_ADD on a filter already there updates it, so adding and modifying are one call.
 */
static int minipty_watch_kqueue_apply(int watch_fd, int fd, uint64_t token, int interest, int renew)
{
    static const struct timespec no_wait = { 0, 0 };
    struct kevent changes[4];
    struct kevent receipts[4];
    void *udata = (void *)(uintptr_t)token;
    int count = 0;
    int first_add;
    int done;
    int i;

    if (sizeof(void *) < sizeof(uint64_t)) {
        errno = ENOTSUP;
        return -1;
    }

    if (renew) {
        EV_SET(&changes[count++], (uintptr_t)fd, EVFILT_READ, EV_DELETE | EV_RECEIPT, 0, 0, NULL);
        EV_SET(&changes[count++], (uintptr_t)fd, EVFILT_WRITE, EV_DELETE | EV_RECEIPT, 0, 0, NULL);
    }

    /* EV_DISPATCH disables a filter once it reports, as EPOLLONESHOT does; the next apply enables it again. */
    first_add = count;
    EV_SET(&changes[count++], (uintptr_t)fd, EVFILT_READ,
        EV_ADD | EV_RECEIPT | EV_DISPATCH | ((interest & MINIPTY_WATCH_READ) ? EV_ENABLE : EV_DISABLE), 0, 0, udata);
    EV_SET(&changes[count++], (uintptr_t)fd, EVFILT_WRITE,
        EV_ADD | EV_RECEIPT | EV_DISPATCH | ((interest & MINIPTY_WATCH_WRITE) ? EV_ENABLE : EV_DISABLE), 0, 0, udata);

    do {
        done = kevent(watch_fd, changes, count, receipts, count, &no_wait);
    } while (done < 0 && errno == EINTR);

    if (done < 0)
        return -1;

    for (i = first_add; i < done; i++) {
        if ((receipts[i].flags & EV_ERROR) != 0 && receipts[i].data != 0) {
            errno = (int)receipts[i].data;
            return -1;
        }
    }

    return 0;
}

/* Deletes both filters of a master; a filter that is not there (ENOENT) is fine. */
static void minipty_watch_kqueue_remove(int watch_fd, int fd)
{
    static const struct timespec no_wait = { 0, 0 };
    struct kevent changes[2];
    struct kevent receipts[2];
    int done;

    EV_SET(&changes[0], (uintptr_t)fd, EVFILT_READ, EV_DELETE | EV_RECEIPT, 0, 0, NULL);
    EV_SET(&changes[1], (uintptr_t)fd, EVFILT_WRITE, EV_DELETE | EV_RECEIPT, 0, 0, NULL);
    do {
        done = kevent(watch_fd, changes, 2, receipts, 2, &no_wait);
    } while (done < 0 && errno == EINTR);
}
#endif

int minipty_watch_add(int watch_fd, int fd, uint64_t token, int interest)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    struct epoll_event event;

    event.events = minipty_watch_epoll_events(interest);
    event.data.u64 = token;
    return epoll_ctl(watch_fd, EPOLL_CTL_ADD, fd, &event) == 0 ? 0 : -1;
#elif defined(MINIPTY_CHILD_WATCH_KQUEUE)
    int saved_errno;

    if (minipty_watch_kqueue_apply(watch_fd, fd, token, interest, 0) == 0)
        return 0;

    /* One filter may have been added before the other failed; the caller reads this master another way. */
    saved_errno = errno;
    minipty_watch_kqueue_remove(watch_fd, fd);
    errno = saved_errno;
    return -1;
#else
    (void)watch_fd;
    (void)fd;
    (void)token;
    (void)interest;
    errno = ENOTSUP;
    return -1;
#endif
}

int minipty_watch_modify(int watch_fd, int fd, uint64_t token, int interest)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    struct epoll_event event;

    event.events = minipty_watch_epoll_events(interest);
    event.data.u64 = token;
    return epoll_ctl(watch_fd, EPOLL_CTL_MOD, fd, &event) == 0 ? 0 : -1;
#elif defined(MINIPTY_CHILD_WATCH_KQUEUE)
    return minipty_watch_kqueue_apply(watch_fd, fd, token, interest, 0);
#else
    (void)watch_fd;
    (void)fd;
    (void)token;
    (void)interest;
    errno = ENOTSUP;
    return -1;
#endif
}

/*
 * Registers a master again with the same interest, which clears a stale end of file under kqueue (see above). With
 * epoll a hang-up is never stale, since a slave whose every descriptor closed cannot open again, so this only modifies.
 */
int minipty_watch_renew(int watch_fd, int fd, uint64_t token, int interest)
{
#if defined(MINIPTY_CHILD_WATCH_KQUEUE)
    return minipty_watch_kqueue_apply(watch_fd, fd, token, interest, 1);
#else
    return minipty_watch_modify(watch_fd, fd, token, interest);
#endif
}

void minipty_watch_remove(int watch_fd, int fd)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    epoll_ctl(watch_fd, EPOLL_CTL_DEL, fd, NULL);
#elif defined(MINIPTY_CHILD_WATCH_KQUEUE)
    minipty_watch_kqueue_remove(watch_fd, fd);
#else
    (void)watch_fd;
    (void)fd;
#endif
}

/*
 * Waits for child exits, master events, and wakes. Each report is a token and its events: MINIPTY_WATCH_CHILD with a
 * token of the pidfd (upper half, -1 under kqueue) and the pid (lower half), or a master's token with its readiness. The
 * wake is drained here and never reported, and a hung-up master registered with minipty_child_watch_add_output only
 * makes the wait return. Returns the report count, 0 on timeout or interruption, -1 on failure.
 */
#define MINIPTY_WATCH_READABLE 1
#define MINIPTY_WATCH_WRITABLE 2
#define MINIPTY_WATCH_HANGUP 4
#define MINIPTY_WATCH_ERROR 8
#define MINIPTY_WATCH_CHILD 16

int minipty_watch_wait(int watch_fd, int wake_read, uint64_t *tokens, int *reports, int capacity, int timeout_ms)
{
    return minipty_watch_wait_arm(watch_fd, wake_read, NULL, NULL, NULL, 0, tokens, reports, capacity, timeout_ms);
}

/*
 * minipty_watch_wait with the arms the caller's pass accumulated applied first (minipty_watch_modify for each, in order).
 * With kqueue they go in the changelist of the same kevent call as the wait, so a pass that arms one master costs one
 * system call instead of two; a master that became ready while its filter was disabled is reported by this wait, since
 * enabling a filter re-evaluates it. A change the kernel refuses comes back as an error report for its token, so the
 * caller looks at that session, whose read then finds the descriptor's state. With epoll each arm is its own call, as
 * before. At most MINIPTY_WATCH_ARM_MAX arms per call; more fail with EINVAL.
 */
int minipty_watch_wait_arm(
    int watch_fd,
    int wake_read,
    const int *arm_fds,
    const uint64_t *arm_tokens,
    const int *arm_interests,
    int arm_count,
    uint64_t *tokens,
    int *reports,
    int capacity,
    int timeout_ms)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL) || defined(MINIPTY_CHILD_WATCH_KQUEUE)
    int max_events = capacity < MINIPTY_CHILD_WATCH_MAX_EVENTS ? capacity : MINIPTY_CHILD_WATCH_MAX_EVENTS;
    int count = 0;
    int ready;
    int i;

    if (tokens == NULL || reports == NULL || max_events <= 0 || arm_count < 0 || arm_count > MINIPTY_WATCH_ARM_MAX
        || (arm_count > 0 && (arm_fds == NULL || arm_tokens == NULL || arm_interests == NULL))) {
        errno = EINVAL;
        return -1;
    }

#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    {
        struct epoll_event events[MINIPTY_CHILD_WATCH_MAX_EVENTS];

        for (i = 0; i < arm_count; i++)
            minipty_watch_modify(watch_fd, arm_fds[i], arm_tokens[i], arm_interests[i]);

        ready = epoll_wait(watch_fd, events, max_events, timeout_ms);
        if (ready < 0)
            return errno == EINTR ? 0 : -1;

        for (i = 0; i < ready; i++) {
            uint64_t token = events[i].data.u64;
            uint32_t got = events[i].events;
            int report = 0;

            if (token == MINIPTY_CHILD_WATCH_WAKE_TOKEN) {
                minipty_drain_wake_pipe(wake_read);
                continue;
            }

            /* A hung-up child's master has output; returning is the whole report. */
            if (token == MINIPTY_CHILD_WATCH_OUTPUT_TOKEN)
                continue;

            if ((token >> 63) != 0) {
                if (got & EPOLLIN)
                    report |= MINIPTY_WATCH_READABLE;
                if (got & EPOLLOUT)
                    report |= MINIPTY_WATCH_WRITABLE;
                if (got & (EPOLLHUP | EPOLLRDHUP))
                    report |= MINIPTY_WATCH_HANGUP;
                if (got & EPOLLERR)
                    report |= MINIPTY_WATCH_ERROR;
            } else {
                /*
                 * A child's pidfd. The one-shot registration is now disarmed. It stays until the caller removes it, and
                 * the pidfd stays open, because the caller signals the child through it until the reap. The pidfd is
                 * reported too, so the caller can tell this report from one for a pidfd it has since closed.
                 */
                report = MINIPTY_WATCH_CHILD;
            }

            tokens[count] = token;
            reports[count] = report;
            count++;
        }
    }
#else
    {
        struct kevent events[MINIPTY_CHILD_WATCH_MAX_EVENTS];
        struct kevent changes[MINIPTY_WATCH_ARM_MAX * 2];
        struct timespec timeout;
        struct timespec *timeout_ptr = NULL;
        int change_count = 0;

        if (timeout_ms >= 0) {
            timeout.tv_sec = timeout_ms / 1000;
            timeout.tv_nsec = (long)(timeout_ms % 1000) * 1000000L;
            timeout_ptr = &timeout;
        }

        /* The same two filters minipty_watch_kqueue_apply sets, without EV_RECEIPT: a refused change is reported as an
         * EV_ERROR event for its token below, and the wait goes on. */
        for (i = 0; i < arm_count; i++) {
            void *udata = (void *)(uintptr_t)arm_tokens[i];

            EV_SET(&changes[change_count++], (uintptr_t)arm_fds[i], EVFILT_READ,
                EV_ADD | EV_DISPATCH | ((arm_interests[i] & MINIPTY_WATCH_READ) ? EV_ENABLE : EV_DISABLE), 0, 0, udata);
            EV_SET(&changes[change_count++], (uintptr_t)arm_fds[i], EVFILT_WRITE,
                EV_ADD | EV_DISPATCH | ((arm_interests[i] & MINIPTY_WATCH_WRITE) ? EV_ENABLE : EV_DISABLE), 0, 0, udata);
        }

        /* The changes are applied before the wait blocks, so an interrupted wait has still applied them. */
        ready = kevent(watch_fd, changes, change_count, events, max_events, timeout_ptr);
        if (ready < 0)
            return errno == EINTR ? 0 : -1;

        for (i = 0; i < ready; i++) {
#if defined(MINIPTY_CHILD_WATCH_USER_WAKE)
            (void)wake_read;
            if (events[i].filter == EVFILT_USER)
                continue;
#else
            if (events[i].filter == EVFILT_READ && (int)events[i].ident == wake_read) {
                minipty_drain_wake_pipe(wake_read);
                continue;
            }
#endif

            /*
             * NOTE_EXIT and registration errors (EV_ERROR) both tell the caller to check the child now. Any other
             * EVFILT_READ is a hung-up child's master with output, and returning is the whole report.
             */
            if (events[i].filter == EVFILT_PROC) {
                /* No pidfd under kqueue: the upper half is -1. */
                tokens[count] = ((uint64_t)UINT32_MAX << 32) | (uint32_t)events[i].ident;
                reports[count] = MINIPTY_WATCH_CHILD;
                count++;
                continue;
            }

            /*
             * A session's master, by the token in udata; a hung-up child's master has none. Its two filters are
             * separate events, joined into one report as epoll gives, so the caller serves the session once per wait.
             * EV_EOF is a hint that a read returning 0 confirms (see minipty_watch_renew).
             */
            if ((events[i].filter == EVFILT_READ || events[i].filter == EVFILT_WRITE)
                && (((uint64_t)(uintptr_t)events[i].udata) >> 63) != 0) {
                uint64_t token = (uint64_t)(uintptr_t)events[i].udata;
                int report;
                int j;

                if (events[i].flags & EV_ERROR)
                    report = MINIPTY_WATCH_ERROR;
                else
                    report = events[i].filter == EVFILT_READ ? MINIPTY_WATCH_READABLE : MINIPTY_WATCH_WRITABLE;
                if (events[i].flags & EV_EOF)
                    report |= MINIPTY_WATCH_HANGUP;

                for (j = 0; j < count; j++) {
                    if (tokens[j] == token)
                        break;
                }

                if (j < count) {
                    reports[j] |= report;
                } else {
                    tokens[count] = token;
                    reports[count] = report;
                    count++;
                }
            }
        }
    }
#endif

    return count;
#else
    (void)watch_fd;
    (void)wake_read;
    (void)tokens;
    (void)reports;
    (void)capacity;
    (void)timeout_ms;
    errno = ENOTSUP;
    return -1;
#endif
}

int minipty_pidfd_open(int pid)
{
#if defined(__linux__)
    siginfo_t info;
    int pidfd;
    int checked;
    int saved_errno;

    /* pidfds are always close-on-exec. */
    pidfd = (int)syscall(SYS_pidfd_open, pid, 0);
    if (pidfd < 0)
        return -1;

    /*
     * pidfd_open accepts any process, and the pid may name another one by now if code outside MiniPty reaped the
     * child. WNOWAIT leaves a zombie waitable, so this only checks that the pidfd names a child of this process.
     * Linux 5.3 has no P_PIDFD, and its pidfd stays unchecked.
     */
    do {
        memset(&info, 0, sizeof(info));
        checked = waitid((idtype_t)P_PIDFD, (id_t)pidfd, &info, WEXITED | WNOHANG | WNOWAIT);
    } while (checked < 0 && errno == EINTR);

    if (checked < 0 && errno != EINVAL) {
        saved_errno = errno;
        close(pidfd);
        errno = saved_errno;
        return -1;
    }

    return pidfd;
#else
    (void)pid;
    errno = ENOSYS;
    return -1;
#endif
}

int minipty_signal_child(int pidfd, int pid, int sig)
{
    siginfo_t info;
    int result;

#if defined(__linux__)
    /*
     * A pidfd keeps naming the process it was opened for, so a signal through it never reaches a process that gets the
     * pid later.
     */
    if (pidfd >= 0)
        return syscall(SYS_pidfd_send_signal, pidfd, sig, NULL, 0) == 0 ? 0 : -1;
#else
    (void)pidfd;
#endif

    /*
     * Without a pidfd the pid is checked first. WNOWAIT leaves a zombie waitable, so this reports whether pid is
     * still an unreaped child of this process without reaping it. ECHILD means code outside MiniPty reaped it, and
     * the pid may already belong to another process. The check cannot tell the session's child from another child
     * of this process that got the same pid. Only a pidfd can.
     */
    do {
        memset(&info, 0, sizeof(info));
        result = waitid(P_PID, (id_t)pid, &info, WEXITED | WNOHANG | WNOWAIT);
    } while (result < 0 && errno == EINTR);

    if (result < 0)
        return -1;

    return kill(pid, sig);
}

static void minipty_set_exit(int exited, int code_or_signal, int *exit_code, int *term_signal)
{
    if (exited) {
        *exit_code = code_or_signal;
        *term_signal = 0;
    } else {
        *exit_code = 128 + code_or_signal;
        *term_signal = code_or_signal;
    }
}

int minipty_reap_child(int pidfd, int pid, int block, int *exit_code, int *term_signal)
{
    int options = block ? 0 : WNOHANG;
    int status;
    pid_t result;

#if defined(__linux__)
    if (pidfd >= 0) {
        siginfo_t info;
        int waited;

        /*
         * By pidfd rather than by pid: once code outside MiniPty has reaped the child, its pid can name another child
         * of this process, and a reap by pid would take that child's exit and status.
         */
        do {
            memset(&info, 0, sizeof(info));
            waited = waitid((idtype_t)P_PIDFD, (id_t)pidfd, &info, WEXITED | options);
        } while (waited < 0 && errno == EINTR);

        if (waited == 0) {
            if (info.si_pid == 0)
                return 0;

            minipty_set_exit(info.si_code == CLD_EXITED, info.si_status, exit_code, term_signal);
            return 1;
        }

        /* Linux 5.3 has pidfd_open but not P_PIDFD, and reaps by pid. */
        if (errno != EINVAL)
            return -1;
    }
#else
    (void)pidfd;
#endif

    do {
        result = waitpid(pid, &status, options);
    } while (result < 0 && errno == EINTR);

    if (result < 0)
        return -1;
    if (result == 0)
        return 0;

    if (WIFEXITED(status))
        minipty_set_exit(1, WEXITSTATUS(status), exit_code, term_signal);
    else if (WIFSIGNALED(status))
        minipty_set_exit(0, WTERMSIG(status), exit_code, term_signal);
    else
        minipty_set_exit(1, 1, exit_code, term_signal);

    return 1;
}

/*
 * A consumer's own wait for a master's output, on its own thread (UnixPtyBackendInstance.TryReadInline on macOS). A
 * macOS master holds 1 KiB, so a consumer that reads a flood finds the master empty after almost every read, before
 * the child has refilled it; waiting here, woken by the child's next write, costs no wake of the loop and no hand-off
 * back to the consumer, which is what a blocking read on a thread of the session's own has. The wait is a kqueue of the
 * session's own with the master's EVFILT_READ, level-triggered, kept for the session and closed with the master. poll
 * was measured 2.5 ms per MiB slower here, since each call builds and drops a registration. The loop's kqueue cannot
 * serve: two threads waiting on one kqueue get each other's events.
 *
 * minipty_master_wait returns 1 when the master reported (output, or EV_EOF, which the caller's read tells apart), 0 on
 * the timeout or an interruption, and -1 on failure. Without kqueue both fail with ENOTSUP.
 */
int minipty_master_wait_open(int master)
{
#if defined(MINIPTY_CHILD_WATCH_KQUEUE)
    struct kevent change;
    int saved_errno;
    int kq;

    kq = kqueue();
    if (kq < 0)
        return -1;

    EV_SET(&change, (uintptr_t)master, EVFILT_READ, EV_ADD, 0, 0, NULL);
    if (kevent(kq, &change, 1, NULL, 0, NULL) != 0) {
        saved_errno = errno;
        close(kq);
        errno = saved_errno;
        return -1;
    }

    return kq;
#else
    (void)master;
    errno = ENOTSUP;
    return -1;
#endif
}

int minipty_master_wait(int wait_fd, int timeout_ms)
{
#if defined(MINIPTY_CHILD_WATCH_KQUEUE)
    struct kevent event;
    struct timespec timeout;
    int ready;

    timeout.tv_sec = timeout_ms / 1000;
    timeout.tv_nsec = (long)(timeout_ms % 1000) * 1000000L;
    ready = kevent(wait_fd, NULL, 0, &event, 1, &timeout);
    if (ready < 0)
        return errno == EINTR ? 0 : -1;

    return ready;
#else
    (void)wait_fd;
    (void)timeout_ms;
    errno = ENOTSUP;
    return -1;
#endif
}
