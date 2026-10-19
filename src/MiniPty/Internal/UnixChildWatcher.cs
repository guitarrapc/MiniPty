using System.Runtime.InteropServices;

namespace MiniPty.Internal;

internal static partial class UnixPtyBackend
{
    /// <summary>
    /// Process-wide observer of MiniPty child exits: one dedicated background thread for every Unix
    /// session, so observing exit adds no per-session thread and never occupies a thread-pool worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The native shim reports exits through pidfd + epoll (Linux 5.3+) or <c>kqueue</c>
    /// <c>EVFILT_PROC</c> (macOS, FreeBSD). A child the shim cannot register — no pidfd support, a
    /// seccomp filter, or a child that exited before <c>kqueue</c> registration — joins a polling set
    /// that the same thread checks with <c>waitpid(WNOHANG)</c> every <see cref="PollIntervalMs"/>.
    /// When the shim is unavailable altogether, every child is polled.
    /// </para>
    /// <para>
    /// This thread is the only code that reaps MiniPty children. A single reaper reads each wait status
    /// exactly once, and reaping a disposed session's child prevents zombies without blocking the
    /// thread that disposed it.
    /// </para>
    /// <para>
    /// It also sends the kill that follows a disposal's hang-up. The deadline joins this thread's wait timeout
    /// rather than getting a timer of its own, so disposal allocates no timer. When the process exits normally,
    /// hung-up children are killed at once, and so is a child disposed after that, because the process may end
    /// before any deadline passes. A signal that ends a process by default (<c>SIGTERM</c>, <c>SIGINT</c>,
    /// <c>SIGHUP</c>, <c>SIGQUIT</c>) runs no exit handler, so it kills the hung-up children too.
    /// </para>
    /// </remarks>
    private static partial class ChildWatcher
    {
        private const int PollIntervalMs = 10;
        private const int WaitCapacity = 16;

        // A plain monitor rather than System.Threading.Lock: polling-only mode sleeps in Monitor.Wait on it.
        private static readonly object s_lock = new();
        private static readonly Dictionary<int, UnixPtyBackendInstance> s_children = [];
        private static readonly List<int> s_polled = [];
        // Children hung up by disposal and not yet reaped, each killed at its HangupKillDeadline.
        private static readonly List<UnixPtyBackendInstance> s_hangups = [];
        private static bool s_started;
        private static bool s_nativeWatch;
        private static bool s_exiting;
        // Held for the life of the process: disposing a registration removes its handler.
        private static PosixSignalRegistration[]? s_terminationSignals;

        /// <summary>Starts watching <paramref name="child"/>; its exit task completes once the child is reaped.</summary>
        internal static void Register(UnixPtyBackendInstance child)
        {
            lock (s_lock)
            {
                EnsureStarted();
                // A registered pid can only be handed out again once its child was reaped. MiniPty had not
                // reaped it, so something outside MiniPty did and the pid was reused before this thread
                // noticed; complete the stale session now, or its exit would never be reported.
                if (s_children.TryGetValue(child.ProcessId, out var stale) && stale != child)
                {
                    stale.OnReapedElsewhere();
                    // Its polling entry would otherwise outlive the new child and keep this thread polling, and its
                    // hang-up entry would keep waking this thread for a kill that its exit already cancels.
                    s_polled.Remove(child.ProcessId);
                    s_hangups.Remove(stale);
                }

                s_children[child.ProcessId] = child;
                if (!s_nativeWatch || minipty_child_watch_add(child.ProcessId) != 0)
                {
                    s_polled.Add(child.ProcessId);
                    WakeLocked();
                }
            }
        }

        /// <summary>
        /// Kills <paramref name="child"/> at its <see cref="UnixPtyBackendInstance.HangupKillDeadline"/> unless it is
        /// reaped first.
        /// </summary>
        internal static void ScheduleHangupKill(UnixPtyBackendInstance child)
        {
            lock (s_lock)
            {
                // A child reaped after the hang-up was sent has nothing left to kill.
                if (!s_children.TryGetValue(child.ProcessId, out var registered) || registered != child)
                    return;

                // The process is exiting and may end before the deadline. A disposal gets here from a host exit handler
                // subscribed after the first Pty.Start, which runs after this class's, or from another thread during exit.
                if (s_exiting)
                {
                    child.KillAfterHangup();
                    return;
                }

                s_hangups.Add(child);
                // The new deadline may come before the timeout the thread is waiting with.
                WakeLocked();
            }
        }

        /// <summary>
        /// Kills every hung-up child now, and makes later disposals kill at once. A deadline that has not passed when
        /// the process exits would otherwise never be acted on, and a child that ignores the hang-up would outlive
        /// its host.
        /// </summary>
        private static void OnProcessExit()
        {
            lock (s_lock)
            {
                s_exiting = true;
                KillHungUpChildrenLocked();
            }
        }

        /// <summary>
        /// Kills every hung-up child when a signal that ends a process by default arrives. A host ended by such a
        /// signal raises no <see cref="AppDomain.ProcessExit"/>, so without this a child it disposed less than a grace
        /// period earlier, and that ignores the hang-up, would outlive it.
        /// </summary>
        /// <remarks>
        /// The handler never cancels the signal, so the host ends exactly as it would without it, with the same exit
        /// status (128 + signal). It also does not mark the process as exiting: another handler may cancel the signal,
        /// such as a daemon that reloads on <c>SIGHUP</c> or a console app that handles Ctrl+C, and the host then keeps
        /// running, with later disposals keeping their grace period. Only the children already hung up lose the rest of
        /// theirs.
        /// </remarks>
        private static void OnTerminationSignal(PosixSignalContext context)
        {
            lock (s_lock)
                KillHungUpChildrenLocked();
        }

        private static void KillHungUpChildrenLocked()
        {
            // Same lock order as the reap: this lock, then the child's pid lock.
            foreach (var child in s_hangups)
                child.KillAfterHangup();

            s_hangups.Clear();
        }

        private static void EnsureStarted()
        {
            if (s_started)
                return;

            s_nativeWatch = minipty_child_watch_init() == 0;
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "MiniPty child watcher",
            };
            thread.Start();
            // This thread stops with the process, so the exit itself must send the pending kills.
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => OnProcessExit();
            // The same for a signal that ends the process by default, which runs no exit handler.
            s_terminationSignals = new PosixSignalRegistration[4];
            s_terminationSignals[0] = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminationSignal);
            s_terminationSignals[1] = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnTerminationSignal);
            s_terminationSignals[2] = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnTerminationSignal);
            s_terminationSignals[3] = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnTerminationSignal);
            s_started = true;
        }

        private static void WakeLocked()
        {
            if (s_nativeWatch)
                _ = minipty_child_watch_wake();
            else
                Monitor.PulseAll(s_lock);
        }

        private static void Run()
        {
            Span<int> signaled = stackalloc int[WaitCapacity];
            List<int> polledSnapshot = [];
            while (true)
            {
                var count = s_nativeWatch ? WaitNative(signaled) : WaitManaged();
                for (var i = 0; i < count; i++)
                    Reap(signaled[i]);

                PollFallbackChildren(polledSnapshot);
                KillExpiredHangups();
            }
        }

        /// <summary>
        /// The longest the thread may wait: forever, the polling interval while any child is polled, or until the
        /// earliest hang-up deadline. Caller holds <see cref="s_lock"/>.
        /// </summary>
        private static int WaitTimeoutLocked()
        {
            var timeout = s_polled.Count > 0 ? PollIntervalMs : Timeout.Infinite;
            if (s_hangups.Count == 0)
                return timeout;

            var now = Environment.TickCount64;
            foreach (var child in s_hangups)
            {
                var remaining = (int)Math.Clamp(child.HangupKillDeadline - now, 0, int.MaxValue);
                if (timeout == Timeout.Infinite || remaining < timeout)
                    timeout = remaining;
            }

            return timeout;
        }

        private static void KillExpiredHangups()
        {
            lock (s_lock)
            {
                if (s_hangups.Count == 0)
                    return;

                var now = Environment.TickCount64;
                for (var i = s_hangups.Count - 1; i >= 0; i--)
                {
                    var child = s_hangups[i];
                    if (child.HangupKillDeadline > now)
                        continue;

                    child.KillAfterHangup();
                    s_hangups.RemoveAt(i);
                }
            }
        }

        /// <summary>Blocks in the native watcher until a child exits, the wait timeout elapses, or a wake-up.</summary>
        private static unsafe int WaitNative(Span<int> signaled)
        {
            int timeout;
            lock (s_lock)
                timeout = WaitTimeoutLocked();

            int count;
            fixed (int* pids = signaled)
                count = minipty_child_watch_wait(pids, signaled.Length, timeout);

            if (count >= 0)
                return count;

            // The native wait itself failed: degrade to polling every remaining child.
            lock (s_lock)
            {
                s_nativeWatch = false;
                foreach (var pid in s_children.Keys)
                {
                    if (!s_polled.Contains(pid))
                        s_polled.Add(pid);
                }
            }

            return 0;
        }

        /// <summary>Polling-only mode: sleeps until a registration, a hang-up deadline, or the next polling interval.</summary>
        private static int WaitManaged()
        {
            lock (s_lock)
                Monitor.Wait(s_lock, WaitTimeoutLocked());

            return 0;
        }

        private static void Reap(int pid)
        {
            lock (s_lock)
            {
                if (!s_children.TryGetValue(pid, out var child))
                    return;

                if (child.TryReapExitedChild())
                {
                    s_children.Remove(pid);
                    s_polled.Remove(pid);
                    s_hangups.Remove(child);
                }
                else if (!s_polled.Contains(pid))
                {
                    // Reported but not yet reapable: keep checking until waitpid returns it.
                    s_polled.Add(pid);
                }
            }
        }

        private static void PollFallbackChildren(List<int> snapshot)
        {
            lock (s_lock)
            {
                if (s_polled.Count == 0)
                    return;

                snapshot.Clear();
                snapshot.AddRange(s_polled);
            }

            foreach (var pid in snapshot)
                Reap(pid);
        }

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_child_watch_init();

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_child_watch_add(int pid);

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_child_watch_wake();

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static unsafe partial int minipty_child_watch_wait(int* pids, int capacity, int timeoutMs);
    }
}
