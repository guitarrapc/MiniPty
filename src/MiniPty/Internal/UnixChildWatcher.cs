using System.Runtime.InteropServices;

namespace MiniPty.Internal;

internal static partial class UnixPtyBackend
{
    /// <summary>
    /// Observer of the exits of every child this load of MiniPty starts, and the I/O loop of every session: one dedicated
    /// background thread for every Unix session, so neither observing exit nor reading output adds a per-session thread,
    /// and neither occupies a thread-pool worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>I/O loop.</b> Each session's non-blocking master is registered on the same epoll (Linux) or kqueue (macOS,
    /// FreeBSD) that reports the children's exits, level-triggered, with the interest its state needs: output while its
    /// ring has room, input while some is queued (<see cref="UnixPtyBackendInstance.ServiceOnLoop"/>). kqueue keeps the
    /// two interests as two filters of the master, and the native wait joins their events into one report per master, as
    /// epoll gives. A master's token names its slot and the slot's
    /// generation, so a report that arrives after the session's registration was removed never reaches the session that
    /// took the slot next. Each pass reads every reporting session a bounded amount and publishes each read at once (no
    /// coalescing window, as on Windows: a consumer that is busy takes what arrived meanwhile as one chunk), writes its
    /// queued input, and looks at every session that asked (a consumer freed space, input was queued, a sink attached, the
    /// close or disposal began). A session's failure ends that session's output alone. Reading needs no thread-pool worker,
    /// so output keeps flowing into rings while the host's pool is starved; only the consumers' continuations wait for it.
    /// Sessions read on reader threads under the rollback switch (<see cref="PtyOutputDrivers"/>), and when the watcher
    /// has no epoll or kqueue.
    /// </para>
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
    /// rather than getting a timer of its own, so disposal allocates no timer. Until the child is reaped or killed, this
    /// thread also discards its output, because the session's reading has stopped and the master stays open for the
    /// hang-up: a session read on this loop keeps its registration and discards there, and one read on a reader thread
    /// has its master watched for output as follows. The master's output wakes the wait, and the polling interval backs that up. A macOS master buffers
    /// about 1 KiB, so discarding on the interval alone held a writing child to about 100 KiB/s. When the process exits
    /// normally, hung-up children are killed at once, and so is a child disposed after that, because the process may
    /// end before any deadline passes.
    /// </para>
    /// </remarks>
    private static partial class ChildWatcher
    {
        private const int PollIntervalMs = 10;
        private const int WaitCapacity = 16;

        // Session I/O: the masters of sessions whose output this thread reads. A master's token is its slot plus
        // the slot's generation, with the top bit set so that it never meets a child's report; a report whose generation
        // is stale (the slot was freed and taken again) is dropped.
        private const ulong MasterTokenBit = 1UL << 63;
        internal const int InterestRead = 1;
        internal const int InterestWrite = 2;
        internal const int ReportReadable = 1;
        internal const int ReportWritable = 2;
        internal const int ReportHangup = 4;
        internal const int ReportError = 8;
        private const int ReportChild = 16;

        // Guarded by s_lock: the sessions by slot, each slot's generation, and the free slots.
        private static UnixPtyBackendInstance?[] s_slots = new UnixPtyBackendInstance?[16];
        private static int[] s_slotGenerations = new int[16];
        private static int[] s_freeSlots = new int[16];
        private static int s_freeSlotCount;
        private static int s_slotsUsed;
        private static int s_mastersRegistered;
        // Sessions that asked this thread to look at them (a consumer freed space, input was queued, a sink attached, the
        // close or disposal began), linked through NextService. Guarded by s_lock.
        private static UnixPtyBackendInstance? s_serviceHead;
        private static UnixPtyBackendInstance? s_serviceTail;
        // Loop passes, for tests that check the loop does not spin.
        private static long s_passes;

        /// <summary>Passes this thread has made through its loop, for tests that check it does not spin.</summary>
        internal static long Passes => Volatile.Read(ref s_passes);

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
                // A session read on this thread already has its master registered, and discards its output there.
                if (!child.DiscardsOnLoop)
                    child.WatchOutputAfterHangup();
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

        /// <summary>
        /// Registers <paramref name="session"/>'s <paramref name="master"/> for reading on this thread. Returns
        /// <see langword="false"/> when this thread cannot read masters (no epoll or kqueue, or a 32-bit kqueue build, whose
        /// event cannot carry the token), and the session then reads on a reader thread. The caller holds a reference on the master until
        /// <see cref="DeregisterMaster"/>, so its descriptor cannot close, and its number be reused, while registered.
        /// </summary>
        internal static bool TryRegisterMaster(UnixPtyBackendInstance session, int master)
        {
            lock (s_lock)
            {
                EnsureStarted();
                if (!s_nativeWatch)
                    return false;

                var slot = TakeSlotLocked();
                var token = MasterToken(slot, s_slotGenerations[slot]);
                if (minipty_watch_add(s_watchFd, master, token, InterestRead) != 0)
                {
                    FreeSlotLocked(slot);
                    return false;
                }

                s_slots[slot] = session;
                s_mastersRegistered++;
                session.OnRegisteredOnLoop(slot, s_slotGenerations[slot]);
                return true;
            }
        }

        /// <summary>Sets what a registered master reports. On this thread only.</summary>
        internal static void SetInterest(int master, int slot, int generation, int interest) =>
            _ = minipty_watch_modify(s_watchFd, master, MasterToken(slot, generation), interest);

        /// <summary>
        /// Registers a master again with the same interest, which clears an end of file kqueue keeps reporting after the
        /// slave opened again (see <see cref="UnixPtyBackendInstance.ServiceOnLoop"/>). On this thread only.
        /// </summary>
        internal static void RenewInterest(int master, int slot, int generation, int interest) =>
            _ = minipty_watch_renew(s_watchFd, master, MasterToken(slot, generation), interest);

        /// <summary>
        /// Removes a registered master, while it is still open, and frees its slot; a report already taken for it is
        /// dropped by its generation. On this thread only.
        /// </summary>
        internal static void DeregisterMaster(int master, int slot)
        {
            minipty_watch_remove(s_watchFd, master);
            lock (s_lock)
            {
                s_slots[slot] = null;
                s_mastersRegistered--;
                FreeSlotLocked(slot);
            }
        }

        /// <summary>
        /// Has this thread look at <paramref name="session"/> on its next pass. Cheap, never blocks, from any thread; a
        /// session already waiting for a look is not queued twice.
        /// </summary>
        internal static void RequestService(UnixPtyBackendInstance session)
        {
            lock (s_lock)
            {
                if (session.ServiceQueued)
                    return;

                session.ServiceQueued = true;
                if (s_serviceTail is null)
                    s_serviceHead = session;
                else
                    s_serviceTail.NextService = session;

                s_serviceTail = session;
                WakeLocked();
            }
        }

        private static ulong MasterToken(int slot, int generation) =>
            MasterTokenBit | ((ulong)(uint)(generation & 0x7FFFFFFF) << 32) | (uint)slot;

        private static int TakeSlotLocked()
        {
            if (s_freeSlotCount > 0)
                return s_freeSlots[--s_freeSlotCount];

            if (s_slotsUsed == s_slots.Length)
            {
                Array.Resize(ref s_slots, s_slots.Length * 2);
                Array.Resize(ref s_slotGenerations, s_slotGenerations.Length * 2);
                Array.Resize(ref s_freeSlots, s_freeSlots.Length * 2);
            }

            return s_slotsUsed++;
        }

        private static void FreeSlotLocked(int slot)
        {
            // A report for the old occupant carries the old generation and is dropped.
            s_slotGenerations[slot]++;
            s_freeSlots[s_freeSlotCount++] = slot;
        }

        /// <summary>The session a master report is about, or null when its slot was freed since. Caller holds s_lock.</summary>
        private static UnixPtyBackendInstance? LookupLocked(ulong token)
        {
            var slot = (int)(uint)token;
            if ((uint)slot >= (uint)s_slotsUsed)
                return null;

            var session = s_slots[slot];
            return session is not null && MasterToken(slot, s_slotGenerations[slot]) == token ? session : null;
        }

        /// <summary>
        /// Makes this thread wake whenever <paramref name="master"/> has output. Fails when the watcher has no epoll or
        /// kqueue, which leaves that output to the polling interval. Caller holds <see cref="s_lock"/>.
        /// </summary>
        internal static bool TryWatchOutputLocked(int master) =>
            s_nativeWatch && minipty_child_watch_add_output(s_watchFd, master) == 0;

        /// <summary>
        /// Undoes <see cref="TryWatchOutputLocked"/>. Called while <paramref name="master"/> is still open, so the call
        /// can never name a descriptor that reused its number. Caller holds <see cref="s_lock"/>.
        /// </summary>
        internal static void UnwatchOutputLocked(int master) => minipty_child_watch_remove_output(s_watchFd, master);

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

        /// <summary>
        /// The loop: wait for child exits, master readiness, and service requests; reap, read and write each session that
        /// reported, look at each session that asked, and serve polled and hung-up children. A session's work is bounded per pass, so one flood does not starve the others,
        /// and a failure in it ends that session alone.
        /// </summary>
        private static void Run()
        {
            Span<ulong> tokens = stackalloc ulong[WaitCapacity];
            Span<int> reports = stackalloc int[WaitCapacity];
            List<int> polledSnapshot = [];
            while (true)
            {
                Volatile.Write(ref s_passes, s_passes + 1);
                var count = s_nativeWatch ? WaitNative(tokens, reports) : WaitManaged();
                for (var i = 0; i < count; i++)
                {
                    var token = tokens[i];
                    if ((reports[i] & ReportChild) != 0)
                    {
                        Reap((int)(uint)token, (int)(token >> 32));
                        continue;
                    }

                    UnixPtyBackendInstance? session;
                    lock (s_lock)
                        session = LookupLocked(token);

                    session?.ServiceOnLoop(reports[i]);
                }

                ServeRequests();
                PollFallbackChildren(polledSnapshot);
                ServeHangups();
            }
        }

        /// <summary>Looks at every session that asked since the last pass, including ones that ask meanwhile.</summary>
        private static void ServeRequests()
        {
            while (true)
            {
                UnixPtyBackendInstance? session;
                lock (s_lock)
                {
                    session = s_serviceHead;
                    if (session is null)
                        return;

                    s_serviceHead = session.NextService;
                    if (s_serviceHead is null)
                        s_serviceTail = null;

                    session.NextService = null;
                    session.ServiceQueued = false;
                }

                session.ServiceOnLoop(0);
            }
        }

        /// <summary>
        /// The longest the thread may wait: forever, or the polling interval while any child is polled or hung up, or
        /// until the earliest hang-up deadline if that comes first. Caller holds <see cref="s_lock"/>.
        /// </summary>
        private static int WaitTimeoutLocked()
        {
            if (s_hangups.Count == 0)
                return s_polled.Count > 0 ? PollIntervalMs : Timeout.Infinite;

            // A hung-up child's output is discarded on every pass, so this thread wakes at the polling interval.
            var timeout = PollIntervalMs;
            var now = Environment.TickCount64;
            foreach (var child in s_hangups)
            {
                var remaining = (int)Math.Clamp(child.HangupKillDeadline - now, 0, int.MaxValue);
                if (remaining < timeout)
                    timeout = remaining;
            }

            return timeout;
        }

        /// <summary>
        /// Kills each hung-up child whose deadline has passed, and discards the output of the others. Runs on every
        /// pass of this thread.
        /// </summary>
        private static void ServeHangups()
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
                    {
                        // A session read on this thread keeps its master registered and discards there.
                        if (!child.DiscardsOnLoop)
                            child.DiscardOutputAfterHangup();

                        continue;
                    }

                    child.KillAfterHangup();
                    s_hangups.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Blocks in the native watcher until a child exits, a master reports, the wait timeout elapses, or a wake-up.
        /// </summary>
        private static unsafe int WaitNative(Span<ulong> tokens, Span<int> reports)
        {
            int timeout;
            lock (s_lock)
                timeout = WaitTimeoutLocked();

            int count;
            fixed (ulong* tokenPtr = tokens)
            fixed (int* reportPtr = reports)
                count = minipty_watch_wait(s_watchFd, s_wakeRead, tokenPtr, reportPtr, tokens.Length, timeout);

            if (count >= 0)
                return count;

            lock (s_lock)
            {
                // Sessions read on this thread have no other reader, so it keeps waiting rather than dropping them.
                if (s_mastersRegistered > 0)
                {
                    Monitor.Wait(s_lock, PollIntervalMs);
                    return 0;
                }

                // The native wait itself failed: degrade to polling every remaining child.
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
        private static partial int minipty_child_watch_add_output(int watchFd, int masterFd);

        [LibraryImport("minipty_unix")]
        private static partial void minipty_child_watch_remove_output(int watchFd, int masterFd);

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_child_watch_wake(int watchFd, int wakeWrite);

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static unsafe partial int minipty_watch_wait(int watchFd, int wakeRead, ulong* tokens, int* reports, int capacity, int timeoutMs);

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_watch_add(int watchFd, int fd, ulong token, int interest);

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_watch_modify(int watchFd, int fd, ulong token, int interest);

        [LibraryImport("minipty_unix", SetLastError = true)]
        private static partial int minipty_watch_renew(int watchFd, int fd, ulong token, int interest);

        [LibraryImport("minipty_unix")]
        private static partial void minipty_watch_remove(int watchFd, int fd);
    }
}
