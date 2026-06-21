#include <errno.h>
#include <fcntl.h>
#include <stdlib.h>
#include <sys/ioctl.h>
#include <unistd.h>

/*
 * Fork and run the PTY child setup in native code only.
 * The child must not return to managed code after fork() in a multithreaded CLR process.
 */
int minipty_fork_child_exec(
    int master,
    int slave,
    unsigned long tioc_setctty,
    const char *working_directory,
    const char *file,
    char *const *argv)
{
    pid_t pid = fork();
    if (pid < 0)
        return -1;

    if (pid == 0)
    {
        if (close(master) != 0)
            _exit(127);
        if (setsid() < 0)
            _exit(127);
        if (ioctl(slave, tioc_setctty, 0) < 0)
            _exit(127);
        if (dup2(slave, STDIN_FILENO) < 0)
            _exit(127);
        if (dup2(slave, STDOUT_FILENO) < 0)
            _exit(127);
        if (dup2(slave, STDERR_FILENO) < 0)
            _exit(127);
        if (slave > STDERR_FILENO && close(slave) != 0)
            _exit(127);
        if (working_directory != NULL && working_directory[0] != '\0' && chdir(working_directory) < 0)
            _exit(127);
        execvp(file, argv);
        _exit(127);
    }

    return (int)pid;
}
