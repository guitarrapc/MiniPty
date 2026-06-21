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
#include <spawn.h>
#include <sys/ioctl.h>
#include <termios.h>
#elif defined(__FreeBSD__)
#include <libutil.h>
#endif

#if defined(__APPLE__)
extern char **environ;

#ifndef POSIX_SPAWN_SETSID
#define POSIX_SPAWN_SETSID 1024
#endif

static int spawn_pty_child(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    const char *file,
    char *const *argv,
    pid_t *pid_out)
{
    char slave_name[128];
    int slave = -1;
    int res;
    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attrs;
    short flags = POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSID;

    *master = posix_openpt(O_RDWR | O_NOCTTY);
    if (*master == -1)
        return -1;

    if (grantpt(*master) == -1)
        goto fail;
    if (unlockpt(*master) == -1)
        goto fail;

    if (ioctl(*master, TIOCPTYGNAME, slave_name) == -1)
        goto fail;

    slave = open(slave_name, O_RDWR | O_NOCTTY);
    if (slave == -1)
        goto fail;

    if (winp != NULL && ioctl(slave, TIOCSWINSZ, winp) == -1)
        goto fail;

    if (posix_spawn_file_actions_init(&actions) != 0)
        goto fail;
    if (posix_spawnattr_init(&attrs) != 0) {
        posix_spawn_file_actions_destroy(&actions);
        goto fail;
    }

    posix_spawn_file_actions_adddup2(&actions, slave, STDIN_FILENO);
    posix_spawn_file_actions_adddup2(&actions, slave, STDOUT_FILENO);
    posix_spawn_file_actions_adddup2(&actions, slave, STDERR_FILENO);
    posix_spawn_file_actions_addclose(&actions, slave);
    posix_spawn_file_actions_addclose(&actions, *master);

    if (cwd != NULL && cwd[0] != '\0')
        posix_spawn_file_actions_addchdir_np(&actions, cwd);

    posix_spawnattr_setflags(&attrs, flags);

    res = posix_spawn(pid_out, file, &actions, &attrs, argv, environ);
    posix_spawn_file_actions_destroy(&actions);
    posix_spawnattr_destroy(&attrs);
    close(slave);
    slave = -1;

    if (res != 0) {
        errno = res;
        goto fail;
    }

    return 0;

fail:
    if (slave != -1)
        close(slave);
    if (*master != -1) {
        close(*master);
        *master = -1;
    }
    return -1;
}

#else

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
        if (cwd != NULL && cwd[0] != '\0')
            chdir(cwd);
        execvp(file, argv);
        _exit(127);
    }

    *pid_out = pid;
    return 0;
}

#endif

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
