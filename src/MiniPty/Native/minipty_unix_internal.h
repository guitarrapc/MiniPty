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

int minipty_set_winsize(
    int master,
    unsigned short rows,
    unsigned short cols,
    unsigned short pixel_width,
    unsigned short pixel_height);
int minipty_get_active_process_name(int master, char *buffer, int buffer_length);

int minipty_child_watch_init(void);
int minipty_child_watch_add(int pid);
int minipty_child_watch_wake(void);
int minipty_child_watch_wait(int *pids, int capacity, int timeout_ms);

int minipty_wake_open(int *read_fd, int *write_fd);
int minipty_wake_signal(int write_fd);
void minipty_wake_drain(int read_fd);
void minipty_wake_close(int read_fd, int write_fd);
int minipty_set_nonblocking(int fd);
int minipty_pty_wait(int master, int events, int wake_fd, int timeout_ms);

#endif
