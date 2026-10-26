#include "minipty_unix_internal.h"

#include <fcntl.h>

/*
 * PTY master setup for the I/O loop (minipty_unix_exit.c), which waits for every session's master and reads and writes
 * it without blocking: a write takes what the tty input queue accepts, and a read that finds nothing waits for the next
 * readiness report.
 */
int minipty_set_nonblocking(int fd)
{
    int flags = fcntl(fd, F_GETFL);

    if (flags < 0)
        return -1;
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}
