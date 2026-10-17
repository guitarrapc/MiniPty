#include "minipty_unix_internal.h"

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

extern char **environ;

int main(int argc, char **argv)
{
    const char *cwd;
    char **child_envp;

    if (argc < 2)
        _exit(127);

    {
        /*
         * Opening the slave by name makes it the controlling terminal of this session. A blocking open waits for
         * carrier, and a master that closed before this point never brings it back, so the helper would wait
         * forever. A non-blocking open returns at once and still takes the terminal. A terminal that has already
         * hung up reports POLLHUP, which macOS sets only when read readiness is requested, so POLLIN is asked for
         * and pending input is left unread. The helper then ends by the SIGHUP it missed, as the kernel ends a
         * session leader whose terminal hangs up. A hang-up after the open reaches it from the kernel as usual.
         */
        const char *slave = ttyname(STDIN_FILENO);
        if (slave != NULL) {
            int fd = open(slave, O_RDWR | O_NONBLOCK);
            if (fd >= 0) {
                struct pollfd pfd = { fd, POLLIN, 0 };
                if (poll(&pfd, 1, 0) > 0 && (pfd.revents & POLLHUP) != 0) {
                    raise(SIGHUP);
                    _exit(128 + SIGHUP);
                }

                close(fd);
            }
        }
    }

    cwd = minipty_env_get_cwd(environ);
    if (cwd != NULL && cwd[0] != '\0' && chdir(cwd) != 0)
        _exit(126);

    child_envp = minipty_envp_strip_internal(environ);
    if (child_envp == NULL)
        _exit(127);

    minipty_execvpe(argv[1], &argv[1], child_envp);
    free(child_envp);
    _exit(127);
}
