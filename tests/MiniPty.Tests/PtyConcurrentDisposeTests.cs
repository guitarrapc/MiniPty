using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// Disposal can race other teardown on purpose: a host disposes from its exit handler while the main thread disposes
/// too, and a cancellation callback kills a child that another thread is disposing. Library threads race it on their
/// own: the exit watcher, the EOF staging timer, and the post-exit drain. Teardown must release every handle and the
/// output ring exactly once, and nothing may use a handle after its release, because Windows hands a released handle's
/// value to the next object the process opens.
/// </summary>
/// <remarks>
/// The tests run alone. Their racing threads spin to start together and take whole cores meanwhile, which slows any
/// ConPTY-bound test beside them on a four-core hosted runner, because ConPTY drains slowly under CPU contention. Other
/// tests opening handles meanwhile would also take the released values the reuse test waits for.
/// </remarks>
[NotInParallel]
public sealed class PtyConcurrentDisposeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Concurrent <see cref="PtySession.Dispose"/> calls release the backend once. A second release closed the process
    /// handles twice on Windows and freed the exec payload twice on Unix, and it returned the output ring to the pool
    /// while the reader thread could still write into it.
    /// </summary>
    [Test]
    public async Task ConcurrentDisposeReleasesTheBackendOnce()
    {
        const int Rounds = 2000;
        for (var round = 0; round < Rounds; round++)
        {
            var backend = new CountingBackend();
            var session = new PtySession(backend);

            RunTogether(session.Dispose, session.Dispose, session.Dispose, session.Dispose);
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);

            await Assert.That(backend.DisposeCount).IsEqualTo(1);
        }
    }

    /// <summary>
    /// Disposal, kills, and exit queries on real sessions, all at once. A kill that loses the race to disposal either
    /// ends the child or reports the disposal, and exit queries never fail. With <see cref="PtyStartInfo.KillProcessTree"/>
    /// the kill uses the job handle that disposal releases, pinned the same way.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeKillAndExitQueriesCanRunAtTheSameTime(bool killProcessTree)
    {
        const int Rounds = 20;
        for (var round = 0; round < Rounds; round++)
        {
            var session = Pty.Start(BlockingChild() with { KillProcessTree = killProcessTree });
            var disposeAsync = Task.CompletedTask;

            RunTogether(
                session.Dispose,
                () => disposeAsync = session.DisposeAsync().AsTask(),
                () => KillUnlessDisposed(session),
                () => KillUnlessDisposed(session),
                () => QueryExit(session),
                () => QueryExit(session));

            await disposeAsync.WaitAsync(Timeout);
            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        }

        static void KillUnlessDisposed(PtySession session)
        {
            try
            {
                session.Kill();
            }
            catch (ObjectDisposedException)
            {
                // Disposal got there first, which is one of the two allowed outcomes.
            }
        }

        static void QueryExit(PtySession session)
        {
            for (var i = 0; i < 100; i++)
            {
                _ = session.HasExited;
                _ = session.ExitCode;
                _ = session.ExitStatus;
            }
        }
    }

    /// <summary>
    /// After disposal, exit queries on Windows use no released process handle. Windows hands a released handle's value
    /// to the next handle the process opens, so the queries used to read whatever took it: here, a duplicate handle to
    /// another child that exited with 77, which the disposed session then reported as its own exit. A session disposed
    /// while its child ran never reports the exit on Windows.
    /// </summary>
    [Test]
    public async Task ExitQueriesAfterDisposeIgnoreAReusedHandleValueOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var exited = Process.Start(new ProcessStartInfo(ComSpec, "/c exit 77")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        await exited.WaitForExitAsync().WaitAsync(Timeout);
        var exitedHandle = exited.SafeHandle.DangerousGetHandle();

        const int Rounds = 20;
        const int Duplicates = 64;
        var duplicates = new IntPtr[Duplicates];
        for (var round = 0; round < Rounds; round++)
        {
            var session = Pty.Start(BlockingChild());
            session.Dispose();

            // Taken right after the release, so one of them likely gets the value of the session's process handle.
            var count = 0;
            try
            {
                for (; count < Duplicates; count++)
                {
                    if (!DuplicateHandle(CurrentProcess, exitedHandle, CurrentProcess, out duplicates[count], 0, false, DuplicateSameAccess))
                        throw new InvalidOperationException($"DuplicateHandle failed ({Marshal.GetLastPInvokeError()})");
                }

                await Assert.That(session.HasExited).IsFalse();
                await Assert.That(session.ExitCode).IsNull();
                await Assert.That(session.ExitStatus).IsNull();
            }
            finally
            {
                for (var i = 0; i < count; i++)
                    CloseHandle(duplicates[i]);
            }

            await session.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    /// <summary>
    /// Runs every action on its own dedicated thread, released at the same instant once all of them are spinning, and
    /// rethrows the first failure. Thread-pool workers would start too far apart to race.
    /// </summary>
    private static void RunTogether(params Action[] actions)
    {
        var ready = 0;
        var go = 0;
        Exception? failure = null;
        var threads = new Thread[actions.Length];
        for (var i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            threads[i] = new Thread(() =>
            {
                Interlocked.Increment(ref ready);
                var spinner = new SpinWait();
                while (Volatile.Read(ref go) == 0)
                    spinner.SpinOnce(sleep1Threshold: -1);

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // An exception on a dedicated thread would take the test host down.
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
            })
            {
                IsBackground = true,
            };
            threads[i].Start();
        }

        // Spinning yields its time slice but never sleeps: on a two-core runner a busy spin kept the threads it waits for
        // off the cores, and 2000 rounds took minutes, while a sleep would release the threads too far apart to race.
        var waiter = new SpinWait();
        while (Volatile.Read(ref ready) < actions.Length)
            waiter.SpinOnce(sleep1Threshold: -1);

        Volatile.Write(ref go, 1);
        foreach (var thread in threads)
            thread.Join();

        if (failure is not null)
            throw new AggregateException(failure);
    }

    /// <summary>Waits on stdin, which nobody writes, so only teardown ends it.</summary>
    private static PtyStartInfo BlockingChild() =>
        OperatingSystem.IsWindows()
            ? new PtyStartInfo { FileName = ComSpec, Arguments = ["/c", "set /p DUMMY="] }
            : new PtyStartInfo { FileName = "sh", Arguments = ["-c", "IFS= read -r _"] };

    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    /// <summary>Produces no output until disposed, and counts disposals.</summary>
    private sealed class CountingBackend : PtyOutputPumpTests.FakeBackend
    {
        private readonly ManualResetEventSlim _disposed = new();
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public override int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            _disposed.Wait();
            return 0;
        }

        public override int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            endOfOutput = true;
            return 0;
        }

        public override void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            base.Dispose();
            _disposed.Set();
        }
    }

    private static readonly IntPtr CurrentProcess = new(-1);
    private const uint DuplicateSameAccess = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
