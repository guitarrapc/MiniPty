using System.Runtime.InteropServices;

namespace MiniPty.Internal;

internal static partial class UnixPtyBackend
{
    /// <summary>
    /// Process-wide observer of MiniPty child exits: one dedicated background thread for every Unix
    /// session, so an idle session holds no thread of its own and never occupies a thread-pool worker.
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
    /// </remarks>
    private static partial class ChildWatcher
    {
        private const int PollIntervalMs = 10;
        private const int WaitCapacity = 16;

        // A plain monitor rather than System.Threading.Lock: polling-only mode sleeps in Monitor.Wait on it.
        private static readonly object s_lock = new();
        private static readonly Dictionary<int, UnixPtyBackendInstance> s_children = [];
        private static readonly List<int> s_polled = [];
        private static bool s_started;
        private static bool s_nativeWatch;

        /// <summary>Starts watching <paramref name="child"/>; its exit task completes once the child is reaped.</summary>
        internal static void Register(UnixPtyBackendInstance child)
        {
            lock (s_lock)
            {
                EnsureStarted();
                s_children[child.ProcessId] = child;
                if (!s_nativeWatch || minipty_child_watch_add(child.ProcessId) != 0)
                {
                    s_polled.Add(child.ProcessId);
                    WakeLocked();
                }
            }
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
            }
        }

        /// <summary>Blocks in the native watcher until a child exits, the polling interval elapses, or a wake-up.</summary>
        private static unsafe int WaitNative(Span<int> signaled)
        {
            int timeout;
            lock (s_lock)
                timeout = s_polled.Count > 0 ? PollIntervalMs : Timeout.Infinite;

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

        /// <summary>Polling-only mode: sleeps until a registration or the next polling interval.</summary>
        private static int WaitManaged()
        {
            lock (s_lock)
                Monitor.Wait(s_lock, s_polled.Count > 0 ? PollIntervalMs : Timeout.Infinite);

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
