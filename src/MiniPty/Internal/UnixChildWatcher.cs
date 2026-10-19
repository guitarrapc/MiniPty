using System.Runtime.InteropServices;

namespace MiniPty.Internal;

internal static partial class UnixPtyBackend
{
    /// <summary>
    /// Observer of the exits of every child this load of MiniPty starts: one dedicated background thread for every
    /// Unix session, so observing exit adds no per-session thread and never occupies a thread-pool worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The native shim reports exits through pidfd + epoll (Linux 5.3+) or <c>kqueue</c>
    /// <c>EVFILT_PROC</c> (macOS, FreeBSD). A child the shim cannot register joins a polling set that the same
    /// thread checks every <see cref="PollIntervalMs"/>. On Linux that is a child the spawn got no pidfd for, because
    /// the kernel gave none, a seccomp filter blocked it, or the pidfd named no child. With <c>kqueue</c> it is a child
    /// that exited before its registration. When the watcher's descriptors could not be opened, every child is polled.
    /// </para>
    /// <para>
    /// The epoll or kqueue and its wake descriptors belong to this class, not to the shim. A second
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> that loads MiniPty gets its own watcher but shares the
    /// shim, and a watcher kept in the shim handed one load's exits to the other load's thread.
    /// </para>
    /// <para>
    /// This thread is the only code that reaps a session's child. A single reaper reads each wait status
    /// exactly once, and reaping a disposed session's child prevents zombies without blocking the
    /// thread that disposed it. On Linux it also closes each reaped child's pidfd, which the session signals through.
    /// </para>
    /// <para>
    /// It also sends the kill that follows a disposal's hang-up. The deadline joins this thread's wait timeout
    /// rather than getting a timer of its own, so disposal allocates no timer. When the process exits normally,
    /// hung-up children are killed at once, and so is a child disposed after that, because the process may end
    /// before any deadline passes.
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
        // This watcher's epoll or kqueue and its wake pipe, or -1 when they could not be opened. macOS wakes through an
        // event on the kqueue and has no pipe.
        private static int s_watchFd = -1;
        private static int s_wakeRead = -1;
        private static int s_wakeWrite = -1;

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
                    ReleaseLocked(stale);
                    // Its polling entry would otherwise outlive the new child and keep this thread polling, and its
                    // hang-up entry would keep waking this thread for a kill that its exit already cancels.
                    s_polled.Remove(child.ProcessId);
                    s_hangups.Remove(stale);
                }

                s_children[child.ProcessId] = child;
                try
                {
                    if (!s_nativeWatch || minipty_child_watch_add(s_watchFd, child.Pidfd, child.ProcessId) != 0)
                    {
                        s_polled.Add(child.ProcessId);
                        WakeLocked();
                    }
                }
                catch
                {
                    // The failed start closes the child's pidfd, so this class must not keep a child whose pidfd
                    // number another descriptor may get next.
                    s_children.Remove(child.ProcessId);
                    throw;
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
                // Same lock order as the reap: this lock, then the child's pid lock.
                foreach (var child in s_hangups)
                    child.KillAfterHangup();

                s_hangups.Clear();
            }
        }

        private static void EnsureStarted()
        {
            if (s_started)
                return;

            // The descriptors are opened only once. A thread start that fails below leaves them open for the next
            // registration, and opening a second set then would leak the first.
            if (s_watchFd < 0 && minipty_child_watch_open(out var watchFd, out var wakeRead, out var wakeWrite) == 0)
            {
                s_watchFd = watchFd;
                s_wakeRead = wakeRead;
                s_wakeWrite = wakeWrite;
            }

            s_nativeWatch = s_watchFd >= 0;

            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "MiniPty child watcher",
            };
            thread.Start();
            // This thread stops with the process, so the exit itself must send the pending kills.
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => OnProcessExit();
            s_started = true;
        }

        private static void WakeLocked()
        {
            if (s_nativeWatch)
                _ = minipty_child_watch_wake(s_watchFd, s_wakeWrite);
            else
                Monitor.PulseAll(s_lock);
        }

        /// <summary>
        /// Closes a reaped child's pidfd. Its registration is removed first: a child forked concurrently holds a
        /// duplicate of the pidfd until it execs, and epoll drops a registration only when every duplicate is closed.
        /// Caller holds <see cref="s_lock"/>.
        /// </summary>
        private static void ReleaseLocked(UnixPtyBackendInstance child)
        {
            if (child.Pidfd < 0)
                return;

            minipty_child_watch_remove(s_watchFd, child.Pidfd);
            child.ClosePidfd();
        }

        private static void Run()
        {
            Span<int> signaled = stackalloc int[WaitCapacity];
            Span<int> signaledPidfds = stackalloc int[WaitCapacity];
            List<int> polledSnapshot = [];
            while (true)
            {
                var count = s_nativeWatch ? WaitNative(signaled, signaledPidfds) : WaitManaged();
                for (var i = 0; i < count; i++)
                    Reap(signaled[i], signaledPidfds[i]);

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
        private static unsafe int WaitNative(Span<int> signaled, Span<int> signaledPidfds)
        {
            int timeout;
            lock (s_lock)
                timeout = WaitTimeoutLocked();

            int count;
            fixed (int* pids = signaled)
            fixed (int* pidfds = signaledPidfds)
                count = minipty_child_watch_wait(s_watchFd, s_wakeRead, pids, pidfds, signaled.Length, timeout);

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

        /// <summary>
        /// Reaps the child with <paramref name="pid"/> if it has exited. <paramref name="pidfd"/> is the pidfd the
        /// native watcher reported for it, or -1 when the report came from polling or from kqueue.
        /// </summary>
        private static void Reap(int pid, int pidfd)
        {
            lock (s_lock)
            {
                if (!s_children.TryGetValue(pid, out var child))
                    return;

                // A report for a pidfd this class has since closed, such as a stale child's whose pid a new child
                // reused, is not about this child.
                if (pidfd >= 0 && pidfd != child.Pidfd)
                    return;

                if (child.TryReapExitedChild())
                {
                    s_children.Remove(pid);
                    s_polled.Remove(pid);
                    s_hangups.Remove(child);
                    ReleaseLocked(child);
                }
                else if (!s_polled.Contains(pid))
                {
                    // Reported but not yet reapable: keep checking until the reap succeeds.
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
                Reap(pid, -1);
        }

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_child_watch_open(out int watchFd, out int wakeRead, out int wakeWrite);

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_child_watch_add(int watchFd, int pidfd, int pid);

        [LibraryImport("minipty_unix")]
        private static partial void minipty_child_watch_remove(int watchFd, int pidfd);

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_child_watch_wake(int watchFd, int wakeWrite);

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static unsafe partial int minipty_child_watch_wait(int watchFd, int wakeRead, int* pids, int* pidfds, int capacity, int timeoutMs);
    }
}
