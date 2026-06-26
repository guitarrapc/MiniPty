#include "minipty_unix_internal.h"

#include <errno.h>
#include <fcntl.h>
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
        const char *slave = ttyname(STDIN_FILENO);
        if (slave != NULL) {
            int fd = open(slave, O_RDWR);
            if (fd >= 0)
                close(fd);
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
