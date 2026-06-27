#include "minipty_unix_internal.h"

#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <pthread.h>
#include <signal.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

#ifndef PATH_MAX
#define PATH_MAX 4096
#endif

#if defined(__linux__)
#include <linux/close_range.h>
#include <pty.h>
#include <sys/syscall.h>
#elif defined(__APPLE__)
#include <dlfcn.h>
#include <libgen.h>
#include <spawn.h>
#include <util.h>
#elif defined(__FreeBSD__)
#include <libutil.h>
#endif

#if !defined(__APPLE__)

#ifndef NSIG
#define NSIG 32
#endif

static void minipty_reset_child_signals(void)
{
    struct sigaction sa;

    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = SIG_DFL;
    sa.sa_flags = 0;
    sigemptyset(&sa.sa_mask);

    for (int sig = 1; sig < NSIG; sig++)
        sigaction(sig, &sa, NULL);
}

#if defined(__linux__)

static int minipty_set_close_on_exec(int fd)
{
    int flags = fcntl(fd, F_GETFD, 0);

    if (flags == -1)
        return flags;
    if (flags & FD_CLOEXEC)
        return 0;

    return fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
}

static void minipty_close_inherited_fds(void)
{
#if defined(SYS_close_range) && defined(CLOSE_RANGE_CLOEXEC)
    if (syscall(SYS_close_range, 3, ~0U, CLOSE_RANGE_CLOEXEC) == 0)
        return;
#endif

    /*
     * node-pty fallback: set CLOEXEC on fds >= 3; try the first 16 unconditionally,
     * then stop after the first error past fd 15.
     */
    for (int fd = 3; ; fd++) {
        if (minipty_set_close_on_exec(fd) && fd > 15)
            break;
    }
}

#endif /* __linux__ */

static void minipty_prepare_fork_child(void)
{
#if defined(__linux__)
    minipty_close_inherited_fds();
#endif
}

static int spawn_pty_child_forkpty(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    const char *file,
    char *const *argv,
    char *const *envp,
    pid_t *pid_out)
{
    pid_t pid;
    sigset_t newmask;
    sigset_t oldmask;
    char **child_envp = envp == NULL ? minipty_envp_for_child(NULL) : (char **)envp;

    if (child_envp == NULL) {
        errno = ENOMEM;
        return ENOMEM;
    }

    sigfillset(&newmask);
    pthread_sigmask(SIG_BLOCK, &newmask, &oldmask);

    pid = forkpty(master, NULL, NULL, (struct winsize *)winp);

    if (pid == 0)
        minipty_reset_child_signals();

    pthread_sigmask(SIG_SETMASK, &oldmask, NULL);

    if (pid < 0) {
        int err = errno > 0 ? errno : EINVAL;
        if (envp == NULL)
            free(child_envp);
        return err;
    }

    if (pid == 0) {
        minipty_prepare_fork_child();
        if (cwd != NULL && cwd[0] != '\0' && chdir(cwd) != 0)
            _exit(126);
        minipty_execvpe(file, argv, child_envp);
        _exit(127);
    }

    if (envp == NULL)
        free(child_envp);

    *pid_out = pid;
    return 0;
}

#endif /* !__APPLE__ */

#if defined(__APPLE__)
static int minipty_spawn_err_is_transient(int err)
{
    return err == EAGAIN || err == ENOMEM || err == ENXIO;
}

static int minipty_resolve_helper_path(char *out, size_t out_len)
{
    Dl_info info;

    if (dladdr((void *)&minipty_fork_pty_exec, &info) == 0 || info.dli_fname == NULL)
        return -1;

    {
        char dir_buf[PATH_MAX];
        char *dir;
        int written;

        if (strlen(info.dli_fname) >= sizeof(dir_buf))
            return -1;

        memcpy(dir_buf, info.dli_fname, strlen(info.dli_fname) + 1);
        dir = dirname(dir_buf);
        written = snprintf(out, out_len, "%s/%s", dir, MINIPTY_SPAWN_HELPER_NAME);
        if (written < 0 || (size_t)written >= out_len)
            return -1;
    }

    if (access(out, X_OK) != 0)
        return -1;

    return 0;
}

static void minipty_free_spawn_env(char **spawn_envp, char **owned_inherited)
{
    char *cwd_entry = NULL;

    if (spawn_envp != NULL) {
        for (size_t i = 0; spawn_envp[i] != NULL; i++) {
            const char *entry = spawn_envp[i];
            if (strncmp(entry, MINIPTY_CWD_KEY "=", strlen(MINIPTY_CWD_KEY) + 1) == 0) {
                cwd_entry = spawn_envp[i];
                break;
            }
        }
    }

    free(cwd_entry);
    free(spawn_envp);
    free(owned_inherited);
}

static int minipty_spawn_darwin_once(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    char *const *argv,
    char *const *envp,
    pid_t *pid_out)
{
    char helper_path[PATH_MAX];
    char slave_name[128];
    char **spawn_envp = NULL;
    char **owned_inherited = NULL;
    char **helper_argv = NULL;
    size_t argc = 0;
    int low_fds[3] = {-1, -1, -1};
    size_t low_fd_opened = 0;
    int slave = -1;
    int err = 0;
    int spawn_err = 0;
    int actions_initialized = 0;
    int attrs_initialized = 0;
    int success = 0;
    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attrs;
    sigset_t signal_set;

    *master = -1;

    for (size_t i = 0; i < 3; i++) {
        int fd = posix_openpt(O_RDWR | O_CLOEXEC);
        if (fd < 0) {
            err = errno > 0 ? errno : EINVAL;
            goto done;
        }

        /* Reserve only vacant stdio slots (0/1/2). If fd >= 3, nothing to reserve. */
        if (fd >= (STDERR_FILENO + 1)) {
            close(fd);
            break;
        }

        low_fds[i] = fd;
        low_fd_opened = i + 1;
        if (fd >= STDERR_FILENO)
            break;
    }

    if (minipty_resolve_helper_path(helper_path, sizeof(helper_path)) != 0) {
        err = ENOENT;
        goto done;
    }

    *master = posix_openpt(O_RDWR | O_CLOEXEC);
    if (*master < 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    if (grantpt(*master) != 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    if (unlockpt(*master) != 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    if (ioctl(*master, TIOCPTYGNAME, slave_name) != 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    slave = open(slave_name, O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (slave < 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    if (winp != NULL && ioctl(slave, TIOCSWINSZ, winp) != 0) {
        err = errno > 0 ? errno : EINVAL;
        goto done;
    }

    if (minipty_envp_append_cwd(envp, cwd, &spawn_envp, &owned_inherited) != 0) {
        err = errno > 0 ? errno : ENOMEM;
        goto done;
    }

    if (spawn_envp == NULL && envp == NULL) {
        spawn_envp = minipty_envp_for_child(NULL);
        if (spawn_envp == NULL) {
            err = ENOMEM;
            goto done;
        }
    }

    while (argv[argc] != NULL)
        argc++;

    helper_argv = malloc((argc + 2) * sizeof(char *));
    if (helper_argv == NULL) {
        err = ENOMEM;
        goto done;
    }

    helper_argv[0] = helper_path;
    for (size_t i = 0; i < argc; i++)
        helper_argv[i + 1] = argv[i];
    helper_argv[argc + 1] = NULL;

    spawn_err = posix_spawn_file_actions_init(&actions);
    if (spawn_err != 0) {
        err = spawn_err;
        goto done;
    }
    actions_initialized = 1;

    spawn_err = posix_spawnattr_init(&attrs);
    if (spawn_err != 0) {
        err = spawn_err;
        goto done;
    }
    attrs_initialized = 1;

    spawn_err = posix_spawn_file_actions_adddup2(&actions, slave, STDIN_FILENO);
    if (spawn_err != 0)
        goto spawn_setup_err;
    spawn_err = posix_spawn_file_actions_adddup2(&actions, slave, STDOUT_FILENO);
    if (spawn_err != 0)
        goto spawn_setup_err;
    spawn_err = posix_spawn_file_actions_adddup2(&actions, slave, STDERR_FILENO);
    if (spawn_err != 0)
        goto spawn_setup_err;
    spawn_err = posix_spawn_file_actions_addclose(&actions, slave);
    if (spawn_err != 0)
        goto spawn_setup_err;
    spawn_err = posix_spawn_file_actions_addclose(&actions, *master);
    if (spawn_err != 0)
        goto spawn_setup_err;

    spawn_err = posix_spawnattr_setflags(
        &attrs,
        POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSID);
    if (spawn_err != 0)
        goto spawn_setup_err;

    sigfillset(&signal_set);
    sigdelset(&signal_set, SIGKILL);
    sigdelset(&signal_set, SIGSTOP);
    spawn_err = posix_spawnattr_setsigdefault(&attrs, &signal_set);
    if (spawn_err != 0)
        goto spawn_setup_err;
    sigemptyset(&signal_set);
    spawn_err = posix_spawnattr_setsigmask(&attrs, &signal_set);
    if (spawn_err != 0)
        goto spawn_setup_err;

    do {
        spawn_err = posix_spawn(
            pid_out,
            helper_argv[0],
            &actions,
            &attrs,
            helper_argv,
            spawn_envp != NULL ? spawn_envp : (char **)envp);
    } while (spawn_err == EINTR);

    if (spawn_err != 0) {
        err = spawn_err;
        goto done;
    }

    success = 1;
    goto done;

spawn_setup_err:
    err = spawn_err > 0 ? spawn_err : EINVAL;
    goto done;

done:
    if (actions_initialized)
        posix_spawn_file_actions_destroy(&actions);
    if (attrs_initialized)
        posix_spawnattr_destroy(&attrs);
    if (slave >= 0)
        close(slave);
    for (size_t i = 0; i < low_fd_opened; i++) {
        if (low_fds[i] >= 0)
            close(low_fds[i]);
    }

    free(helper_argv);
    minipty_free_spawn_env(spawn_envp, owned_inherited);

    if (!success && *master >= 0) {
        close(*master);
        *master = -1;
    }

    if (err != 0)
        return err;

    return 0;
}

static int spawn_pty_child_darwin(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    const char *file,
    char *const *argv,
    char *const *envp,
    pid_t *pid_out)
{
    int err = 0;

    *master = -1;

    (void)file;

    for (int attempt = 0; attempt < MINIPTY_SPAWN_RETRY_MAX; attempt++) {
        err = minipty_spawn_darwin_once(master, winp, cwd, argv, envp, pid_out);
        if (err == 0)
            return 0;
        if (!minipty_spawn_err_is_transient(err))
            return err;
        if (*master >= 0) {
            close(*master);
            *master = -1;
        }
        usleep(MINIPTY_SPAWN_RETRY_BASE_US * (unsigned int)(attempt + 1));
    }

    return err > 0 ? err : EINVAL;
}
#endif

static int spawn_pty_child(
    int *master,
    const struct winsize *winp,
    const char *cwd,
    const char *file,
    char *const *argv,
    char *const *envp,
    pid_t *pid_out)
{
#if defined(__APPLE__)
    return spawn_pty_child_darwin(master, winp, cwd, file, argv, envp, pid_out);
#else
    return spawn_pty_child_forkpty(master, winp, cwd, file, argv, envp, pid_out);
#endif
}

int minipty_fork_pty_exec(
    int *master,
    const struct winsize *winp,
    const char *working_directory,
    const char *file,
    char *const *argv,
    char *const *envp,
    int *pid_out)
{
    pid_t pid = -1;
    int err;

    err = spawn_pty_child(master, winp, working_directory, file, argv, envp, &pid);
    if (err != 0)
        return err;

    *pid_out = (int)pid;
    return 0;
}

int minipty_set_winsize(int master, unsigned short rows, unsigned short cols)
{
    struct winsize ws = {
        .ws_row = rows,
        .ws_col = cols,
        .ws_xpixel = 0,
        .ws_ypixel = 0,
    };

    if (ioctl(master, TIOCSWINSZ, &ws) != 0)
        return -1;

    return 0;
}

int minipty_peek_readable_bytes(int fd, int *bytes_available)
{
    int nbytes = 0;

    if (bytes_available == NULL)
        return -1;

    if (ioctl(fd, FIONREAD, &nbytes) != 0)
        return -1;

    if (nbytes < 0)
        nbytes = 0;

    *bytes_available = nbytes;
    return 0;
}

int minipty_try_read(int fd, void *buf, unsigned int count, int *bytes_read, int *is_eof)
{
    int flags;
    ssize_t n;
    int saved_errno;

    if (bytes_read == NULL || is_eof == NULL || buf == NULL)
        return -1;

    flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0)
        return -1;

    if (fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0)
        return -1;

    do {
        n = read(fd, buf, count);
    } while (n < 0 && errno == EINTR);

    saved_errno = errno;
    fcntl(fd, F_SETFL, flags);

    if (n < 0) {
        if (saved_errno == EAGAIN || saved_errno == EWOULDBLOCK) {
            *bytes_read = 0;
            *is_eof = 0;
            return 0;
        }

        if (saved_errno == EIO) {
            *bytes_read = 0;
            *is_eof = 1;
            return 0;
        }

        errno = saved_errno;
        return -1;
    }

    if (n == 0) {
        *bytes_read = 0;
        *is_eof = 1;
        return 0;
    }

    *bytes_read = (int)n;
    *is_eof = 0;
    return 0;
}
