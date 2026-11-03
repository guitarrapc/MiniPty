using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace MiniPty.Tests;

/// <summary>
/// How a Unix session's child starts. Linux starts it with <c>clone(CLONE_VM | CLONE_VFORK)</c> in the host's memory
/// rather than with <c>forkpty</c>, so these pin what the child must still get: a session of its own on the session's
/// terminal, default signal handling with the calling thread's mask, a failure report of its own, and no terminal
/// descriptor reaching any other process. Tests that read <c>/proc</c> run on Linux only.
/// </summary>
public sealed class PtyUnixStartTests
{
    private const int ENOENT = 2;
    // Bit 13 - 1 of a /proc status signal mask.
    private const ulong SigpipeBit = 1UL << 12;
    // SIGRTMAX is 64 on Linux, the last of the kernel's signals, so bit 64 - 1.
    private const ulong SigrtmaxBit = 1UL << 63;
    private const int SigBlock = 0;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The child leads a new session and its own process group, and the session's terminal is its controlling
    /// terminal with that group in the foreground: what a shell needs for job control, Ctrl+C, and
    /// <see cref="PtySession.ActiveProcessName"/>. The fields come from <c>/proc/&lt;pid&gt;/stat</c> after the
    /// command name: state, ppid, pgrp, session, tty_nr, tpgid.
    /// </summary>
    [Test]
    public async Task TheChildLeadsASessionOnTheSessionsTerminal()
    {
        if (!OperatingSystem.IsLinux())
            return;

        await using var session = Pty.Start(new PtyStartInfo
        {
            FileName = "sh",
            Arguments = ["-c", "set -- $(sed 's/.*) //' /proc/$$/stat); printf 'pid=%s pgrp=%s sid=%s tty=%s tpgid=%s\\n' $$ $3 $4 $5 $6"],
        });
        var text = await PtyBaselineTests.ReadAllTextAsync(session).WaitAsync(Timeout);
        var fields = ParseFields(text);
        var pid = session.ProcessId.ToString(CultureInfo.InvariantCulture);

        await Assert.That(fields.GetValueOrDefault("pid")).IsEqualTo(pid).Because(text);
        await Assert.That(fields.GetValueOrDefault("pgrp")).IsEqualTo(pid).Because(text);
        await Assert.That(fields.GetValueOrDefault("sid")).IsEqualTo(pid).Because(text);
        await Assert.That(fields.GetValueOrDefault("tpgid")).IsEqualTo(pid).Because(text);
        await Assert.That(fields.GetValueOrDefault("tty")).IsNotEqualTo("0").Because(text);
    }

    /// <summary>
    /// The child starts with every signal at its default action, and with the signal mask of the thread that called
    /// <see cref="Pty.Start"/>. The .NET host ignores <c>SIGPIPE</c>, and a child that kept that would not end when it
    /// wrote to a closed pipe. The start blocks every signal while the child runs in the host's memory, and the child
    /// must exec with the caller's mask instead. .NET threads block nothing, and a child that dropped the caller's mask
    /// would match an empty one, so the calling thread blocks <c>SIGRTMAX</c> first. <c>cat</c> changes neither, so
    /// its status shows what the start gave it.
    /// </summary>
    [Test]
    public async Task TheChildStartsWithDefaultSignalHandlingAndTheCallersMask()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var hostIgnored = StatusMask(File.ReadAllLines("/proc/self/status"), "SigIgn");
        await Assert.That(hostIgnored & SigpipeBit).IsEqualTo(SigpipeBit).Because("the .NET host ignores SIGPIPE");

        // A thread of its own, not the test's, which goes back to the thread pool. Threads created on it meanwhile keep
        // the block after it ends, such as the child watcher on the process's first start, or a pool worker. So the
        // signal is SIGRTMAX, which no test sends and PtySignal cannot name. Pty.Start is synchronous, so nothing runs
        // between the read of the caller's mask and the start.
        ulong callerBlocked = 0;
        string[] lines = [];
        Exception? failure = null;
        var starter = new Thread(() =>
        {
            try
            {
                var set = new ulong[16];
                set[0] = SigrtmaxBit;
                var error = pthread_sigmask(SigBlock, set, null);
                if (error != 0)
                    throw new Win32Exception(error, "pthread_sigmask failed");

                callerBlocked = StatusMask(File.ReadAllLines("/proc/thread-self/status"), "SigBlk");
                using var session = Pty.Start(new PtyStartInfo { FileName = "cat", Arguments = ["/proc/self/status"] });
                lines = PtyBaselineTests.ReadAllTextAsync(session).WaitAsync(Timeout).GetAwaiter().GetResult().Split('\n');
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        starter.Start();
        starter.Join();

        await Assert.That(failure).IsNull();
        await Assert.That(callerBlocked & SigrtmaxBit).IsEqualTo(SigrtmaxBit);
        await Assert.That(StatusMask(lines, "SigIgn")).IsEqualTo(0UL);
        await Assert.That(StatusMask(lines, "SigBlk")).IsEqualTo(callerBlocked);
    }

    /// <summary>
    /// A process the host starts on another thread while a session starts gets none of the session's terminal
    /// descriptors. Both ends are opened close-on-exec before the child exists, so a process started elsewhere at any
    /// moment of the start inherits neither. Four threads start probes around every start. Each probe lists the
    /// terminals among its descriptors above stdio, which must be what a probe started before any session found.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ProcessesTheHostStartsDuringAStartGetNoTerminalDescriptor()
    {
        if (OperatingSystem.IsWindows())
            return;

        var baseline = RunProbe();
        var leaks = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var probes = 0;
        using var stop = new CancellationTokenSource();
        // Dedicated threads: pool workers blocked on the probes would starve the sessions' own continuations.
        var probers = new Thread[4];
        for (var i = 0; i < probers.Length; i++)
        {
            probers[i] = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var found = RunProbe();
                    Interlocked.Increment(ref probes);
                    if (found != baseline)
                        leaks.Enqueue(found);
                }
            })
            {
                IsBackground = true,
            };
            probers[i].Start();
        }

        try
        {
            for (var i = 0; i < 100; i++)
            {
                await using var session = Pty.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "exit 0"] });
                await session.WaitForExitAsync().WaitAsync(Timeout);
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var prober in probers)
                prober.Join();
        }

        await Assert.That(leaks).IsEmpty()
            .Because($"{leaks.Count} of {probes} probes found terminal descriptors [{string.Join("], [", leaks)}] where the first found [{baseline}]");

        // The descriptors above stdio that are terminals, in a process started with Process, as the host's other code
        // starts processes. The probe reads no input, and its output is a pipe.
        static string RunProbe()
        {
            using var probe = Process.Start(new ProcessStartInfo("sh", ["-c", "i=3; while [ $i -lt 1024 ]; do if [ -t $i ]; then printf '%s ' $i; fi; i=$((i+1)); done"])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var terminals = probe.StandardOutput.ReadToEnd();
            probe.WaitForExit();
            return terminals.Trim();
        }
    }

    /// <summary>
    /// Starts that fail at the same time each report their own failure. A Linux child reports in a record on its own
    /// start's stack, a macOS or FreeBSD child on a pipe of its own start, so no start sees another's stage or error. A
    /// missing working directory fails at <c>chdir</c> and a missing program at exec, both with <c>ENOENT</c>, which
    /// only the stage tells apart. Starts that succeed run between them.
    /// </summary>
    [Test]
    public async Task ConcurrentStartFailuresEachReportTheirOwnStage()
    {
        if (OperatingSystem.IsWindows())
            return;

        const int starts = 24;
        var outcomes = new string[starts];
        using var ready = new Barrier(starts);
        var threads = new Thread[starts];
        for (var i = 0; i < starts; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                ready.SignalAndWait();
                outcomes[index] = Outcome(index);
            })
            {
                IsBackground = true,
            };
            threads[i].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        for (var i = 0; i < starts; i++)
            await Assert.That(outcomes[i]).IsEqualTo(Expected(i));

        static string Expected(int index) => (index % 3) switch
        {
            0 => $"exec {ENOENT}",
            1 => $"chdir {ENOENT}",
            _ => "started",
        };

        static string Outcome(int index)
        {
            var startInfo = (index % 3) switch
            {
                // A fixed PATH: an inherited one can hold an entry the search may not read, which reports EACCES.
                0 => new PtyStartInfo
                {
                    FileName = "minipty-start-no-such-program",
                    Environment = new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin" },
                },
                1 => new PtyStartInfo
                {
                    FileName = "sh",
                    Arguments = ["-c", "exit 0"],
                    WorkingDirectory = Path.Combine(Path.GetTempPath(), "minipty-start-missing-" + Guid.NewGuid().ToString("N")),
                },
                _ => new PtyStartInfo { FileName = "sh", Arguments = ["-c", "exit 0"] },
            };

            try
            {
                Pty.Start(startInfo).Dispose();
                return "started";
            }
            catch (Win32Exception exception)
            {
                var stage = exception.Message.Contains("working directory", StringComparison.Ordinal) ? "chdir" : "exec";
                return $"{stage} {exception.NativeErrorCode}";
            }
        }
    }

    private static Dictionary<string, string> ParseFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
                fields[part[..equals]] = part[(equals + 1)..];
        }

        return fields;
    }

    /// <summary>Changes the calling thread's signal mask. <c>sigset_t</c> is 1,024 bits in glibc and musl.</summary>
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int pthread_sigmask(int how, ulong[] set, ulong[]? oldSet);

    /// <summary>A signal mask from a <c>/proc</c> status file: the hexadecimal value of the line named <paramref name="name"/>.</summary>
    private static ulong StatusMask(string[] lines, string name)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith(name + ":", StringComparison.Ordinal))
                return ulong.Parse(line[(name.Length + 1)..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"No {name} line in [{string.Join("|", lines)}]");
    }
}
