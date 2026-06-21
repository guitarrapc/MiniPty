#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <signal.h>
#include <string.h>
#include <unistd.h>

#if defined(__linux__)
#include <pty.h>
#elif defined(__APPLE__)
#include <util.h>
#elif defined(__FreeBSD__)
#include <libutil.h>
#endif

static int spawn_pty_child(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    const char *file,
    char *const *argv,
    pid_t *pid_out)
{
    pid_t pid;
    sigset_t newmask;
    sigset_t oldmask;

    sigfillset(&newmask);
    pthread_sigmask(SIG_BLOCK, &newmask, &oldmask);

    pid = forkpty(master, NULL, NULL, (struct winsize *)winp);

    pthread_sigmask(SIG_SETMASK, &oldmask, NULL);

    if (pid < 0)
        return -1;

    if (pid == 0) {
        if (cwd != NULL && cwd[0] != '\0' && chdir(cwd) != 0)
            _exit(126);
        execvp(file, argv);
        _exit(127);
    }

    *pid_out = pid;
    return 0;
}

int minipty_fork_pty_exec(
    int *master,
    const struct winsize *winp,
    const char *working_directory,
    const char *file,
    char *const *argv,
    int *pid_out)
{
    pid_t pid = -1;

    if (spawn_pty_child(master, winp, working_directory, file, argv, &pid) != 0)
        return -1;

    *pid_out = (int)pid;
    return 0;
}
