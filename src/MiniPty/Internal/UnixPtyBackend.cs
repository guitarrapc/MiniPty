using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

internal static partial class UnixPtyBackend
{
    private const byte InputEot = 0x04;
    // Disposal hangs the child up and kills it only this long afterwards, because a shell needs time to save its
    // history and run its exit hooks. The bound is for a child that ignores the hang-up.
    internal const int DisposeHangupGraceMs = 1000;
    // EOF staging advances one step per tick, matching the cadence of the exit-wait poll loop it replaced.
    private const int EofTickMs = 100;
    // macOS posix_spawn children can still be running short -c scripts when the first EOF tick fires.
    // Sending EOT before they finish leaves /bin/sh waiting on stdin and never exiting.
    private const int MacOsEofAttachDeferPolls = 4;
    // How long a read keeps appending output that arrives right after it to the same ring slot.
    private const int CoalesceMicroWindowMs = 1;
    // Bounds the windows chained into one slot, so output trickling in faster than the window is not held until
    // the slot fills. The last window may run past it: a slot is held for at most about 3 ms.
    private const int CoalesceBudgetMs = 2;
    private static readonly long s_coalesceBudgetTicks = System.Diagnostics.Stopwatch.Frequency * CoalesceBudgetMs / 1000;

    internal static IPtyBackend Start(PtyStartInfo startInfo)
    {
        var size = startInfo.ClampedSize;
        var winsize = new Winsize { ws_col = (ushort)size.Columns, ws_row = (ushort)size.Rows };
        var arguments = startInfo.Arguments as string[] ?? startInfo.Arguments.ToArray();
        var environment = PtyEnvironment.BuildUnix(startInfo);
        var exec = UnixExecPayload.Create(startInfo.FileName, arguments, startInfo.WorkingDirectory, environment);
        var pid = 0;
        var master = 0;
        unsafe
        {
            var spawnError = ForkPtyExec(&master, &winsize, exec.WorkingDirectory, exec.Executable, exec.Argv, exec.Envp, &pid);
            if (spawnError != 0)
            {
                exec.Dispose();
                throw new IOException($"PTY spawn failed (errno {spawnError})");
            }
        }

        try
        {
            return new UnixPtyBackendInstance(master, pid, size, exec);
        }
        catch
        {
            // The child runs, but no watcher will reap it, because the instance registers with the watcher last. It is
            // killed and reaped here, so a failed start leaves neither a running child nor a zombie.
            UnixInterop.kill(pid, UnixInterop.SigKill);
            while (UnixInterop.waitpid(pid, out _, 0) < 0 && Marshal.GetLastPInvokeError() == UnixInterop.EINTR)
            {
            }

            exec.Dispose();
            if (master >= 0)
                UnixInterop.close(master);
            throw;
        }
    }

    private static unsafe int ForkPtyExec(
        int* master,
        Winsize* winsize,
        IntPtr workingDirectory,
        IntPtr executable,
        IntPtr argv,
        IntPtr envp,
        int* pid) =>
        minipty_fork_pty_exec(
            master,
            winsize,
            (byte*)workingDirectory,
            (byte*)executable,
            (byte**)argv,
            (byte**)envp,
            pid);

    private sealed class UnixExecPayload : IDisposable
    {
        private readonly List<IntPtr> _owned;

        private UnixExecPayload(List<IntPtr> owned, IntPtr executable, IntPtr argv, IntPtr envp, IntPtr workingDirectory)
        {
            _owned = owned;
            Executable = executable;
            Argv = argv;
            Envp = envp;
            WorkingDirectory = workingDirectory;
        }

        public IntPtr Executable { get; }
        public IntPtr Argv { get; }
        public IntPtr Envp { get; }
        public IntPtr WorkingDirectory { get; }

        public static unsafe UnixExecPayload Create(
            string fileName,
            string[] arguments,
            string? cwd,
            KeyValuePair<string, string>[]? environment)
        {
            var owned = new List<IntPtr>();
            try
            {
                var executable = AllocUtf8CString(fileName, owned);
                var argv = AllocUtf8Argv(fileName, arguments, owned);
                var envp = environment is null ? null : AllocUtf8Envp(environment, owned);
                IntPtr workingDirectory = IntPtr.Zero;
                if (!string.IsNullOrWhiteSpace(cwd))
                    workingDirectory = (IntPtr)AllocUtf8CString(cwd, owned);
                return new UnixExecPayload(owned, (IntPtr)executable, (IntPtr)argv, (IntPtr)envp, workingDirectory);
            }
            catch
            {
                FreeUtf8Allocations(owned);
                throw;
            }
        }

        public void Dispose() => FreeUtf8Allocations(_owned);
    }

    private sealed class UnixPtyBackendInstance : IPtyBackend, IPtyInputQueue, IValueTaskSource<int>
    {
        // _masterState: bit 0 is set once the master is closed to new users; the remaining bits count references
        // in units of MasterReference. The owner holds one reference from spawn until the transport closes.
        private const int MasterClosedBit = 1;
        private const int MasterReference = 2;

        // _inputState: whether queued input can still be written.
        private const int InputOpen = 0;
        private const int InputOutputEnded = 1;
        private const int InputDisposed = 2;

        private readonly int _master;
        private readonly int _pid;
        private readonly UnixExecPayload? _execHold;
        // Serializes the child's reap with signal delivery, so Kill can never reach a pid the watcher has
        // already released for reuse.
        private readonly Lock _pidLock = new();
        // Mutable struct: used only through this field, never copied.
        private PtyExitSignal _exitSignal;
        private Timer? _eofTimer;
        // Zero until Dispose hangs up a running child. Written under _pidLock.
        private long _hangupKillDeadline;
        private bool _eofSent;
        private bool _eofPending;
        private int _eofAttachPollsRemaining;
        private bool _eotLineSubmitSent;
        private bool _inputWritten;
        private bool _inputEndsWithNewline;
        private int _masterState = MasterReference;
        // Input the tty input queue could not take yet, oldest first; guarded by lock (this), which serializes
        // every input and EOT write with the queue. Locking the instance itself, which is never exposed, adds no
        // object to the session.
        private PtyPendingInput? _inputHead;
        private PtyPendingInput? _inputTail;
        private int _inputState;
        private PtyOutputPump? _pump;
        private bool _exited;
        private int _exitCode;
        private int _termSignal;
        private bool _disposed;
        private PtySize _size;

        public UnixPtyBackendInstance(int master, int pid, PtySize size, UnixExecPayload execHold)
        {
            _master = master;
            _pid = pid;
            _execHold = execHold;
            _size = size;
            // Non-blocking, so a write takes what the tty input queue accepts and never blocks its caller. The reader
            // reads only after the master reports readable. A read that still finds nothing waits again.
            if (UnixInterop.minipty_set_nonblocking(master) != 0)
                throw new IOException($"Setting the PTY master non-blocking failed (errno {Marshal.GetLastPInvokeError()})");

            Input = new PtyInputStream(this);
            // Last, so the watcher never observes a partially constructed instance.
            ChildWatcher.Register(this);
        }

        public Stream Input { get; }

        public void SetOutputPump(PtyOutputPump pump) => Volatile.Write(ref _pump, pump);

        public PtyPendingInput? QueueInput(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return null;

            PtyPendingInput entry;
            lock (this)
            {
                ThrowIfInputClosedLocked();
                if (_inputHead is null)
                {
                    var written = WriteNow(bytes);
                    if (written == bytes.Length)
                        return null;

                    bytes = bytes[written..];
                }

                // The caller's span does not outlive this call, so what is left is copied for the reader thread.
                var owned = ArrayPool<byte>.Shared.Rent(bytes.Length);
                bytes.CopyTo(owned);
                entry = EnqueueLocked(owned.AsMemory(0, bytes.Length), owned);
            }

            WakeReaderForInput();
            return entry;
        }

        public PtyPendingInput? QueueInput(ReadOnlyMemory<byte> bytes, byte[]? ownedBuffer)
        {
            // Returned to the pool here unless a queued entry takes it over.
            var owned = ownedBuffer;
            try
            {
                if (bytes.IsEmpty)
                    return null;

                PtyPendingInput entry;
                lock (this)
                {
                    ThrowIfInputClosedLocked();
                    if (_inputHead is null)
                    {
                        var written = WriteNow(bytes.Span);
                        if (written == bytes.Length)
                            return null;

                        bytes = bytes[written..];
                    }

                    entry = EnqueueLocked(bytes, owned);
                    owned = null;
                }

                WakeReaderForInput();
                return entry;
            }
            finally
            {
                if (owned is not null)
                    ArrayPool<byte>.Shared.Return(owned);
            }
        }

        private bool HasPendingInput => Volatile.Read(ref _inputHead) is not null;

        /// <summary>Throws once input can no longer be written. Caller holds lock (this).</summary>
        private void ThrowIfInputClosedLocked()
        {
            switch (_inputState)
            {
                case InputDisposed:
                    throw new ObjectDisposedException(nameof(PtySession));
                case InputOutputEnded:
                    throw new IOException("PTY input cannot be written: the child's terminal has closed.");
            }
        }

        /// <summary>
        /// Writes what the master accepts now. Returns the byte count written; fewer than requested means the tty
        /// input queue is full. Caller holds lock (this).
        /// </summary>
        private unsafe int WriteNow(ReadOnlySpan<byte> bytes)
        {
            if (!TryAcquireMaster(out var master))
                throw new IOException("The PTY transport is closed.");

            try
            {
                fixed (byte* ptr = bytes)
                {
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var written = UnixInterop.Write(master, ptr + offset, (nuint)(bytes.Length - offset));
                        if (written > 0)
                        {
                            offset += written;
                            continue;
                        }

                        if (written == 0)
                            throw new IOException("write wrote 0 bytes");

                        var errno = Marshal.GetLastPInvokeError();
                        if (errno == UnixInterop.EINTR)
                            continue;
                        if (errno == UnixInterop.EAGAIN)
                            break;

                        throw new IOException($"write failed (errno {errno})");
                    }

                    // EOT goes by the bytes the terminal took. A canceled write discards the rest, so the caller's
                    // last byte may never arrive.
                    if (offset > 0)
                        OnInputWritten(bytes[..offset]);

                    return offset;
                }
            }
            finally
            {
                ReleaseMaster();
            }
        }

        /// <summary>Appends a queued write. Caller holds lock (this) and has checked that input is open.</summary>
        private PtyPendingInput EnqueueLocked(ReadOnlyMemory<byte> remaining, byte[]? owned)
        {
            var entry = PtyPendingInput.Rent(this, remaining, owned);
            PtyPendingInput.Enqueue(ref _inputHead, ref _inputTail, entry);
            return entry;
        }

        // The reader thread may be waiting for output only. This makes it wait for the master to take input too.
        private void WakeReaderForInput() => Volatile.Read(ref _pump)?.WakeReader();

        /// <summary>Writes queued input until the master is full again. Runs on the reader thread.</summary>
        private void FlushInput()
        {
            PtyPendingInput? done = null;
            PtyPendingInput? doneTail = null;
            PtyPendingInput? failed = null;
            Exception? error = null;
            lock (this)
            {
                while (_inputHead is { } head)
                {
                    int written;
                    try
                    {
                        written = WriteNow(head.Remaining.Span);
                    }
                    catch (Exception ex)
                    {
                        // The child's terminal closed (EIO) or the transport did: nothing queued can be written.
                        error = ex;
                        failed = PtyPendingInput.DetachAll(ref _inputHead, ref _inputTail);
                        break;
                    }

                    if (written < head.Remaining.Length)
                    {
                        head.Remaining = head.Remaining[written..];
                        break;
                    }

                    PtyPendingInput.DequeueHead(ref _inputHead, ref _inputTail);
                    if (doneTail is null)
                        done = head;
                    else
                        doneTail.Next = head;

                    doneTail = head;
                }
            }

            // Completed outside the lock: a waiter's continuation must never run while the queue is held.
            PtyPendingInput.CompleteChain(done, null);
            PtyPendingInput.CompleteChain(failed, error);
        }

        public void CancelInput(PtyPendingInput entry)
        {
            bool removed;
            lock (this)
                removed = PtyPendingInput.Remove(ref _inputHead, ref _inputTail, entry);

            // Bytes of it already written stay written; the rest is discarded.
            if (removed)
                entry.Complete(new OperationCanceledException(entry.Token));
        }

        /// <summary>
        /// Stops accepting queued input and fails what is queued. Disposal overrides an earlier end of output, such as a
        /// child that exited before disposal, so that a write after disposal reports the disposal.
        /// </summary>
        private void CloseInput(int reason)
        {
            PtyPendingInput? queued;
            lock (this)
            {
                if (_inputState == InputOpen || reason == InputDisposed)
                    _inputState = reason;

                queued = PtyPendingInput.DetachAll(ref _inputHead, ref _inputTail);
            }

            if (queued is null)
                return;

            Exception error = reason == InputDisposed
                ? new ObjectDisposedException(nameof(PtySession))
                : new IOException("PTY input cannot be written: the child's terminal has closed.");
            PtyPendingInput.CompleteChain(queued, error);
        }

        public int ProcessId => _pid;

        // Exit state is cached by the watcher's reap; reading it never calls waitpid, which would race
        // the watcher for the one-time wait status.
        public bool HasExited => IsExitObserved;

        public int ExitCode => _exitCode;

        public int? ExitSignal => IsExitObserved && _termSignal != 0 ? _termSignal : null;

        public Task<PtyExitStatus> Exited => _exitSignal.GetTask();

        public ValueTask<int> WaitForExitCodeAsync() => _exitSignal.WaitForExitCodeAsync(this);

        public void SetExitListener(IPtyExitListener listener) => _exitSignal.SetListener(listener);

        int IValueTaskSource<int>.GetResult(short token) => _exitSignal.GetWaiterResult(token);

        ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _exitSignal.GetWaiterStatus(token);

        void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _exitSignal.OnWaiterCompleted(continuation, state, token, flags);

        private bool IsExitObserved => Volatile.Read(ref _exited);

        public PtySize Size => _size;

        public string? ActiveProcessName
        {
            get
            {
                if (_disposed || IsExitObserved || !TryAcquireMaster(out var master))
                    return null;

                Span<byte> name = stackalloc byte[256];
                int length;
                try
                {
                    unsafe
                    {
                        fixed (byte* buffer = name)
                            length = minipty_get_active_process_name(master, buffer, name.Length);
                    }
                }
                finally
                {
                    ReleaseMaster();
                }

                if (length <= 0)
                    return null;

                var processName = Encoding.UTF8.GetString(name[..length]);
                return processName is "spawn_helper" or "minipty_spawn_helper" or "kernel_task"
                    ? null
                    : processName;
            }
        }

        public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Frontend size events race child exit. Once post-exit drain has closed the master there is
            // nothing left to resize, so the call is a no-op rather than an error. The reference keeps the
            // descriptor from being closed, and its number reused, during the ioctl.
            if (!TryAcquireMaster(out var master))
                return;

            try
            {
                columns = PtySizeLimits.Clamp(columns);
                rows = PtySizeLimits.Clamp(rows);
                if (minipty_set_winsize(
                        master,
                        (ushort)rows,
                        (ushort)columns,
                        (ushort)pixelWidth,
                        (ushort)pixelHeight) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (IsMasterClosed || IsExitObserved)
                        return;

                    throw new IOException($"TIOCSWINSZ failed (errno {errno})");
                }

                _size = new PtySize(columns, rows);
            }
            finally
            {
                ReleaseMaster();
            }
        }

        public void SendEof()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsExitObserved || _eofSent || _eofPending)
                return;

            // Defer EOT so the child has time to attach (same attach race as ConPTY). The staging timer
            // advances it, so EOF no longer depends on somebody awaiting exit.
            _eofPending = true;
            if (OperatingSystem.IsMacOS())
                _eofAttachPollsRemaining = MacOsEofAttachDeferPolls;

            ScheduleEofTick();
        }

        public void Kill()
        {
            lock (_pidLock)
            {
                if (_disposed || _exited)
                    return;

                KillCore();
            }
        }

        public void Kill(PtySignal signal)
        {
            // Validate before the disposed/exited guards so misuse always throws.
            var nativeSignal = MapSignal(signal);
            lock (_pidLock)
            {
                if (_disposed || _exited)
                    return;

                // Fire-and-forget like KillCore; ESRCH after a racing exit is benign.
                UnixInterop.kill(_pid, nativeSignal);
            }
        }

        private void KillCore()
        {
            UnixInterop.kill(_pid, UnixInterop.SigKill);
        }

        private static int MapSignal(PtySignal signal) => signal switch
        {
            PtySignal.Hangup => UnixInterop.SigHup,
            PtySignal.Interrupt => UnixInterop.SigInt,
            PtySignal.Quit => UnixInterop.SigQuit,
            PtySignal.Kill => UnixInterop.SigKill,
            PtySignal.User1 => OperatingSystem.IsLinux() ? UnixInterop.SigUsr1Linux : UnixInterop.SigUsr1Bsd,
            PtySignal.User2 => OperatingSystem.IsLinux() ? UnixInterop.SigUsr2Linux : UnixInterop.SigUsr2Bsd,
            PtySignal.Terminate => UnixInterop.SigTerm,
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        };

        // Unix PTYs report end of output on their own once every slave descriptor closes; the post-exit drain
        // closes the master only when a grandchild keeps the slave open (Linux; macOS revokes the tty when the
        // session leader exits).
        public void CloseOutputTransport() => CloseMaster();

        public bool IsOutputClosed => IsMasterClosed;

        public long CoalesceBudgetTicks => s_coalesceBudgetTicks;

        public int PostExitQuietMs => PtyOutputPump.DefaultPostExitQuietMs;

        public bool TryBeginOutputRead()
        {
            if (TryAcquireMaster(out _))
                return true;

            // The master closed before the reader began. The reader is what writes queued input, so nothing ever will.
            CloseInput(InputOutputEnded);
            return false;
        }

        public void EndOutputRead()
        {
            // Queued input is written by this reader; once it stops, nothing can deliver it.
            CloseInput(InputOutputEnded);
            ReleaseMaster();
        }

        /// <summary>
        /// Waits on the master and the reader's wake descriptor together, then reads; while input is queued the
        /// same wait also reports when the master can take more, and the queue is written then. Closing the master
        /// does not interrupt a read blocked on it in another thread, so a close or disposal wakes the reader
        /// instead, and the reader's reference keeps the descriptor number from being reused under it.
        /// </summary>
        public int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            while (true)
            {
                if (IsMasterClosed)
                    return 0;

                var events = UnixInterop.PtyReadable | (HasPendingInput ? UnixInterop.PtyWritable : 0);
                var ready = UnixInterop.minipty_pty_wait(_master, events, reader.WakeFd, Timeout.Infinite);
                if (ready < 0)
                    throw new IOException($"Waiting for PTY output failed (errno {Marshal.GetLastPInvokeError()})");

                if ((ready & UnixInterop.PtyWritable) != 0)
                    FlushInput();

                if ((ready & UnixInterop.PtyWoken) != 0)
                {
                    reader.ConsumeWake();
                    return -1;
                }

                if ((ready & UnixInterop.PtyReadable) != 0)
                {
                    var read = ReadMaster(buffer);
                    if (read >= 0)
                        return read;
                }
            }
        }

        /// <summary>
        /// Appends output that is available now or arrives within the micro-window. A pending wake is left for
        /// <see cref="ReadOutput"/>, which re-checks the session state.
        /// </summary>
        public int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput)
        {
            endOfOutput = false;
            if (IsMasterClosed)
                return 0;

            var ready = UnixInterop.minipty_pty_wait(_master, UnixInterop.PtyReadable, reader.WakeFd, CoalesceMicroWindowMs);
            if (ready <= 0 || (ready & UnixInterop.PtyReadable) == 0)
                return 0;

            var read = ReadMaster(buffer);
            endOfOutput = read == 0;
            return read < 0 ? 0 : read;
        }

        /// <summary>
        /// Parks the reader on a full ring, but keeps writing queued input: a child blocked on output may still be
        /// reading input, and a caller waits for that input to be written.
        /// </summary>
        public void WaitWhileOutputBlocked(PtyReaderThread reader)
        {
            if (!HasPendingInput || IsMasterClosed)
            {
                reader.WaitForWake(Timeout.Infinite);
                return;
            }

            var ready = UnixInterop.minipty_pty_wait(_master, UnixInterop.PtyWritable, reader.WakeFd, Timeout.Infinite);
            if (ready < 0)
            {
                // Not expected for descriptors the reader holds; back off rather than spin.
                Thread.Sleep(10);
                return;
            }

            if ((ready & UnixInterop.PtyWritable) != 0)
                FlushInput();

            if ((ready & UnixInterop.PtyWoken) != 0)
                reader.ConsumeWake();
        }

        /// <summary>
        /// Reads after the master reported ready. Returns the byte count, 0 at end of output, or -1 when nothing was
        /// there after all (the master is non-blocking).
        /// </summary>
        private unsafe int ReadMaster(Span<byte> buffer)
        {
            fixed (byte* ptr = buffer)
            {
                while (true)
                {
                    var read = UnixInterop.Read(_master, ptr, (nuint)buffer.Length);
                    if (read >= 0)
                        return read;

                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == UnixInterop.EINTR)
                        continue;
                    if (errno == UnixInterop.EAGAIN)
                        return -1;

                    // EIO: every slave descriptor closed (Linux reports end of output this way).
                    if (errno is 0 or UnixInterop.EIO or UnixInterop.EBADF)
                        return 0;

                    throw new IOException($"read failed (errno {errno})");
                }
            }
        }

        private bool IsMasterClosed => (Volatile.Read(ref _masterState) & MasterClosedBit) != 0;

        /// <summary>Pins the master for one use. Fails once it is closed; pair a success with <see cref="ReleaseMaster"/>.</summary>
        private bool TryAcquireMaster(out int master)
        {
            var state = Volatile.Read(ref _masterState);
            while ((state & MasterClosedBit) == 0)
            {
                var seen = Interlocked.CompareExchange(ref _masterState, state + MasterReference, state);
                if (seen == state)
                {
                    master = _master;
                    return true;
                }

                state = seen;
            }

            master = -1;
            return false;
        }

        private void ReleaseMaster()
        {
            // The last reference after the close closes the descriptor.
            if (Interlocked.Add(ref _masterState, -MasterReference) == MasterClosedBit)
                UnixInterop.close(_master);
        }

        /// <summary>
        /// Stops new uses of the master and drops the owner's reference. The descriptor closes now, or when the
        /// reader and any in-flight call release theirs, so its number is never reused while someone still uses it.
        /// </summary>
        private void CloseMaster()
        {
            var state = Volatile.Read(ref _masterState);
            while ((state & MasterClosedBit) == 0)
            {
                var seen = Interlocked.CompareExchange(ref _masterState, state | MasterClosedBit, state);
                if (seen == state)
                {
                    ReleaseMaster();
                    return;
                }

                state = seen;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            // Queued writes fail now, on the disposing thread, without waiting for the reader to stop. This comes before
            // the hang-up and the master close, which end output, so a queued write reports the disposal rather than
            // the end of output.
            CloseInput(InputDisposed);
            var exitObserved = true;
            lock (_pidLock)
            {
                // A hang-up rather than a kill, so a shell can save its history and run its exit hooks. Closing the
                // master below hangs the tty up too, but only once no other process holds a copy of the master, so
                // the signal is sent explicitly. The deadline is set under the lock, so a racing second Dispose finds
                // it and schedules no other kill.
                if (!_exited && _hangupKillDeadline == 0)
                {
                    exitObserved = false;
                    UnixInterop.kill(_pid, UnixInterop.SigHup);
                    _hangupKillDeadline = Environment.TickCount64 + DisposeHangupGraceMs;
                }
            }

            _eofTimer?.Dispose();
            if (!exitObserved)
            {
                _exitSignal.SetDisposedBeforeExit();
                // The watcher kills a child that ignores the hang-up and reaps it either way, so disposal never waits.
                ChildWatcher.ScheduleHangupKill(this);
            }

            CloseTransport();
            _execHold?.Dispose();
        }

        /// <summary>The tick count at which <see cref="ChildWatcher"/> kills this child if a disposal hung it up.</summary>
        internal long HangupKillDeadline => _hangupKillDeadline;

        /// <summary>Sends the kill that backs a disposal's hang-up. Called only by <see cref="ChildWatcher"/>.</summary>
        internal void KillAfterHangup()
        {
            // Under the reap's lock, so a child the watcher has already reaped is skipped and its released pid is
            // never signaled.
            lock (_pidLock)
            {
                if (!_exited)
                    KillCore();
            }
        }

        /// <summary>
        /// Reaps the child when it has exited. Called only by <see cref="ChildWatcher"/>, the sole reaper.
        /// </summary>
        /// <returns><see langword="true"/> once the child has been reaped.</returns>
        internal bool TryReapExitedChild()
        {
            lock (_pidLock)
            {
                if (_exited)
                    return true;

                if (TryWaitPid(_pid, UnixInterop.WaitNoHang, out var status, out var result))
                {
                    if (result != _pid)
                        return false;

                    _exitCode = MapWaitStatusToExitCode(status);
                    _termSignal = WIFSIGNALED(status) ? WTERMSIG(status) : 0;
                }
                else if (Marshal.GetLastPInvokeError() != UnixInterop.ECHILD)
                {
                    return false;
                }

                // ECHILD: something outside MiniPty reaped the child, so its status is lost; report exit 0.
                Volatile.Write(ref _exited, true);
            }

            _exitSignal.TrySetExited(new PtyExitStatus(_exitCode, _termSignal != 0 ? _termSignal : null));
            return true;
        }

        /// <summary>
        /// Records exit for a child that something outside MiniPty reaped, without calling <c>waitpid</c>: its pid
        /// already belongs to another child. The wait status is lost, as on <c>ECHILD</c>. Called only by
        /// <see cref="ChildWatcher"/>.
        /// </summary>
        internal void OnReapedElsewhere()
        {
            lock (_pidLock)
            {
                if (_exited)
                    return;

                Volatile.Write(ref _exited, true);
            }

            _exitSignal.TrySetExited(new PtyExitStatus(_exitCode, null));
        }

        /// <summary>Arms the one-shot EOF staging tick; each tick re-arms itself while staging is in progress.</summary>
        private void ScheduleEofTick()
        {
            _eofTimer ??= new Timer(static state => ((UnixPtyBackendInstance)state!).OnEofTick(), this, Timeout.Infinite, Timeout.Infinite);
            _eofTimer.Change(EofTickMs, Timeout.Infinite);
        }

        private void OnEofTick()
        {
            if (_disposed || IsExitObserved || !_eofPending)
                return;

            // A timer callback must never throw: an unhandled exception there terminates the host process.
            try
            {
                SendEotIfPending();
                if (!_disposed && !IsExitObserved && _eofPending)
                    _eofTimer?.Change(EofTickMs, Timeout.Infinite);
            }
            catch (Exception)
            {
                // The transport failed, or Dispose closed it or the timer under the tick; nothing is left to signal.
                CompleteEotSignaling();
            }
        }

        private void CloseTransport()
        {
            SendEotIfPending();
            CloseMaster();
        }

        private void SendEotIfPending()
        {
            if (!_eofPending)
                return;

            if (_eofAttachPollsRemaining > 0)
            {
                _eofAttachPollsRemaining--;
                return;
            }

            WriteEotToMaster();
        }

        private void OnInputWritten(ReadOnlySpan<byte> buffer)
        {
            _inputWritten = true;
            var last = buffer[^1];
            _inputEndsWithNewline = last is (byte)'\n' or (byte)'\r';
        }

        private void WriteEotToMaster()
        {
            if (_eofSent || _exited)
                return;

            if (IsExitObserved)
            {
                CompleteEotSignaling();
                return;
            }

            // EOT goes after every byte written before it: while input is still queued, a later tick sends it.
            if (HasPendingInput || !TryAcquireMaster(out var master))
                return;

            try
            {
                DrainMasterOutputBeforeEot(master);

                if (IsExitObserved)
                {
                    CompleteEotSignaling();
                    return;
                }

                lock (this)
                {
                    if (_inputHead is not null)
                        return;

                    // Canonical line discipline: one EOT on a non-empty buffer submits the line but
                    // does not signal EOF; a second EOT on the empty buffer ends input for programs like cat.
                    // Stage the two EOT writes on separate ticks so the child can attach and consume the submit.
                    if (_inputWritten && !_inputEndsWithNewline && !_eotLineSubmitSent)
                    {
                        switch (TryWriteEotByte(master))
                        {
                            case EotWritten:
                                _eotLineSubmitSent = true;
                                break;
                            case EotSlaveGone:
                                CompleteEotSignaling();
                                break;
                        }

                        return;
                    }

                    // A full tty input queue (EotWouldBlock) leaves EOF pending for a later tick.
                    if (TryWriteEotByte(master) != EotWouldBlock)
                        CompleteEotSignaling();
                }
            }
            finally
            {
                ReleaseMaster();
            }
        }

        private void CompleteEotSignaling()
        {
            _eofPending = false;
            _eofSent = true;
        }

        private const int EotWritten = 0;
        private const int EotSlaveGone = 1;
        private const int EotWouldBlock = 2;

        /// <summary>
        /// Writes one EOT byte. Reports <see cref="EotSlaveGone"/> when the slave is gone (EIO/EPIPE), which is
        /// benign after exit, and <see cref="EotWouldBlock"/> when the tty input queue is full.
        /// </summary>
        private static unsafe int TryWriteEotByte(int master)
        {
            Span<byte> eot = stackalloc byte[1] { InputEot };
            fixed (byte* ptr = eot)
            {
                while (true)
                {
                    var written = UnixInterop.Write(master, ptr, 1);
                    if (written == 1)
                        return EotWritten;
                    if (written < 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        if (errno == UnixInterop.EINTR)
                            continue;
                        if (errno == UnixInterop.EAGAIN)
                            return EotWouldBlock;

                        // The slave closed: the child exited or is exiting, and the watcher will reap it.
                        if (errno is UnixInterop.EIO or UnixInterop.EPIPE)
                            return EotSlaveGone;

                        throw new IOException($"write failed (errno {errno})");
                    }

                    throw new IOException("write wrote 0 bytes");
                }
            }
        }

        /// <summary>
        /// Waits until prior master writes reach the slave, or the slave opens, so staged EOT is not lost to attach races.
        /// macOS <c>tcdrain</c> can block forever when the slave never reads; rely on attach defer and exit checks instead.
        /// </summary>
        private void DrainMasterOutputBeforeEot(int master)
        {
            if (!_inputWritten)
                return;

            if (IsExitObserved)
                return;

            if (OperatingSystem.IsMacOS())
                return;

            while (UnixInterop.tcdrain(master) != 0)
            {
                if (Marshal.GetLastPInvokeError() is UnixInterop.EINTR)
                    continue;
                return;
            }
        }
    }

    private static unsafe byte** AllocUtf8Argv(string fileName, string[] arguments, List<IntPtr> owned)
    {
        var argc = arguments.Length + 1;
        var argv = (byte**)NativeMemory.Alloc((nuint)(argc + 1) * (nuint)IntPtr.Size);
        owned.Add((IntPtr)argv);

        argv[0] = AllocUtf8CString(fileName, owned);
        for (var i = 0; i < arguments.Length; i++)
            argv[i + 1] = AllocUtf8CString(arguments[i], owned);
        argv[argc] = null;
        return argv;
    }

    private static unsafe byte** AllocUtf8Envp(KeyValuePair<string, string>[] environment, List<IntPtr> owned)
    {
        var envp = (byte**)NativeMemory.Alloc((nuint)(environment.Length + 1) * (nuint)IntPtr.Size);
        owned.Add((IntPtr)envp);

        for (var i = 0; i < environment.Length; i++)
            envp[i] = AllocUtf8CString(environment[i].Key + "=" + environment[i].Value, owned);
        envp[environment.Length] = null;
        return envp;
    }

    private static unsafe byte* AllocUtf8CString(string value, List<IntPtr> owned)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        owned.Add((IntPtr)ptr);
        bytes.AsSpan().CopyTo(new Span<byte>(ptr, bytes.Length));
        ptr[bytes.Length] = 0;
        return ptr;
    }

    private static unsafe void FreeUtf8Allocations(List<IntPtr> owned)
    {
        foreach (var ptr in owned)
            NativeMemory.Free((void*)ptr);
        owned.Clear();
    }

    // The exited/signaled wait-status layout (low 7 bits = signal, bits 8-15 = exit code) is
    // identical on Linux, macOS, and FreeBSD, so decoding stays managed. WIFSIGNALED uses the
    // explicit form excluding the stopped marker 0x7f: the textbook glibc macro relies on a
    // signed-char cast that a direct C# transcription loses, which would misclassify a stopped
    // status as signaled. Unreachable under WNOHANG-only polling today, but load-bearing now
    // that the termination signal is public API.
    private static bool WIFEXITED(int status) => (status & 0x7f) == 0;
    private static int WEXITSTATUS(int status) => (status >> 8) & 0xff;
    private static bool WIFSIGNALED(int status) => (status & 0x7f) != 0 && (status & 0x7f) != 0x7f;
    private static int WTERMSIG(int status) => status & 0x7f;

    private static int MapWaitStatusToExitCode(int status)
    {
        if (WIFEXITED(status))
            return WEXITSTATUS(status);
        if (WIFSIGNALED(status))
            return 128 + WTERMSIG(status);
        return 1;
    }

    private static bool TryWaitPid(int pid, int options, out int status, out int result)
    {
        while (true)
        {
            result = UnixInterop.waitpid(pid, out status, options);
            if (result >= 0)
                return true;
            if (Marshal.GetLastPInvokeError() == UnixInterop.EINTR)
                continue;
            return false;
        }
    }

    [LibraryImport("minipty_unix")]
    private static unsafe partial int minipty_fork_pty_exec(
        int* master,
        Winsize* winp,
        byte* working_directory,
        byte* file,
        byte** argv,
        byte** envp,
        int* pid_out);

    [LibraryImport("minipty_unix", SetLastError = true)]
    private static partial int minipty_set_winsize(
        int master,
        ushort rows,
        ushort cols,
        ushort pixelWidth,
        ushort pixelHeight);

    [LibraryImport("minipty_unix")]
    private static unsafe partial int minipty_get_active_process_name(int master, byte* buffer, int bufferLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }
}
