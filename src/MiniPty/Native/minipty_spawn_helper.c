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
    int report_fd = MINIPTY_START_REPORT_FD;
    int err;

    /*
     * The parent passes the start report pipe on this descriptor without close-on-exec, so the helper marks it before
     * anything can exec. A helper started without one (the descriptor is not open) reports nothing.
     */
    if (fcntl(report_fd, F_SETFD, FD_CLOEXEC) != 0)
        report_fd = -1;

    if (argc < 2) {
        minipty_report_start_failure(report_fd, MINIPTY_START_STAGE_EXEC, EINVAL);
        _exit(127);
    }

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
    if (cwd != NULL && cwd[0] != '\0' && chdir(cwd) != 0) {
        err = errno;
        minipty_report_start_failure(report_fd, MINIPTY_START_STAGE_CHDIR, err);
        _exit(126);
    }

    child_envp = minipty_envp_strip_internal(environ);
    if (child_envp == NULL) {
        minipty_report_start_failure(report_fd, MINIPTY_START_STAGE_EXEC, ENOMEM);
        _exit(127);
    }

    minipty_execvpe(argv[1], &argv[1], child_envp);
    err = errno;
    minipty_report_start_failure(report_fd, MINIPTY_START_STAGE_EXEC, err);
    free(child_envp);
    _exit(127);
}
