#ifndef MINIPTY_UNIX_INTERNAL_H
#define MINIPTY_UNIX_INTERNAL_H

#include <stddef.h>

#define MINIPTY_CWD_KEY "MINIPTY_CWD"
#define MINIPTY_SPAWN_HELPER_NAME "minipty_spawn_helper"
#define MINIPTY_SPAWN_RETRY_MAX 4
#define MINIPTY_SPAWN_RETRY_BASE_US 25000

struct winsize;

void minipty_execvpe(const char *file, char *const *argv, char *const *envp);

const char *minipty_env_get_cwd(char *const *envp);
char **minipty_envp_for_child(char *const *envp);
char **minipty_envp_strip_internal(char *const *envp);
int minipty_envp_append_cwd(char *const *envp, const char *cwd, char ***out_envp, char ***owned_inherited);

int minipty_fork_pty_exec(
    int *master,
    const struct winsize *winp,
    const char *working_directory,
    const char *file,
    char *const *argv,
    char *const *envp,
    int *pid_out);

int minipty_set_winsize(int master, unsigned short rows, unsigned short cols);
int minipty_peek_readable_bytes(int fd, int *bytes_available);
int minipty_peek_pending_output_bytes(int fd, int *bytes_pending);
int minipty_try_read(int fd, void *buf, unsigned int count, int *bytes_read, int *is_eof);

#endif
