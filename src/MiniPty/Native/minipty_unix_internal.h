#ifndef MINIPTY_UNIX_INTERNAL_H
#define MINIPTY_UNIX_INTERNAL_H

#include <stddef.h>
#include <stdint.h>

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
    int *pid_out,
    int *pidfd_out,
    int *slave_out);

int minipty_set_winsize(
    int master,
    unsigned short rows,
    unsigned short cols,
    unsigned short pixel_width,
    unsigned short pixel_height);
int minipty_get_active_process_name(int master, char *buffer, int buffer_length);

int minipty_child_watch_open(int *watch_fd, int *wake_read, int *wake_write);
int minipty_child_watch_add(int watch_fd, int pidfd, int pid);
void minipty_child_watch_remove(int watch_fd, int pidfd);
int minipty_child_watch_add_output(int watch_fd, int master_fd);
void minipty_child_watch_remove_output(int watch_fd, int master_fd);
int minipty_child_watch_wake(int watch_fd, int wake_write);
int minipty_watch_add(int watch_fd, int fd, uint64_t token, int interest);
int minipty_watch_modify(int watch_fd, int fd, uint64_t token, int interest);
int minipty_watch_renew(int watch_fd, int fd, uint64_t token, int interest);
void minipty_watch_remove(int watch_fd, int fd);
int minipty_watch_wait(int watch_fd, int wake_read, uint64_t *tokens, int *reports, int capacity, int timeout_ms);
int minipty_pidfd_open(int pid);
int minipty_signal_child(int pidfd, int pid, int sig);
int minipty_reap_child(int pidfd, int pid, int block, int *exit_code, int *term_signal);

int minipty_wake_open(int *read_fd, int *write_fd);
int minipty_wake_signal(int write_fd);
void minipty_wake_drain(int read_fd);
void minipty_wake_close(int read_fd, int write_fd);
int minipty_set_nonblocking(int fd);
int minipty_pty_wait(int master, int events, int wake_fd, int timeout_ms);

#endif
