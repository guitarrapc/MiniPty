#include "minipty_unix_internal.h"

#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#ifndef PATH_MAX
#define PATH_MAX 4096
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
    static const char *shells[] = { "/bin/sh", "/usr/bin/sh" };

    execve(path, argv, envp);

    if (errno != ENOEXEC && errno != EACCES
        && !(errno == ENOENT && access(path, F_OK) == 0))
        return;

    while (argv[argc] != NULL)
        argc++;

    char *shell_argv[argc + 2];
    shell_argv[0] = (char *)"sh";
    shell_argv[1] = (char *)path;
    for (size_t i = 1; i < argc; i++)
        shell_argv[i + 1] = argv[i];
    shell_argv[argc + 1] = NULL;

    for (size_t i = 0; i < sizeof(shells) / sizeof(shells[0]); i++) {
        execve(shells[i], shell_argv, envp);
        if (errno != ENOENT && errno != ENOTDIR)
            break;
    }
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

void minipty_execvpe(const char *file, char *const *argv, char *const *envp)
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

const char *minipty_env_get_cwd(char *const *envp)
{
    return minipty_get_env_value(envp, MINIPTY_CWD_KEY);
}

char **minipty_envp_for_child(char *const *envp)
{
    size_t count = 0;

    if (envp == NULL)
        return minipty_build_inherited_envp();

    while (envp[count] != NULL)
        count++;

    char **copy = malloc((count + 1) * sizeof(char *));
    if (copy == NULL)
        return NULL;

    for (size_t i = 0; i <= count; i++)
        copy[i] = envp[i];

    return copy;
}

char **minipty_envp_strip_internal(char *const *envp)
{
    size_t count = 0;
    size_t kept = 0;

    if (envp == NULL)
        return NULL;

    while (envp[count] != NULL)
        count++;

    for (size_t i = 0; i < count; i++) {
        if (!minipty_has_env_name(envp[i], MINIPTY_CWD_KEY))
            kept++;
    }

    char **out = malloc((kept + 1) * sizeof(char *));
    if (out == NULL)
        return NULL;

    size_t index = 0;
    for (size_t i = 0; i < count; i++) {
        if (!minipty_has_env_name(envp[i], MINIPTY_CWD_KEY))
            out[index++] = envp[i];
    }

    out[index] = NULL;
    return out;
}

int minipty_envp_append_cwd(char *const *envp, const char *cwd, char ***out_envp, char ***owned_inherited)
{
    char cwd_entry[PATH_MAX + sizeof(MINIPTY_CWD_KEY) + 2];
    char *cwd_copy;
    size_t count = 0;
    char **base;
    char **result;
    char **inherited = NULL;

    *out_envp = NULL;
    if (owned_inherited != NULL)
        *owned_inherited = NULL;

    if (cwd == NULL || cwd[0] == '\0')
        return 0;

    if (snprintf(cwd_entry, sizeof(cwd_entry), "%s=%s", MINIPTY_CWD_KEY, cwd) >= (int)sizeof(cwd_entry)) {
        errno = ENAMETOOLONG;
        return -1;
    }

    cwd_copy = strdup(cwd_entry);
    if (cwd_copy == NULL)
        return -1;

    if (envp == NULL) {
        inherited = minipty_build_inherited_envp();
        if (inherited == NULL) {
            free(cwd_copy);
            return -1;
        }
        base = inherited;
    } else {
        base = (char **)envp;
    }

    while (base[count] != NULL)
        count++;

    result = malloc((count + 2) * sizeof(char *));
    if (result == NULL) {
        free(cwd_copy);
        free(inherited);
        return -1;
    }

    for (size_t i = 0; i < count; i++)
        result[i] = base[i];
    result[count] = cwd_copy;
    result[count + 1] = NULL;

    *out_envp = result;
    if (owned_inherited != NULL)
        *owned_inherited = inherited;
    return 0;
}
