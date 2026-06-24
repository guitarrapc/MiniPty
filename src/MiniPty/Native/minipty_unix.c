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
#include <pty.h>
#elif defined(__APPLE__)
#include <util.h>
#elif defined(__FreeBSD__)
#include <libutil.h>
#endif

extern char **environ;

static const char minipty_default_term[] = "TERM=xterm-256color";
static const char minipty_default_path[] = "/bin:/usr/bin";

static int minipty_has_env_name(const char *entry, const char *name)
{
    size_t name_len = strlen(name);
    return strncmp(entry, name, name_len) == 0 && entry[name_len] == '=';
}

static int minipty_is_sanitized_key(const char *entry)
{
    static const char *keys[] = {
        "TMUX",
        "TMUX_PANE",
        "STY",
        "WINDOW",
        "WINDOWID",
        "TERMCAP",
        "COLUMNS",
        "LINES",
    };

    for (size_t i = 0; i < sizeof(keys) / sizeof(keys[0]); i++) {
        if (minipty_has_env_name(entry, keys[i]))
            return 1;
    }

    return 0;
}

static const char *minipty_get_env_value(char *const *envp, const char *name)
{
    size_t name_len = strlen(name);

    if (envp == NULL)
        return NULL;

    for (char *const *entry = envp; *entry != NULL; entry++) {
        if (minipty_has_env_name(*entry, name))
            return *entry + name_len + 1;
    }

    return NULL;
}

static const char *minipty_get_path(char *const *envp)
{
    const char *path = minipty_get_env_value(envp, "PATH");

    if (path != NULL)
        return path;

    return NULL;
}

static char **minipty_build_inherited_envp(void)
{
    size_t count = 0;
    size_t kept = 0;
    int has_term = 0;

    if (environ == NULL) {
        char **envp = malloc(2 * sizeof(char *));
        if (envp == NULL)
            return NULL;

        envp[0] = (char *)minipty_default_term;
        envp[1] = NULL;
        return envp;
    }

    while (environ[count] != NULL) {
        if (!minipty_is_sanitized_key(environ[count]))
            kept++;
        if (minipty_has_env_name(environ[count], "TERM"))
            has_term = 1;
        count++;
    }

    char **envp = malloc((kept + (has_term ? 0 : 1) + 1) * sizeof(char *));
    if (envp == NULL)
        return NULL;

    size_t index = 0;
    for (size_t i = 0; i < count; i++) {
        if (!minipty_is_sanitized_key(environ[i]))
            envp[index++] = environ[i];
    }

    if (!has_term)
        envp[index++] = (char *)minipty_default_term;

    envp[index] = NULL;
    return envp;
}

static void minipty_execve_compat(const char *path, char *const *argv, char *const *envp)
{
    size_t argc = 0;

    execve(path, argv, envp);

    if (errno != ENOEXEC)
        return;

    while (argv[argc] != NULL)
        argc++;

    char *shell_argv[argc + 2];
    shell_argv[0] = (char *)"sh";
    shell_argv[1] = (char *)path;
    for (size_t i = 1; i < argc; i++)
        shell_argv[i + 1] = argv[i];
    shell_argv[argc + 1] = NULL;

    execve("/bin/sh", shell_argv, envp);
}

static void minipty_execve_path(const char *dir, size_t dir_len, const char *file, char *const *argv, char *const *envp)
{
    size_t file_len = strlen(file);
    size_t needs_slash = dir_len > 0 ? 1 : 0;
    char path[PATH_MAX];

    if (dir_len == 0) {
        minipty_execve_compat(file, argv, envp);
        return;
    }

    if (dir_len + needs_slash + file_len + 1 > sizeof(path)) {
        errno = ENAMETOOLONG;
        return;
    }

    if (dir_len > 0)
        memcpy(path, dir, dir_len);
    if (needs_slash)
        path[dir_len] = '/';
    memcpy(path + dir_len + needs_slash, file, file_len);
    path[dir_len + needs_slash + file_len] = '\0';

    minipty_execve_compat(path, argv, envp);
}

static void minipty_execvpe(const char *file, char *const *argv, char *const *envp)
{
    const char *path;
    const char *cursor;
    int saved_errno = ENOENT;
    int saw_eacces = 0;

    if (file == NULL || file[0] == '\0') {
        errno = ENOENT;
        return;
    }

    if (strchr(file, '/') != NULL) {
        minipty_execve_compat(file, argv, envp);
        return;
    }

    path = minipty_get_path(envp);
    if (path == NULL)
        path = minipty_default_path;

    cursor = path;
    while (1) {
        const char *separator = strchr(cursor, ':');
        size_t dir_len = separator == NULL ? strlen(cursor) : (size_t)(separator - cursor);

        minipty_execve_path(cursor, dir_len, file, argv, envp);
        if (errno == EACCES)
            saw_eacces = 1;
        else if (errno != ENOENT && errno != ENOTDIR)
            saved_errno = errno;

        if (separator == NULL)
            break;
        cursor = separator + 1;
    }

    errno = saw_eacces ? EACCES : saved_errno;
}

static int spawn_pty_child(
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
    char **child_envp = envp == NULL ? minipty_build_inherited_envp() : (char **)envp;
    if (child_envp == NULL) {
        errno = ENOMEM;
        return -1;
    }

    sigfillset(&newmask);
    pthread_sigmask(SIG_BLOCK, &newmask, &oldmask);

    pid = forkpty(master, NULL, NULL, (struct winsize *)winp);

    pthread_sigmask(SIG_SETMASK, &oldmask, NULL);

    if (pid < 0) {
        if (envp == NULL)
            free(child_envp);
        return -1;
    }

    if (pid == 0) {
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

    if (spawn_pty_child(master, winp, working_directory, file, argv, envp, &pid) != 0)
        return -1;

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
