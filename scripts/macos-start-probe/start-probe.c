/*
 * Where a macOS session start spends its time (improvement plan I7). Times five starts of one program, each from the
 * first call until waitpid reaps the child, and alternates them round by round so that a runner whose speed drifts
 * affects all of them alike:
 *
 *   spawn        posix_spawn of the program and nothing else: the cost of creating a process.
 *   pty          the same with MiniPty's terminal and attributes: a new pseudo-terminal, its slave on the child's
 *                stdio, its size and termios set, POSIX_SPAWN_SETSID | POSIX_SPAWN_CLOEXEC_DEFAULT |
 *                POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK. The child gets no controlling terminal, which is what
 *                the helper is for.
 *   open         as pty, but the child opens the slave by name in a file action instead of inheriting the parent's
 *                descriptor: the start without the helper. It takes the controlling terminal only if Darwin creates
 *                the new session before it runs the file actions, which the check below reports.
 *   helper       as pty, but spawning minipty_spawn_helper, which opens the slave by name (taking it as the
 *                controlling terminal) and then executes the program: the native part of MiniPty's start.
 *   helper-name  as helper, but passing the program's file name alone, so the helper searches PATH for it as it does
 *                for MiniPty's "sh".
 *
 * Before timing, each terminal variant runs /bin/sh -c 'exec 3</dev/tty' once (sh -c ... for helper-name): /dev/tty
 * opens only for a process that has a controlling terminal, so its exit status shows which variants have one.
 *
 * MiniPty's whole start, from Pty.Start to the exit, is timed on the same runner by the benchmarks' --start-probe.
 * This probe leaves out what MiniPty adds around the spawn and does not depend on the helper: the start report pipe,
 * the stdio slots it reserves, and its environment handling.
 *
 * Build: cc -O2 -o start-probe scripts/macos-start-probe/start-probe.c
 * Usage: start-probe <path to minipty_spawn_helper> <rounds> <program path> [arguments...]
 * Prints the minimum, median and mean of each, in microseconds.
 */
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <sys/wait.h>
#include <termios.h>
#include <time.h>
#include <unistd.h>

#ifndef __APPLE__
#error "This probe measures the macOS start; TIOCPTYGNAME and the helper are macOS only."
#endif

extern char **environ;

enum { WarmUpRounds = 10, Variants = 5 };
enum { Spawn, Pty, Open, Helper, HelperName };

static const char *const variant_names[Variants] = { "spawn", "pty", "open", "helper", "helper-name" };

static unsigned long long now_ns(void)
{
    return clock_gettime_nsec_np(CLOCK_UPTIME_RAW);
}

static int wait_child(pid_t pid)
{
    int status;

    while (waitpid(pid, &status, 0) < 0) {
        if (errno != EINTR)
            return -1;
    }

    return WIFEXITED(status) && WEXITSTATUS(status) == 0 ? 0 : -1;
}

/* A master and its open slave, as MiniPty opens them before the spawn, and the slave's name. */
static int open_terminal(int *master, int *slave, char *name)
{
    struct winsize size = { 24, 80, 0, 0 };
    struct termios term;

    *master = posix_openpt(O_RDWR | O_CLOEXEC);
    if (*master < 0)
        return -1;

    if (grantpt(*master) != 0 || unlockpt(*master) != 0 || ioctl(*master, TIOCPTYGNAME, name) != 0)
        goto fail;

    *slave = open(name, O_RDWR | O_NOCTTY | O_CLOEXEC);
    if (*slave < 0)
        goto fail;

    if (ioctl(*slave, TIOCSWINSZ, &size) != 0 || tcgetattr(*slave, &term) != 0 || tcsetattr(*slave, TCSANOW, &term) != 0) {
        close(*slave);
        goto fail;
    }

    return 0;

fail:
    close(*master);
    return -1;
}

/*
 * One start of a variant, until the child is reaped; returns its time in nanoseconds, or 0 when the start failed or
 * the child exited with a status other than 0. argv is the program's (spawn, pty, open) or the helper's (helper,
 * helper-name).
 */
static unsigned long long start_once(int variant, char **argv)
{
    posix_spawn_file_actions_t actions;
    posix_spawnattr_t attrs;
    sigset_t signals;
    unsigned long long started;
    char name[128];
    int master = -1;
    int slave = -1;
    int err;
    int ok;
    pid_t pid;

    started = now_ns();
    if (variant == Spawn) {
        err = posix_spawn(&pid, argv[0], NULL, NULL, argv, environ);
        if (err != 0)
            return 0;

        return wait_child(pid) == 0 ? now_ns() - started : 0;
    }

    if (open_terminal(&master, &slave, name) != 0)
        return 0;

    posix_spawn_file_actions_init(&actions);
    posix_spawnattr_init(&attrs);
    if (variant == Open) {
        /* Without O_NOCTTY: a session leader with no controlling terminal takes the one it opens. */
        posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, name, O_RDWR, 0);
        posix_spawn_file_actions_adddup2(&actions, STDIN_FILENO, STDOUT_FILENO);
        posix_spawn_file_actions_adddup2(&actions, STDIN_FILENO, STDERR_FILENO);
    } else {
        posix_spawn_file_actions_adddup2(&actions, slave, STDIN_FILENO);
        posix_spawn_file_actions_adddup2(&actions, slave, STDOUT_FILENO);
        posix_spawn_file_actions_adddup2(&actions, slave, STDERR_FILENO);
        posix_spawn_file_actions_addclose(&actions, slave);
    }
    posix_spawn_file_actions_addclose(&actions, master);
    posix_spawnattr_setflags(
        &attrs, POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSID);
    sigfillset(&signals);
    sigdelset(&signals, SIGKILL);
    sigdelset(&signals, SIGSTOP);
    posix_spawnattr_setsigdefault(&attrs, &signals);
    sigemptyset(&signals);
    posix_spawnattr_setsigmask(&attrs, &signals);

    err = posix_spawn(&pid, argv[0], &actions, &attrs, argv, environ);

    posix_spawn_file_actions_destroy(&actions);
    posix_spawnattr_destroy(&attrs);
    /* MiniPty keeps the slave open until it closes the master; both close after the exit here. */
    ok = err == 0 && wait_child(pid) == 0;
    close(slave);
    close(master);
    return ok ? now_ns() - started : 0;
}

/* argv for the helper: minipty_spawn_helper <program> [arguments...]. */
static char **helper_argv_for(const char *helper, const char *program, char **arguments, int argument_count)
{
    char **argv = calloc((size_t)argument_count + 3, sizeof(char *));
    int i;

    if (argv == NULL)
        return NULL;

    argv[0] = (char *)helper;
    argv[1] = (char *)program;
    for (i = 0; i < argument_count; i++)
        argv[i + 2] = arguments[i];

    return argv;
}

static int compare_ns(const void *a, const void *b)
{
    unsigned long long x = *(const unsigned long long *)a;
    unsigned long long y = *(const unsigned long long *)b;

    return x < y ? -1 : x > y;
}

int main(int argc, char **argv)
{
    static char *check_arguments[] = { "-c", "exec 3</dev/tty" };
    static char *check_program_argv[] = { "/bin/sh", "-c", "exec 3</dev/tty", NULL };
    unsigned long long *times[Variants];
    char **run_argv[Variants];
    char **check_argv[Variants];
    const char *helper;
    const char *file_name;
    int rounds;
    int i;
    int v;

    if (argc < 4 || strchr(argv[3], '/') == NULL) {
        fprintf(stderr, "usage: %s <path to minipty_spawn_helper> <rounds> <program path> [arguments...]\n", argv[0]);
        return 2;
    }

    helper = argv[1];
    rounds = atoi(argv[2]);
    if (rounds <= 0) {
        fprintf(stderr, "rounds must be positive\n");
        return 2;
    }

    file_name = strrchr(argv[3], '/') + 1;
    run_argv[Spawn] = run_argv[Pty] = run_argv[Open] = &argv[3];
    run_argv[Helper] = helper_argv_for(helper, argv[3], &argv[4], argc - 4);
    run_argv[HelperName] = helper_argv_for(helper, file_name, &argv[4], argc - 4);
    check_argv[Spawn] = NULL;
    check_argv[Pty] = check_argv[Open] = check_program_argv;
    check_argv[Helper] = helper_argv_for(helper, "/bin/sh", check_arguments, 2);
    check_argv[HelperName] = helper_argv_for(helper, "sh", check_arguments, 2);
    if (run_argv[Helper] == NULL || run_argv[HelperName] == NULL || check_argv[Helper] == NULL || check_argv[HelperName] == NULL)
        return 1;

    for (v = 0; v < Variants; v++) {
        times[v] = calloc((size_t)rounds, sizeof(unsigned long long));
        if (times[v] == NULL)
            return 1;
    }

    printf("start-probe: controlling terminal:");
    for (v = Pty; v < Variants; v++)
        printf(" %s %s", variant_names[v], start_once(v, check_argv[v]) != 0 ? "yes" : "no");
    printf("\n");

    for (i = 0; i < WarmUpRounds; i++) {
        for (v = 0; v < Variants; v++) {
            if (start_once(v, run_argv[v]) == 0) {
                fprintf(stderr, "%s start failed (errno %d)\n", variant_names[v], errno);
                return 1;
            }
        }
    }

    for (i = 0; i < rounds; i++) {
        for (v = 0; v < Variants; v++) {
            times[v][i] = start_once(v, run_argv[v]);
            if (times[v][i] == 0) {
                fprintf(stderr, "%s start failed in round %d (errno %d)\n", variant_names[v], i + 1, errno);
                return 1;
            }
        }
    }

    printf("start-probe: %s, %d rounds after %d warm-up rounds, times in microseconds\n", argv[3], rounds, WarmUpRounds);
    for (v = 0; v < Variants; v++) {
        unsigned long long sum = 0;

        for (i = 0; i < rounds; i++)
            sum += times[v][i];

        qsort(times[v], (size_t)rounds, sizeof(unsigned long long), compare_ns);
        printf("  %-11s min %8.1f  median %8.1f  mean %8.1f\n",
            variant_names[v],
            times[v][0] / 1000.0,
            times[v][rounds / 2] / 1000.0,
            (double)sum / rounds / 1000.0);
    }

    return 0;
}
