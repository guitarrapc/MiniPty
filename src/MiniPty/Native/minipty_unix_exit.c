#include "minipty_unix_internal.h"

#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <unistd.h>

#if defined(__linux__)
#include <sys/epoll.h>
#include <sys/syscall.h>
#ifndef SYS_pidfd_open
/* pidfd_open has the same number on every architecture since Linux 5.3 (unified syscall table). */
#define SYS_pidfd_open 434
#endif
#define MINIPTY_CHILD_WATCH_EPOLL 1
#elif defined(__APPLE__) || defined(__FreeBSD__)
#include <sys/types.h>
#include <sys/event.h>
#include <sys/time.h>
#define MINIPTY_CHILD_WATCH_KQUEUE 1
#if defined(__APPLE__)
/* macOS has no pipe2, so the wake is an EVFILT_USER event on the kqueue itself; see minipty_child_watch_init. */
#define MINIPTY_CHILD_WATCH_USER_WAKE 1
#endif
#endif

/*
 * Process-wide child exit watcher.
 *
 * One managed thread blocks in minipty_child_watch_wait for every MiniPty child in the process, so
 * observing exit adds no thread per session. Linux watches pidfds with epoll; macOS and FreeBSD watch
 * EVFILT_PROC NOTE_EXIT with kqueue.
 *
 * The watcher only reports that a child exited. Reaping stays with the managed caller so the wait
 * status is read exactly once, by one owner. A child that cannot be registered (pidfd unavailable on an
 * older kernel or under seccomp, or a child that already exited before kqueue registration) makes
 * minipty_child_watch_add fail, and the caller polls waitpid for that child instead.
 *
 * A self-pipe wakes the wait when the caller's polling set changes; on macOS an EVFILT_USER event on the
 * kqueue does instead. Every descriptor is close-on-exec so later children never keep the watcher's
 * descriptors past exec. Until a concurrently forked child execs, though, it holds duplicates of them, which
 * is why pidfd registrations are one-shot and removed explicitly.
 */

static int g_watch_fd = -1;
#if !defined(MINIPTY_CHILD_WATCH_USER_WAKE)
static int g_wake_read = -1;
static int g_wake_write = -1;
#endif

#define MINIPTY_CHILD_WATCH_MAX_EVENTS 16
#define MINIPTY_CHILD_WATCH_WAKE_TOKEN UINT64_MAX

#if defined(MINIPTY_CHILD_WATCH_USER_WAKE)
/* The EVFILT_USER event's identifier. Events are keyed by identifier and filter, so it never meets a pid. */
#define MINIPTY_CHILD_WATCH_WAKE_ID 0
#else
static int minipty_set_cloexec_nonblock(int fd)
{
    int fd_flags = fcntl(fd, F_GETFD);
    int fl_flags = fcntl(fd, F_GETFL);

    if (fd_flags < 0 || fl_flags < 0)
        return -1;
    if (fcntl(fd, F_SETFD, fd_flags | FD_CLOEXEC) != 0)
        return -1;
    return fcntl(fd, F_SETFL, fl_flags | O_NONBLOCK);
}

static void minipty_drain_wake_pipe(void)
{
    char buffer[64];

    while (read(g_wake_read, buffer, sizeof(buffer)) > 0) {
    }
}
#endif

int minipty_child_watch_init(void)
{
#if defined(MINIPTY_CHILD_WATCH_USER_WAKE)
    struct kevent change;
    int saved_errno;

    if (g_watch_fd >= 0)
        return 0;

    /*
     * A pipe would be marked close-on-exec only after it opened, and a child the host forks on another thread
     * in that instant would keep both ends. A kqueue is never inherited by a fork child.
     */
    g_watch_fd = kqueue();
    if (g_watch_fd < 0)
        return -1;

    /* EV_CLEAR: reporting the event resets it, so one report drains any number of wakes. */
    EV_SET(&change, MINIPTY_CHILD_WATCH_WAKE_ID, EVFILT_USER, EV_ADD | EV_CLEAR, 0, 0, NULL);
    if (kevent(g_watch_fd, &change, 1, NULL, 0, NULL) != 0) {
        saved_errno = errno;
        close(g_watch_fd);
        g_watch_fd = -1;
        errno = saved_errno;
        return -1;
    }

    return 0;
#elif defined(MINIPTY_CHILD_WATCH_EPOLL) || defined(MINIPTY_CHILD_WATCH_KQUEUE)
    int fds[2];
    int saved_errno;

    if (g_watch_fd >= 0)
        return 0;

#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    g_watch_fd = epoll_create1(EPOLL_CLOEXEC);
#else
    /* kqueue descriptors are not inherited by fork children. */
    g_watch_fd = kqueue();
#endif
    if (g_watch_fd < 0)
        return -1;

    if (pipe(fds) != 0)
        goto fail;

    g_wake_read = fds[0];
    g_wake_write = fds[1];
    if (minipty_set_cloexec_nonblock(g_wake_read) != 0 || minipty_set_cloexec_nonblock(g_wake_write) != 0)
        goto fail;

#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    {
        struct epoll_event event;

        event.events = EPOLLIN;
        event.data.u64 = MINIPTY_CHILD_WATCH_WAKE_TOKEN;
        if (epoll_ctl(g_watch_fd, EPOLL_CTL_ADD, g_wake_read, &event) != 0)
            goto fail;
    }
#else
    {
        struct kevent change;

        EV_SET(&change, (uintptr_t)g_wake_read, EVFILT_READ, EV_ADD, 0, 0, NULL);
        if (kevent(g_watch_fd, &change, 1, NULL, 0, NULL) != 0)
            goto fail;
    }
#endif

    return 0;

fail:
    saved_errno = errno;
    if (g_wake_read >= 0)
        close(g_wake_read);
    if (g_wake_write >= 0)
        close(g_wake_write);
    close(g_watch_fd);
    g_wake_read = -1;
    g_wake_write = -1;
    g_watch_fd = -1;
    errno = saved_errno;
    return -1;
#else
    errno = ENOTSUP;
    return -1;
#endif
}

int minipty_child_watch_add(int pid)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    struct epoll_event event;
    int saved_errno;
    /* pidfds are always close-on-exec, and a pidfd for an exited, unreaped child is readable at once. */
    int pidfd = (int)syscall(SYS_pidfd_open, pid, 0);

    if (pidfd < 0)
        return -1;

    /* One-shot: the exit is reported once even if a duplicate of the pidfd keeps the registration alive. */
    event.events = EPOLLIN | EPOLLONESHOT;
    event.data.u64 = ((uint64_t)(uint32_t)pidfd << 32) | (uint32_t)pid;
    if (epoll_ctl(g_watch_fd, EPOLL_CTL_ADD, pidfd, &event) != 0) {
        saved_errno = errno;
        close(pidfd);
        errno = saved_errno;
        return -1;
    }

    return 0;
#elif defined(MINIPTY_CHILD_WATCH_KQUEUE)
    struct kevent change;

    EV_SET(&change, (uintptr_t)pid, EVFILT_PROC, EV_ADD | EV_ONESHOT, NOTE_EXIT, 0, NULL);
    return kevent(g_watch_fd, &change, 1, NULL, 0, NULL) == 0 ? 0 : -1;
#else
    (void)pid;
    errno = ENOTSUP;
    return -1;
#endif
}

int minipty_child_watch_wake(void)
{
#if defined(MINIPTY_CHILD_WATCH_USER_WAKE)
    struct kevent change;
    int result;

    EV_SET(&change, MINIPTY_CHILD_WATCH_WAKE_ID, EVFILT_USER, 0, NOTE_TRIGGER, 0, NULL);
    do {
        result = kevent(g_watch_fd, &change, 1, NULL, 0, NULL);
    } while (result < 0 && errno == EINTR);

    return result == 0 ? 0 : -1;
#else
    char byte = 1;
    ssize_t written;

    do {
        written = write(g_wake_write, &byte, 1);
    } while (written < 0 && errno == EINTR);

    /* A full pipe already guarantees a pending wake-up. */
    return written == 1 || (written < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) ? 0 : -1;
#endif
}

int minipty_child_watch_wait(int *pids, int capacity, int timeout_ms)
{
#if defined(MINIPTY_CHILD_WATCH_EPOLL) || defined(MINIPTY_CHILD_WATCH_KQUEUE)
    int max_events = capacity < MINIPTY_CHILD_WATCH_MAX_EVENTS ? capacity : MINIPTY_CHILD_WATCH_MAX_EVENTS;
    int count = 0;
    int ready;
    int i;

    if (pids == NULL || max_events <= 0) {
        errno = EINVAL;
        return -1;
    }

#if defined(MINIPTY_CHILD_WATCH_EPOLL)
    {
        struct epoll_event events[MINIPTY_CHILD_WATCH_MAX_EVENTS];

        ready = epoll_wait(g_watch_fd, events, max_events, timeout_ms);
        if (ready < 0)
            return errno == EINTR ? 0 : -1;

        for (i = 0; i < ready; i++) {
            int pidfd;

            if (events[i].data.u64 == MINIPTY_CHILD_WATCH_WAKE_TOKEN) {
                minipty_drain_wake_pipe();
                continue;
            }

            /*
             * Remove the registration explicitly before closing. Closing alone is not enough: a child forked
             * concurrently still holds a duplicate of this pidfd until it execs, and epoll drops a
             * registration only when every descriptor for the open file description is closed. A stale
             * registration would report this exit again with a pidfd number that may since have been
             * reused for an unrelated descriptor.
             */
            pidfd = (int)(events[i].data.u64 >> 32);
            epoll_ctl(g_watch_fd, EPOLL_CTL_DEL, pidfd, NULL);
            close(pidfd);
            pids[count++] = (int)(uint32_t)events[i].data.u64;
        }
    }
#else
    {
        struct kevent events[MINIPTY_CHILD_WATCH_MAX_EVENTS];
        struct timespec timeout;
        struct timespec *timeout_ptr = NULL;

        if (timeout_ms >= 0) {
            timeout.tv_sec = timeout_ms / 1000;
            timeout.tv_nsec = (long)(timeout_ms % 1000) * 1000000L;
            timeout_ptr = &timeout;
        }

        ready = kevent(g_watch_fd, NULL, 0, events, max_events, timeout_ptr);
        if (ready < 0)
            return errno == EINTR ? 0 : -1;

        for (i = 0; i < ready; i++) {
#if defined(MINIPTY_CHILD_WATCH_USER_WAKE)
            if (events[i].filter == EVFILT_USER)
                continue;
#else
            if (events[i].filter == EVFILT_READ && (int)events[i].ident == g_wake_read) {
                minipty_drain_wake_pipe();
                continue;
            }
#endif

            /* NOTE_EXIT and registration errors (EV_ERROR) both tell the caller to check the child now. */
            if (events[i].filter == EVFILT_PROC)
                pids[count++] = (int)events[i].ident;
        }
    }
#endif

    return count;
#else
    (void)pids;
    (void)capacity;
    (void)timeout_ms;
    errno = ENOTSUP;
    return -1;
#endif
}
