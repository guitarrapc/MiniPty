/* Must precede every system header: lets select() take descriptors above FD_SETSIZE on macOS. */
#if defined(__APPLE__)
#define _DARWIN_UNLIMITED_SELECT 1
#endif

#include "minipty_unix_internal.h"

#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <string.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <sys/select.h>
#else
#include <poll.h>
#endif

#if defined(__linux__)
#include <sys/eventfd.h>
#endif

/*
 * Output reader primitives.
 *
 * Each MiniPty output reader thread owns one wake descriptor and blocks in minipty_pty_wait on the PTY
 * master and that descriptor together. Closing a descriptor does not interrupt a read or poll blocked on it in
 * another thread (see close(2)), so the wake descriptor is how a reader is stopped, or told that its session
 * changed state, without closing the master underneath it. The same wait reports when the master can take
 * input again: the master is non-blocking, and stdin the tty input queue could not take at once is written by
 * the reader thread when it becomes writable.
 *
 * Linux uses one eventfd for both ends; macOS and FreeBSD use a pipe. Every descriptor is close-on-exec and
 * non-blocking, so a signal never blocks the signaler and a drain never blocks the reader.
 */

/* minipty_pty_wait events and results. */
#define MINIPTY_PTY_READABLE 1
#define MINIPTY_PTY_WOKEN 2
#define MINIPTY_PTY_WRITABLE 4

#if !defined(__linux__)
static int minipty_output_set_cloexec_nonblock(int fd)
{
    int fd_flags = fcntl(fd, F_GETFD);
    int fl_flags = fcntl(fd, F_GETFL);

    if (fd_flags < 0 || fl_flags < 0)
        return -1;
    if (fcntl(fd, F_SETFD, fd_flags | FD_CLOEXEC) != 0)
        return -1;
    return fcntl(fd, F_SETFL, fl_flags | O_NONBLOCK);
}
#endif

int minipty_wake_open(int *read_fd, int *write_fd)
{
#if defined(__linux__)
    int fd;

    if (read_fd == NULL || write_fd == NULL) {
        errno = EINVAL;
        return -1;
    }

    fd = eventfd(0, EFD_CLOEXEC | EFD_NONBLOCK);
    if (fd < 0)
        return -1;

    *read_fd = fd;
    *write_fd = fd;
    return 0;
#else
    int fds[2];
    int saved_errno;

    if (read_fd == NULL || write_fd == NULL) {
        errno = EINVAL;
        return -1;
    }

    if (pipe(fds) != 0)
        return -1;

    if (minipty_output_set_cloexec_nonblock(fds[0]) != 0 || minipty_output_set_cloexec_nonblock(fds[1]) != 0) {
        saved_errno = errno;
        close(fds[0]);
        close(fds[1]);
        errno = saved_errno;
        return -1;
    }

    *read_fd = fds[0];
    *write_fd = fds[1];
    return 0;
#endif
}

int minipty_wake_signal(int write_fd)
{
    /* An eventfd takes exactly eight bytes; a pipe takes them atomically (below PIPE_BUF). */
    uint64_t one = 1;
    ssize_t written;

    do {
        written = write(write_fd, &one, sizeof(one));
    } while (written < 0 && errno == EINTR);

    /* A full pipe already guarantees a pending wake-up. */
    return written == (ssize_t)sizeof(one) || (written < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) ? 0 : -1;
}

void minipty_wake_drain(int read_fd)
{
    uint64_t buffer[8];

    /* One eventfd read resets its counter; a pipe is read until it would block. */
    while (read(read_fd, buffer, sizeof(buffer)) > 0) {
    }
}

void minipty_wake_close(int read_fd, int write_fd)
{
    close(read_fd);
    if (write_fd != read_fd)
        close(write_fd);
}

int minipty_set_nonblocking(int fd)
{
    int flags = fcntl(fd, F_GETFL);

    if (flags < 0)
        return -1;
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

int minipty_pty_wait(int master, int events, int wake_fd, int timeout_ms)
{
    int want_read = master >= 0 && (events & MINIPTY_PTY_READABLE) != 0;
    int want_write = master >= 0 && (events & MINIPTY_PTY_WRITABLE) != 0;
#if defined(__APPLE__)
    /*
     * macOS poll() does not support devices, PTY masters included, so wait with select(). The sets are sized
     * for the highest descriptor rather than FD_SETSIZE, which unlimited select accepts.
     */
    int max_fd = master > wake_fd ? master : wake_fd;
    size_t words;
    struct timeval timeout;
    struct timeval *timeout_ptr = NULL;
    int ready;
    int result = 0;

    if (max_fd < 0 || (!want_read && !want_write && wake_fd < 0)) {
        errno = EINVAL;
        return -1;
    }

    if (timeout_ms >= 0) {
        timeout.tv_sec = timeout_ms / 1000;
        timeout.tv_usec = (timeout_ms % 1000) * 1000;
        timeout_ptr = &timeout;
    }

    words = (size_t)max_fd / __DARWIN_NFDBITS + 1;
    {
        __int32_t read_set[words];
        __int32_t write_set[words];

        memset(read_set, 0, words * sizeof(__int32_t));
        memset(write_set, 0, words * sizeof(__int32_t));
        if (want_read)
            read_set[master / __DARWIN_NFDBITS] |= (__int32_t)(1u << (master % __DARWIN_NFDBITS));
        if (want_write)
            write_set[master / __DARWIN_NFDBITS] |= (__int32_t)(1u << (master % __DARWIN_NFDBITS));
        if (wake_fd >= 0)
            read_set[wake_fd / __DARWIN_NFDBITS] |= (__int32_t)(1u << (wake_fd % __DARWIN_NFDBITS));

        ready = select(max_fd + 1, (fd_set *)read_set, want_write ? (fd_set *)write_set : NULL, NULL, timeout_ptr);
        if (ready < 0)
            return errno == EINTR ? 0 : -1;

        if (want_read && (read_set[master / __DARWIN_NFDBITS] & (__int32_t)(1u << (master % __DARWIN_NFDBITS))) != 0)
            result |= MINIPTY_PTY_READABLE;
        if (want_write && (write_set[master / __DARWIN_NFDBITS] & (__int32_t)(1u << (master % __DARWIN_NFDBITS))) != 0)
            result |= MINIPTY_PTY_WRITABLE;
        if (wake_fd >= 0 && (read_set[wake_fd / __DARWIN_NFDBITS] & (__int32_t)(1u << (wake_fd % __DARWIN_NFDBITS))) != 0)
            result |= MINIPTY_PTY_WOKEN;
    }

    return result;
#else
    struct pollfd fds[2];
    nfds_t count = 0;
    int master_index = -1;
    int wake_index = -1;
    int ready;
    int result = 0;

    if (want_read || want_write) {
        fds[count].fd = master;
        fds[count].events = (short)((want_read ? POLLIN : 0) | (want_write ? POLLOUT : 0));
        fds[count].revents = 0;
        master_index = (int)count++;
    }

    if (wake_fd >= 0) {
        fds[count].fd = wake_fd;
        fds[count].events = POLLIN;
        fds[count].revents = 0;
        wake_index = (int)count++;
    }

    if (count == 0) {
        errno = EINVAL;
        return -1;
    }

    ready = poll(fds, count, timeout_ms);
    if (ready < 0)
        return errno == EINTR ? 0 : -1;

    if (master_index >= 0) {
        short revents = fds[master_index].revents;
        /* Hang-up and errors count as ready either way: the following read or write reports them. */
        int failed = (revents & (POLLHUP | POLLERR | POLLNVAL)) != 0;

        if (want_read && ((revents & POLLIN) != 0 || failed))
            result |= MINIPTY_PTY_READABLE;
        if (want_write && ((revents & POLLOUT) != 0 || failed))
            result |= MINIPTY_PTY_WRITABLE;
    }

    if (wake_index >= 0 && fds[wake_index].revents != 0)
        result |= MINIPTY_PTY_WOKEN;

    return result;
#endif
}
