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
        var pidfd = -1;
        var slave = -1;
        unsafe
        {
            var spawnError = ForkPtyExec(&master, &winsize, exec.WorkingDirectory, exec.Executable, exec.Argv, exec.Envp, &pid, &pidfd, &slave);
            if (spawnError != 0)
            {
                exec.Dispose();
                throw new IOException($"PTY spawn failed (errno {spawnError})");
            }
        }

        try
        {
            PtyStartTestHook.Run(PtyStartStage.Spawned, pid);
            return new UnixPtyBackendInstance(master, slave, pid, pidfd, size, exec);
        }
        catch
        {
            // The child runs, but no watcher will reap it, because the instance registers with the watcher last. It is
            // killed and reaped here, so a failed start leaves neither a running child, a zombie, nor its pidfd.
            _ = UnixInterop.minipty_signal_child(pidfd, pid, UnixInterop.SigKill);
            _ = UnixInterop.minipty_reap_child(pidfd, pid, 1, out _, out _);
            if (pidfd >= 0)
                UnixInterop.close(pidfd);

            exec.Dispose();
            if (master >= 0)
                UnixInterop.close(master);
            if (slave >= 0)
                UnixInterop.close(slave);
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
        int* pid,
        int* pidfd,
        int* slave) =>
        minipty_fork_pty_exec(
            master,
            winsize,
            (byte*)workingDirectory,
            (byte*)executable,
            (byte**)argv,
            (byte**)envp,
            pid,
            pidfd,
            slave);

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

    private sealed class UnixPtyBackendInstance : IPtyBackend, IPtyBlockingOutput, IPtyInputQueue, IValueTaskSource<int>
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
        // macOS only, -1 elsewhere: a descriptor of the slave that closes with the master, so the child's exit is never
        // the terminal's last close. On that close macOS discards what the master has not read within 0.6 s; with this
        // descriptor open, the session leader's exit waits until the master has read it. Linux keeps unread output after
        // the last close, and there a descriptor of the host would keep output from ending at the exit.
        private readonly int _slave;
        private readonly int _pid;
        private readonly UnixExecPayload? _execHold;
        // Serializes the child's reap with signal delivery, so Kill can never reach a pid the watcher has
        // already released for reuse.
        private readonly Lock _pidLock = new();
        // The child's pidfd on Linux, opened by the spawn right after the fork, or -1 when there is none. The child's
        // signals go through it, and so does its reap from Linux 5.4. The watcher closes it once the child is reaped.
        // Written only by the constructor and, under _pidLock, by ClosePidfd.
        private int _pidfd = -1;
        // Mutable struct: used only through this field, never copied.
        private PtyExitSignal _exitSignal;
        private Timer? _eofTimer;
        // Zero until Dispose hangs up a running child. Written under _pidLock.
        private long _hangupKillDeadline;
        // Whether the master's output wakes the watcher while a disposal's hang-up keeps it open. Read and written only
        // under the watcher's lock.
        private bool _outputWatched;
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

        public UnixPtyBackendInstance(int master, int slave, int pid, int pidfd, PtySize size, UnixExecPayload execHold)
        {
            _master = master;
            _slave = slave;
            _pid = pid;
            _pidfd = pidfd;
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

        /// <summary>The child's pidfd on Linux, or -1. Read by <see cref="ChildWatcher"/> under its lock.</summary>
        internal int Pidfd => _pidfd;

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
        private void WakeReaderForInput() => Volatile.Read(ref _pump)?.WakeDriver();

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

        // A Unix PTY has no screen buffer of its own; the frontend owns the display.
        public void Clear() => ObjectDisposedException.ThrowIf(_disposed, this);

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
                SignalLocked(nativeSignal);
            }
        }

        private void KillCore()
        {
            SignalLocked(UnixInterop.SigKill);
        }

        /// <summary>
        /// Signals the child. Caller holds <see cref="_pidLock"/> and has checked that the child is not yet reaped.
        /// </summary>
        /// <remarks>
        /// Not <c>kill(pid)</c> alone: code outside MiniPty can reap the child, for example a host that reaps every
        /// child, and the pid can then belong to another process before the watcher notices. A pidfd names the child
        /// alone. Without one, the native call sends the signal only while the pid is still an unreaped child of the host.
        /// </remarks>
        private void SignalLocked(int signal) => _ = UnixInterop.minipty_signal_child(_pidfd, _pid, signal);

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

        public int OutputChunkLimit => PtyOutputPump.Capacity;

        public int PostExitQuietMs => PtyOutputPump.DefaultPostExitQuietMs;

        // The reader thread waits on the master and its wake descriptor together, and writes queued input when the
        // master can take more (see ReadOutput and WaitWhileOutputBlocked).
        public void StartOutput(PtyOutputPump pump) => PtyReaderThread.Run(pump, this);

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
                if (IsReadingEnded)
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
            if (IsReadingEnded)
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

        // Disposal ends reading even while the master stays open for a hung-up child, so the reader never waits for
        // that child to exit.
        private bool IsReadingEnded => IsMasterClosed || Volatile.Read(ref _disposed);

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
            // The last reference after the close closes the descriptor, and the slave's with it. The master goes first,
            // so its close hangs the terminal up as before, also for a child held in its exit until the master is read.
            if (Interlocked.Add(ref _masterState, -MasterReference) == MasterClosedBit)
            {
                UnixInterop.close(_master);
                if (_slave >= 0)
                    UnixInterop.close(_slave);
            }
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

            // Read by the reader thread, which stops on it while the master stays open for a hung-up child.
            Volatile.Write(ref _disposed, true);
            // Queued writes fail now, on the disposing thread, without waiting for the reader to stop. This comes before
            // the hang-up and the end of reading, which end output, so a queued write reports the disposal rather than
            // the end of output.
            CloseInput(InputDisposed);
            var exitObserved = true;
            lock (_pidLock)
            {
                // A hang-up rather than a kill, so a shell can save its history and run its exit hooks. It is sent
                // explicitly because the master close, which also hangs the tty up, waits until the child is gone
                // (below), and does not hang it up at all while another process holds a copy of the master. The
                // deadline is set under the lock, so a racing second Dispose finds it and schedules no other kill, and
                // a reap that takes the lock after it closes the master.
                if (!_exited && _hangupKillDeadline == 0)
                {
                    exitObserved = false;
                    SignalLocked(UnixInterop.SigHup);
                    _hangupKillDeadline = Environment.TickCount64 + DisposeHangupGraceMs;
                }
            }

            _eofTimer?.Dispose();
            if (exitObserved)
            {
                CloseTransport();
            }
            else
            {
                _exitSignal.SetDisposedBeforeExit();
                // The master stays open until the watcher reaps the child or kills it after the grace. Closing it hangs
                // the tty up, which signals the child a second time, and an interactive bash that gets that signal while
                // it saves its history dies before it hangs up its jobs. The reader stops now all the same, and the watcher
                // discards what the child writes meanwhile.
                SendEotIfPending();
                // The watcher kills a child that ignores the hang-up and reaps it either way, so disposal never waits.
                ChildWatcher.ScheduleHangupKill(this);
            }

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

            // Also when the kill finds no child to end, so the master a disposal left open lasts no longer than the grace.
            UnwatchOutput();
            CloseMaster();
        }

        /// <summary>
        /// Makes the master's output wake <see cref="ChildWatcher"/>, so <see cref="DiscardOutputAfterHangup"/> keeps
        /// up with a child that writes while it handles the hang-up. Called only by <see cref="ChildWatcher"/>, under its
        /// lock, once it holds the hang-up.
        /// </summary>
        internal void WatchOutputAfterHangup()
        {
            if (!TryAcquireMaster(out var master))
                return;

            try
            {
                _outputWatched = ChildWatcher.TryWatchOutputLocked(master);
            }
            finally
            {
                ReleaseMaster();
            }
        }

        /// <summary>
        /// Undoes <see cref="WatchOutputAfterHangup"/> before the master closes. Called under the watcher's lock. A
        /// master that something else closed first is skipped, since its number may already belong to another
        /// descriptor; the close itself dropped a kqueue registration, and epoll drops one with the last descriptor.
        /// </summary>
        private void UnwatchOutput()
        {
            if (!_outputWatched)
                return;

            _outputWatched = false;
            if (!TryAcquireMaster(out var master))
                return;

            try
            {
                ChildWatcher.UnwatchOutputLocked(master);
            }
            finally
            {
                ReleaseMaster();
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

                // Through the pidfd where there is one (Linux 5.4 and later), so a pid that another child of the host
                // gets after the pidfd was opened never hands this session that child's exit.
                var reaped = UnixInterop.minipty_reap_child(_pidfd, _pid, 0, out var exitCode, out var termSignal);
                if (reaped == 0)
                    return false;

                if (reaped > 0)
                {
                    _exitCode = exitCode;
                    _termSignal = termSignal;
                }
                else if (Marshal.GetLastPInvokeError() != UnixInterop.ECHILD)
                {
                    return false;
                }

                // ECHILD: something outside MiniPty reaped the child, so its status is lost; report exit 0.
                Volatile.Write(ref _exited, true);
            }

            CloseMasterIfHungUp();
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

            CloseMasterIfHungUp();
            _exitSignal.TrySetExited(new PtyExitStatus(_exitCode, null));
        }

        /// <summary>
        /// Reads and discards what the child wrote since the last call, while the master stays open for a disposal's
        /// hang-up. The reader stopped at disposal, and without this a child that writes while it handles the hang-up
        /// would block on a full terminal until the kill. Called only by <see cref="ChildWatcher"/>, whenever the
        /// master's output (see <see cref="WatchOutputAfterHangup"/>) or the polling interval wakes it, and bounded per
        /// call so the watcher stays responsive.
        /// </summary>
        internal unsafe void DiscardOutputAfterHangup()
        {
            if (!TryAcquireMaster(out var master))
                return;

            try
            {
                Span<byte> buffer = stackalloc byte[4096];
                fixed (byte* ptr = buffer)
                {
                    for (var i = 0; i < 16; i++)
                    {
                        // Non-blocking: EAGAIN once nothing is left, EIO once every slave descriptor closed.
                        var read = UnixInterop.Read(master, ptr, (nuint)buffer.Length);
                        if (read > 0)
                            continue;
                        if (read < 0 && Marshal.GetLastPInvokeError() == UnixInterop.EINTR)
                            continue;

                        break;
                    }
                }
            }
            finally
            {
                ReleaseMaster();
            }
        }

        /// <summary>
        /// Closes the master that a disposal left open for its hang-up, once the child is gone. A session that was not
        /// disposed keeps its master for the output drain.
        /// </summary>
        private void CloseMasterIfHungUp()
        {
            // Dispose sets it under the pid lock. A reap that took the lock first sees zero, and Dispose then finds the
            // child exited and closes the master itself.
            if (Volatile.Read(ref _hangupKillDeadline) != 0)
            {
                UnwatchOutput();
                CloseMaster();
            }
        }

        /// <summary>
        /// Closes the pidfd once the child is reaped. Called only by <see cref="ChildWatcher"/>, after it removed the
        /// pidfd's registration.
        /// </summary>
        internal void ClosePidfd()
        {
            lock (_pidLock)
            {
                if (_pidfd < 0)
                    return;

                UnixInterop.close(_pidfd);
                _pidfd = -1;
            }
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

    [LibraryImport("minipty_unix")]
    private static unsafe partial int minipty_fork_pty_exec(
        int* master,
        Winsize* winp,
        byte* working_directory,
        byte* file,
        byte** argv,
        byte** envp,
        int* pid_out,
        int* pidfd_out,
        int* slave_out);

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
