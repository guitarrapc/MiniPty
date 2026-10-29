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

    /// <summary>A pid whose session's next loop service fails, for the test that one failure ends one session. 0: none.</summary>
    internal static int FailLoopReadsOfPidForTests;

    /// <summary>A pid whose session's master the loop refuses to register, for the test that this fails the start. 0: none.</summary>
    internal static int FailLoopRegistrationOfPidForTests;

    /// <summary>
    /// While set, the watcher's epoll or kqueue fails to open, for the test that a load recovers from a first open that
    /// failed. It affects only a load whose watcher has not opened them yet, so a test sets it in a load of MiniPty of
    /// its own.
    /// </summary>
    internal static bool FailWatchOpenForTests;

    /// <summary>Passes the watcher's loop has made, for tests that check it does not spin.</summary>
    internal static long LoopPassesForTests => ChildWatcher.Passes;

    /// <summary>
    /// Times a session's consumer started reading its master inline and joined the loop's list of inline readers, for
    /// tests of where a consumer reads.
    /// </summary>
    internal static long InlineReaderListingsForTests => ChildWatcher.InlineReaderListings;

    /// <summary>Waits consumers made on their own thread for a master's output (macOS), for tests of where a consumer waits.</summary>
    internal static long ConsumerWaitsForTests => Volatile.Read(ref UnixPtyBackendInstance.ConsumerWaits);

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
        var startStage = StartStageNone;
        int spawnError;
        unsafe
        {
            spawnError = ForkPtyExec(&master, &winsize, exec.WorkingDirectory, exec.Executable, exec.Argv, exec.Envp, &pid, &pidfd, &slave, &startStage);
        }

        // Freed as soon as the spawn returns. A forked child reads its own copy-on-write image of these strings, taken at
        // the fork, so the parent's copy is no longer needed; posix_spawn on macOS copies them before it returns. The
        // spawn also returns only once the child has exec'd or reported a failure.
        exec.Dispose();
        if (spawnError != 0)
            throw CreateStartException(startInfo, startStage, spawnError);

        try
        {
            PtyStartTestHook.Run(PtyStartStage.Spawned, pid);
            return new UnixPtyBackendInstance(master, slave, pid, pidfd, size);
        }
        catch
        {
            // The child runs, but no watcher will reap it, because the instance registers with the watcher last. It is
            // killed and reaped here, so a failed start leaves neither a running child, a zombie, nor its pidfd.
            _ = UnixInterop.minipty_signal_child(pidfd, pid, UnixInterop.SigKill);
            _ = UnixInterop.minipty_reap_child(pidfd, pid, 1, out _, out _);
            if (pidfd >= 0)
                UnixInterop.close(pidfd);

            if (master >= 0)
                UnixInterop.close(master);
            if (slave >= 0)
                UnixInterop.close(slave);
            throw;
        }
    }

    // The stage a child reports when it cannot start (minipty_unix_internal.h, MINIPTY_START_STAGE_*).
    private const int StartStageNone = 0;
    private const int StartStageChdir = 1;
    private const int StartStageExec = 2;

    /// <summary>
    /// The exception for a failed start. A child that could not enter its working directory or exec its program
    /// reported the <c>errno</c> before exiting, so the start fails as <c>CreateProcess</c> does on Windows and as
    /// <see cref="System.Diagnostics.Process.Start()"/> does on Unix: a <see cref="System.ComponentModel.Win32Exception"/>
    /// whose <see cref="System.ComponentModel.Win32Exception.NativeErrorCode"/> is that <c>errno</c>. Anything else failed
    /// before a child existed (opening the terminal, forking) and stays an <see cref="IOException"/>.
    /// </summary>
    private static Exception CreateStartException(PtyStartInfo startInfo, int startStage, int error) => startStage switch
    {
        StartStageChdir => new System.ComponentModel.Win32Exception(
            error,
            $"Cannot start '{startInfo.FileName}': working directory '{startInfo.WorkingDirectory}': {Marshal.GetPInvokeErrorMessage(error)}"),
        StartStageExec => new System.ComponentModel.Win32Exception(
            error,
            $"Cannot start '{startInfo.FileName}': {Marshal.GetPInvokeErrorMessage(error)}"),
        _ => new IOException($"PTY spawn failed (errno {error})"),
    };

    private static unsafe int ForkPtyExec(
        int* master,
        Winsize* winsize,
        IntPtr workingDirectory,
        IntPtr executable,
        IntPtr argv,
        IntPtr envp,
        int* pid,
        int* pidfd,
        int* slave,
        int* startStage) =>
        minipty_fork_pty_exec(
            master,
            winsize,
            (byte*)workingDirectory,
            (byte*)executable,
            (byte**)argv,
            (byte**)envp,
            pid,
            pidfd,
            slave,
            startStage);

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

    private sealed class UnixPtyBackendInstance : IPtyBackend, IPtyOutputDriver, IPtyInputQueue, IValueTaskSource<int>
    {
        // _loopPhase: how the watcher's loop reads this session, when it does.
        private const byte LoopNone = 0;
        private const byte LoopReading = 1;
        // The pump finished at disposal, and the master stays open for the hang-up: the loop reads and discards.
        private const byte LoopDiscarding = 2;
        private const byte LoopDone = 3;

        /// <summary>
        /// Reads one service makes for a session before the loop moves on: up to 16 KiB, so a flood shares the loop with
        /// every other session. A master with more is reported again on the next pass.
        /// </summary>
        private const int LoopReadsPerService = 4;

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

        // The watcher loop's state for this session, used only on its thread unless noted: the slot and token of its
        // registered master (the slot and its generation make the token), the interest registered, and the park on a full
        // ring.
        // _loopSlot and _loopPhase are also read by a consumer that arms the master (ArmForConsumer), and _loopInterest is
        // the interest last set by either. Arming needs no lock: see ArmForConsumer.
        private int _loopSlot = -1;
        private int _loopGeneration;
        private byte _loopInterest = ChildWatcher.InterestRead;
        private byte _loopPhase;
        private bool _loopParked;
        // Who reads the master for the session's consumer (ConsumerReads*), and when the consumer last read inline or was
        // handed output to read on from (Stopwatch ticks; written before the state), so the loop can take reading back
        // from a consumer that stopped.
        private int _consumerReads;
        private long _consumerReadStamp;

        // _consumerReads. Loop: the loop reads, and arms the master after each pass, as for any consumer.
        private const int ConsumerReadsLoop = 0;
        // Inline: the consumer reads the master on its own thread and arms it itself when it is about to wait, so the loop
        // does not arm it for output after a pass.
        private const int ConsumerReadsInline = 1;
        // Waiting: a consumer that was reading inline waits with the master armed. The pass that hands it output leaves the
        // master unarmed (Inline again), since that consumer reads on inline once it resumes.
        private const int ConsumerReadsWaiting = 2;

        /// <summary>What <see cref="CheckInlineReader"/> asks of the loop.</summary>
        internal const int InlineReaderKeep = 0;
        internal const int InlineReaderTakeOver = 1;
        internal const int InlineReaderUnlist = 2;

        // A consumer that has not read inline for this long is busy elsewhere (a UI frame, a send) or stopped: the loop
        // reads for it, as it does for any consumer, so the child is not held up and output reaches the ring.
        private static readonly long s_inlineReaderIdleTicks = System.Diagnostics.Stopwatch.Frequency / 1000;
        // A session whose consumer has not read inline for this long leaves the loop's list, so an idle session does not
        // keep the loop waking.
        private static readonly long s_inlineReaderUnlistTicks = System.Diagnostics.Stopwatch.Frequency / 100;
        // macOS: how long a consumer that found the master empty waits for it on its own thread before it parks on the loop
        // (WaitForOutputOnConsumer). During a flood the wait ends within microseconds; after a flood's last chunk it is paid
        // once. About the loop's own check of inline readers.
        private const int ConsumerWaitMs = 1;
        // macOS: the consumer's own kqueue with the master's read filter, opened on its first wait, closed with the master.
        private int _consumerWait = -1;
        // macOS: whether the child is streaming output, so a consumer that finds the master empty waits for it on its own
        // thread (TryReadInline). Set by a read that took the whole 1 KiB the master holds, which says the child was held up
        // and has more; cleared by a wait that brought nothing. Written by whichever thread read, under the producer's turn,
        // and by the consumer after its wait.
        private bool _outputStreams;
        // What a macOS master holds (xnu's TTYCLSIZE): a read of this many bytes emptied a full master.
        private const int MacOsMasterCapacity = 1024;

        /// <summary>Waits a consumer made on its own thread for a master's output (macOS), for tests of where it waits.</summary>
        internal static long ConsumerWaits;

        /// <summary>Whether this session is on the watcher's list of inline readers. Guarded by its lock.</summary>
        internal bool InlineReaderListed;

        /// <summary>The next session on the watcher's list of inline readers. Guarded by its lock.</summary>
        internal UnixPtyBackendInstance? NextInlineReader;

        /// <summary>Whether this session waits on the watcher's queue of sessions to look at. Guarded by its lock.</summary>
        internal bool ServiceQueued;

        /// <summary>The next session on the watcher's queue. Guarded by its lock.</summary>
        internal UnixPtyBackendInstance? NextService;

        public UnixPtyBackendInstance(int master, int slave, int pid, int pidfd, PtySize size)
        {
            _master = master;
            _slave = slave;
            _pid = pid;
            _pidfd = pidfd;
            _size = size;
            // Non-blocking, so a write takes what the tty input queue accepts and never blocks its caller. The loop
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

                // The caller's span does not outlive this call, so what is left is copied for the loop to write later.
                var owned = ArrayPool<byte>.Shared.Rent(bytes.Length);
                bytes.CopyTo(owned);
                entry = EnqueueLocked(owned.AsMemory(0, bytes.Length), owned);
            }

            WakeLoopForInput();
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

                WakeLoopForInput();
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

        // The loop may be watching the master for output only. This has it look at the session, write what it can, and
        // watch for the master taking input too.
        private void WakeLoopForInput() => Volatile.Read(ref _pump)?.WakeDriver();

        /// <summary>Writes queued input until the master is full again. Runs on the watcher's loop.</summary>
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

        public int OutputChunkLimit => PtyOutputPump.Capacity;

        public int PostExitQuietMs => PtyOutputPump.DefaultPostExitQuietMs;

        /// <summary>
        /// Reads output on the watcher's loop (epoll on Linux, kqueue on macOS and FreeBSD), which holds no thread of the
        /// session's own and also writes queued input when the master can take more. A master the loop cannot register
        /// fails the start: no other path reads the session.
        /// </summary>
        /// <remarks>
        /// The spawn opened the slave before this runs, which kqueue needs: before a slave first opens, it reports the
        /// master writable while writes fail with <c>EAGAIN</c>. Registration fails only when the watcher has no epoll or
        /// kqueue (it could not open one, or its wait failed while no master was registered) or the kernel refuses the
        /// descriptor, such as Linux at <c>fs.epoll.max_user_watches</c>.
        /// </remarks>
        public void StartOutput(PtyOutputPump pump)
        {
            if (!TryStartOnLoop(pump))
                throw new IOException("The PTY master could not be registered with MiniPty's I/O loop.");
        }

        /// <summary>Whether the watcher's loop reads this session.</summary>
        internal bool ReadsOnLoop => Volatile.Read(ref _loopPhase) != LoopNone;

        /// <summary>
        /// Whether the loop still reads this session's master, so it discards a hung-up child's output itself. Otherwise
        /// the watcher's separate discard path does: the loop stopped reading the master after a failure in its service,
        /// or never registered it because the start failed.
        /// </summary>
        internal bool DiscardsOnLoop => Volatile.Read(ref _loopPhase) is LoopReading or LoopDiscarding;

        private bool TryStartOnLoop(PtyOutputPump pump)
        {
            // The registration's own reference: the descriptor stays open, and its number unused, until it is removed.
            if (!TryAcquireMaster(out var master))
                return false;

            _loopPhase = LoopReading;
            pump.AttachDriver(this);
            if (FailLoopRegistrationOfPidForTests != _pid && ChildWatcher.TryRegisterMaster(this, master))
                return true;

            _loopPhase = LoopNone;
            ReleaseMaster();
            return false;
        }

        /// <summary>Records the master's registration. Called by <see cref="ChildWatcher"/> under its lock.</summary>
        internal void OnRegisteredOnLoop(int slot, int generation)
        {
            _loopSlot = slot;
            _loopGeneration = generation;
        }

        // IPtyOutputDriver, for a session the loop reads: every call has the loop look at the session.
        void IPtyOutputDriver.Wake(PtyOutputPump pump)
        {
            if (_loopPhase == LoopReading)
                ChildWatcher.RequestService(this);
        }

        void IPtyOutputDriver.CancelRead(PtyOutputPump pump)
        {
            if (_loopPhase == LoopReading)
                ChildWatcher.RequestService(this);
        }

        // The loop finishes the pump itself and already knows.
        void IPtyOutputDriver.OnPumpFinished(PtyOutputPump pump)
        {
        }

        /// <summary>
        /// Reads the master on the consumer's thread while the loop reads this session, and otherwise leaves output to the
        /// loop: a park on a full ring, disposal, the forced end, the close, end of output and read failures are the loop's
        /// to handle, so the consumer then waits for the loop's pass to end its wait.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The consumer takes the producer's turn for its reads (<see cref="PtyOutputPump.TryEnterProducer"/>). When it
        /// reads nothing, it waits: it arms the master's one-shot registration on its own thread, so the loop is woken only
        /// when output arrives for a consumer that waits. While the consumer reads, the loop does not arm the registration for
        /// output after its passes (<see cref="SetLoopInterest"/>), and the pass that hands a waiting consumer output leaves
        /// it unarmed too, since that consumer reads on inline. A flood that the consumer keeps up with then wakes the loop
        /// about once per wait, not once per read.
        /// </para>
        /// <para>
        /// A consumer that stops reading inline for about a millisecond, busy elsewhere or gone, has the loop read for it
        /// again (<see cref="CheckInlineReader"/>): the child is not held up, and its last output reaches the ring before the
        /// post-exit drain finds the master quiet and closes it.
        /// </para>
        /// <para>
        /// <b>macOS waits on the master here too.</b> A macOS master holds 1 KiB of output, and the child writes more only
        /// once it is read, so a consumer that keeps up finds the master empty after almost every read, before the child
        /// has refilled it. Parking then, as Linux does, made every chunk a wake of the loop and a hand-off back to the
        /// consumer, which is what cost macOS floods their speed (about 4 ms per MiB on an 8-core M2, and the slow runs on
        /// CI's 3-vCPU runner). So on macOS a consumer that reads nothing waits for the master on its own thread, on a
        /// kqueue of the session's own, for up to <see cref="ConsumerWaitMs"/>: the child's next write wakes it, and it
        /// reads again. Only a wait that times out arms the loop and parks. It waits only while the child streams
        /// (<see cref="_outputStreams"/>): a read that took the whole kilobyte the master holds says the child was held up
        /// and has more, and a wait that brings nothing says it has stopped. A flood's reads on the loop fill the master,
        /// and its reads on this thread, which wake on the first bytes, are often short but their waits pay off; a
        /// keystroke's echo or a line of a log never fills it, and waiting after those would hold a thread-pool thread
        /// for a millisecond per event that never comes. A consumer on a thread with a
        /// scheduling context (a UI thread) never waits here, as it never blocks. See
        /// <c>.github/docs/plans/plan_macos_flood.md</c> for the measurements. Windows does not read on the consumer's
        /// thread (<see cref="WindowsOutputDriver"/>), and FreeBSD reads inline as Linux does, unmeasured.
        /// </para>
        /// </remarks>
        bool IPtyOutputDriver.TryReadInline(PtyOutputPump pump)
        {
            if (Volatile.Read(ref _loopPhase) != LoopReading)
                return false;

            var published = TryReadInlineOnce(pump);
            if (!published && OperatingSystem.IsMacOS() && Volatile.Read(ref _outputStreams) && !PtyConsumerYield.HasSchedulingContext())
            {
                if (WaitForOutputOnConsumer())
                    published = TryReadInlineOnce(pump);

                // The wait timed out, or reported end of file: the child has stopped, until a full read says otherwise.
                if (!published)
                    Volatile.Write(ref _outputStreams, false);
            }

            if (published)
            {
                MarkReadingInline();
                return true;
            }

            // Nothing there now, or the loop holds the turn: the consumer waits, and the loop reads when the master reports.
            // A consumer that was reading inline goes on reading inline once it is handed output.
            Volatile.Write(ref _consumerReads,
                Volatile.Read(ref _consumerReads) == ConsumerReadsLoop ? ConsumerReadsLoop : ConsumerReadsWaiting);
            ArmForConsumer();
            return false;
        }

        /// <summary>One round of the consumer's reads under the producer's turn; whether any output was published.</summary>
        private bool TryReadInlineOnce(PtyOutputPump pump)
        {
            if (!pump.TryEnterProducer())
                return false;

            try
            {
                return ReadInline(pump);
            }
            finally
            {
                if (pump.ExitProducer())
                    ChildWatcher.RequestService(this);
            }
        }

        /// <summary>
        /// Waits on the consumer's thread until the master has output, up to <see cref="ConsumerWaitMs"/>, on a kqueue of
        /// the session's own that is opened on the first wait and closes with the master (macOS). Returns whether the
        /// master reported; a report may be end of file, which the read that follows tells apart. The wait holds a
        /// reference on the master, so a close that races it completes once the wait returns.
        /// </summary>
        /// <remarks>
        /// No retry before the wait: the child refills the master within about 10 μs on an idle machine, but retrying the
        /// read for that long, yielding between tries, took the M2's floods from 250 to 72 MiB/s and a loaded core's from
        /// 85 to 27, since the retries took the CPU the child needed to refill (plan_macos_flood.md). The wait alone matched
        /// or beat a blocking reader on every load.
        /// </remarks>
        private bool WaitForOutputOnConsumer()
        {
            if (!TryAcquireMaster(out var master))
                return false;

            try
            {
                var wait = _consumerWait;
                if (wait < 0)
                {
                    wait = UnixInterop.minipty_master_wait_open(master);
                    if (wait < 0)
                        return false;

                    Volatile.Write(ref _consumerWait, wait);
                }

                Interlocked.Increment(ref ConsumerWaits);
                return UnixInterop.minipty_master_wait(wait, ConsumerWaitMs) > 0;
            }
            finally
            {
                ReleaseMaster();
            }
        }

        /// <summary>
        /// The consumer reads inline from now on: stamped, and listed with the loop, which takes reading back from a consumer
        /// that stops (<see cref="CheckInlineReader"/>).
        /// </summary>
        private void MarkReadingInline()
        {
            Volatile.Write(ref _consumerReadStamp, System.Diagnostics.Stopwatch.GetTimestamp());
            if (Volatile.Read(ref _consumerReads) == ConsumerReadsInline)
                return;

            Volatile.Write(ref _consumerReads, ConsumerReadsInline);
            if (!Volatile.Read(ref InlineReaderListed))
                ChildWatcher.WatchInlineReader(this);
        }

        void IPtyOutputDriver.HandReadingBack(PtyOutputPump pump)
        {
            if (Volatile.Read(ref _consumerReads) == ConsumerReadsLoop)
                return;

            // The consumer kept the ring between its reads; an idle session holds none. While the loop holds the turn, its
            // pass lets go of it.
            if (pump.TryEnterProducer())
            {
                pump.LetGoOfRing();
                if (pump.ExitProducer())
                    ChildWatcher.RequestService(this);
            }

            Volatile.Write(ref _consumerReads, ConsumerReadsLoop);
            ArmForConsumer();
        }

        /// <summary>
        /// The loop's look at this session on its list of inline readers, under the watcher's lock: takes reading back from
        /// a consumer that has not read inline for about a millisecond, and leaves the list once the consumer has not read
        /// inline for longer, or output ended. A waiting consumer has the master armed and needs nothing.
        /// </summary>
        internal int CheckInlineReader(long now)
        {
            if (_loopPhase != LoopReading)
                return InlineReaderUnlist;

            var idle = now - Volatile.Read(ref _consumerReadStamp);
            if (Volatile.Read(ref _consumerReads) != ConsumerReadsInline)
                return idle < s_inlineReaderUnlistTicks ? InlineReaderKeep : InlineReaderUnlist;

            if (idle < s_inlineReaderIdleTicks)
                return InlineReaderKeep;

            // A consumer that reads again meanwhile takes the turn back by its next read.
            return Interlocked.CompareExchange(ref _consumerReads, ConsumerReadsLoop, ConsumerReadsInline) == ConsumerReadsInline
                ? InlineReaderTakeOver
                : InlineReaderKeep;
        }

        /// <summary>The loop reads for a consumer that stopped reading inline: arms the master for output. On the loop.</summary>
        internal void TakeOverReading() => SetLoopInterest();

        /// <summary>
        /// The consumer's reads, under the producer's turn: up to <see cref="LoopReadsPerService"/> reads while each fills
        /// its span, each published as it completes. Returns whether any output was published. A ring that nothing was read
        /// into is let go of; one with output keeps for the consumer's next read.
        /// </summary>
        private unsafe bool ReadInline(PtyOutputPump pump)
        {
            // Under the turn the loop's own state holds still.
            if (_loopPhase != LoopReading || _loopParked || pump.IsSessionDisposed || pump.IsEndOfOutputForced
                || Volatile.Read(ref _disposed) || !TryAcquireMaster(out var master))
            {
                return false;
            }

            var published = false;
            try
            {
                for (var reads = 0; reads < LoopReadsPerService && !pump.IsFull; reads++)
                {
                    pump.HoldRing();
                    var span = pump.NextReadSpan;
                    int read;
                    fixed (byte* ptr = span)
                        read = ReadMasterNow(master, ptr, span.Length);

                    // Nothing more for now, or end of output, which the loop sees when it reads the master again.
                    if (read <= 0)
                        break;

                    var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    pump.OnOutputRead();
                    pump.Publish(read, timestamp);
                    published = true;
                    if (read >= MacOsMasterCapacity)
                        _outputStreams = true;
                    // A short read took what the terminal held; another would most likely find nothing.
                    if (read < span.Length)
                        break;
                }
            }
            catch (IOException)
            {
                // The loop reads the master again and ends the session's output with the failure.
            }
            finally
            {
                ReleaseMaster();
                if (!published)
                    pump.LetGoOfRing();
            }

            return published;
        }

        /// <summary>
        /// Arms the master's registration from the consumer's thread, which is about to wait or has stopped reading: the
        /// loop is then woken by the next output. Queued input keeps its write interest.
        /// </summary>
        /// <remarks>
        /// <para>
        /// No lock: under one, the loop's arming after each pass and this one contended on every wait, and a flood on two
        /// CPUs took about 12 contentions per MiB. Each side sets the whole interest, so the arms may land in either order.
        /// The loop arms without output only while it is parked on a full ring, when the consumer has output to take and
        /// does not wait. Queued input is the other case: input queued after the check below has its own service request,
        /// which the loop serves after this arm, and input queued between the check and the arm is caught by the second
        /// check, which has the loop arm again.
        /// </para>
        /// <para>
        /// A removal racing this arm is harmless: the reference keeps the descriptor, and its number, this session's; epoll
        /// fails the arm of a removed descriptor, and a kqueue filter it adds back reports at most once (EV_DISPATCH), with a
        /// token whose slot generation the removal retired, so the loop drops the report.
        /// </para>
        /// </remarks>
        private void ArmForConsumer()
        {
            if (!TryAcquireMaster(out _))
                return;

            try
            {
                // Removed from the loop, or no longer read for a consumer: the loop's end of output ends the wait.
                var slot = Volatile.Read(ref _loopSlot);
                if (slot < 0 || Volatile.Read(ref _loopPhase) != LoopReading)
                    return;

                var inputQueued = Volatile.Read(ref _inputHead) is not null;
                SetInterest(slot, ChildWatcher.InterestRead | (inputQueued ? ChildWatcher.InterestWrite : 0));
                if (!inputQueued && Volatile.Read(ref _inputHead) is not null)
                    ChildWatcher.RequestService(this);
            }
            finally
            {
                ReleaseMaster();
            }
        }

        /// <summary>
        /// The pass handed output to a consumer that waits after reading inline: that consumer reads on inline once it
        /// resumes, so the master stays unarmed, and the loop takes reading back if it does not resume within about a
        /// millisecond.
        /// </summary>
        private void ResumeInlineReading(PtyOutputPump pump)
        {
            if (Volatile.Read(ref _consumerReads) != ConsumerReadsWaiting || !pump.HasUnreadOutput)
                return;

            Volatile.Write(ref _consumerReadStamp, System.Diagnostics.Stopwatch.GetTimestamp());
            if (Interlocked.CompareExchange(ref _consumerReads, ConsumerReadsInline, ConsumerReadsWaiting) == ConsumerReadsWaiting
                && !Volatile.Read(ref InlineReaderListed))
            {
                ChildWatcher.WatchInlineReader(this);
            }
        }

        /// <summary>
        /// Serves this session on the loop: after a readiness <paramref name="report"/> from the master, or 0 when the
        /// session asked for a look. Writes queued input, reads output into the ring and publishes each read, within the
        /// per-service bound, parks on a full ring, and ends output. Never throws: a failure ends this session's output
        /// with it, and the loop goes on.
        /// </summary>
        /// <remarks>
        /// The pass takes the producer's turn, which a consumer reading inline may hold for its reads. A report of output
        /// alone is then dropped: that consumer reads the output itself and arms the master again before it waits. Anything
        /// else is owed, and the consumer has the loop look again when it lets go of the turn.
        /// </remarks>
        internal void ServiceOnLoop(int report)
        {
            var pump = Volatile.Read(ref _pump);
            if (pump is not null)
            {
                var outputOnly = report != 0
                    && (report & ~(ChildWatcher.ReportReadable | ChildWatcher.ReportHangup)) == 0
                    && _loopPhase == LoopReading;
                if (!(outputOnly ? pump.TryEnterProducer() : pump.TryEnterProducerOrDefer()))
                    return;
            }

            try
            {
                ServeOnLoop(report);
            }
            catch (Exception ex)
            {
                FailOnLoop(ex);
            }
            finally
            {
                // The loop's own turn is never deferred.
                pump?.ExitProducer();
            }
        }

        private void ServeOnLoop(int report)
        {
            if (_loopPhase == LoopDone)
                return;

            var pump = _pump!;
            if (_loopPhase == LoopReading && (Volatile.Read(ref _disposed) || pump.IsSessionDisposed))
            {
                // Disposal ends reading at once. A master that stays open for the hang-up is read and discarded below
                // until the child is gone.
                FinishOnLoop(pump, null);
            }

            if (_loopPhase == LoopDiscarding)
            {
                DiscardOnLoop(report);
                return;
            }

            if (_loopPhase != LoopReading)
                return;

            if (FailLoopReadsOfPidForTests != 0 && FailLoopReadsOfPidForTests == _pid)
                throw new IOException("Injected read failure");

            if ((report & (ChildWatcher.ReportWritable | ChildWatcher.ReportHangup | ChildWatcher.ReportError)) != 0
                || (report == 0 && HasPendingInput))
            {
                FlushInput();
            }

            pump.DeliverBacklogToSink();
            // A full ring after the close is not end of output either: the session stays parked until the consumer frees
            // space.
            if (ParkWhileFull(pump))
            {
                SetLoopInterest();
                return;
            }

            if (IsMasterClosed)
            {
                // The close (the post-exit drain, or disposal after the exit) ends output; what the master still holds is
                // left unread.
                EndOnLoop(pump);
                return;
            }

            if ((report & (ChildWatcher.ReportReadable | ChildWatcher.ReportHangup | ChildWatcher.ReportError)) != 0
                || pump.IsEndOfOutputForced)
            {
                if (!ReadOnLoop(pump, out var foundNothing))
                    return;

                if (foundNothing)
                    RenewIfHangupIsStale(report);

                ParkWhileFull(pump);
            }

            ResumeInlineReading(pump);
            pump.LetGoOfRing();
            SetLoopInterest();
        }

        /// <summary>
        /// The free-space handshake on the loop: parks the session while the ring is full and no sink drains it, and ends
        /// a park once there is room. Returns whether the session is parked. Read interest follows the park.
        /// </summary>
        private bool ParkWhileFull(PtyOutputPump pump)
        {
            if (pump.IsFull)
            {
                if (_loopParked)
                    return true;

                if (pump.BeginWaitForFreeSpace())
                {
                    _loopParked = true;
                    return true;
                }

                pump.EndWaitForFreeSpace();
                return false;
            }

            if (_loopParked)
            {
                // Room again. Read interest comes back, and the master reports what it holds on the next pass.
                _loopParked = false;
                pump.EndWaitForFreeSpace();
            }

            return false;
        }

        /// <summary>
        /// Reads what the master holds into the ring, up to <see cref="LoopReadsPerService"/> reads, and publishes each read
        /// as it completes. There is no coalescing window: a consumer still busy with its previous chunk takes everything
        /// that arrived meanwhile as one chunk, the policy the Windows driver has too. Returns <see langword="false"/> once
        /// output ended. <paramref name="foundNothing"/> is set when the first read found nothing to read.
        /// </summary>
        private unsafe bool ReadOnLoop(PtyOutputPump pump, out bool foundNothing)
        {
            foundNothing = false;
            if (!TryAcquireMaster(out var master))
            {
                EndOnLoop(pump);
                return false;
            }

            try
            {
                for (var reads = 0; reads < LoopReadsPerService; reads++)
                {
                    if (pump.IsFull)
                        break;

                    pump.HoldRing();
                    var span = pump.NextReadSpan;
                    int read;
                    fixed (byte* ptr = span)
                        read = ReadMasterNow(master, ptr, span.Length);

                    if (read < 0)
                    {
                        foundNothing = reads == 0;
                        break;
                    }

                    if (read == 0)
                    {
                        EndOnLoop(pump);
                        return false;
                    }

                    var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    pump.OnOutputRead();
                    pump.Publish(read, timestamp);
                    if (read >= MacOsMasterCapacity)
                        _outputStreams = true;
                    // A macOS master hands out at most the 1 KiB it holds, so a short read emptied it, and a read after it
                    // would find nothing: one system call per chunk fewer. What the child writes before the next arm is
                    // reported by the next wait. Linux hands out what it has in 4 KiB reads, and a short read there can
                    // precede more.
                    if (read < span.Length && OperatingSystem.IsMacOS())
                        break;
                }
            }
            finally
            {
                ReleaseMaster();
            }

            if (pump.IsEndOfOutputForced)
            {
                // The drain gave up on the transport ending; what the master held is in.
                EndOnLoop(pump);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Registers the master again when a hang-up report found nothing to read. kqueue keeps reporting <c>EV_EOF</c> on a
        /// registration after the slave is opened again, while reads fail with <c>EAGAIN</c>, and only a new registration
        /// clears it (the readiness probe, in <c>.github/docs/references/pty_crossplatform.md</c>). Output ends on
        /// a read that returns 0, never on the report alone, so without this such a master would spin the loop. MiniPty
        /// never opens a slave again, but another process can. epoll never reports a stale hang-up; there this only
        /// modifies the registration.
        /// </summary>
        private void RenewIfHangupIsStale(int report)
        {
            if ((report & ChildWatcher.ReportHangup) == 0 || _loopSlot < 0)
                return;

            ChildWatcher.RenewInterest(_master, _loopSlot, _loopGeneration, _loopInterest);
        }

        /// <summary>
        /// Arms the master for what the session's state needs, after every pass, since a registration reports once:
        /// output while the ring has room, input while some is queued. While the consumer reads inline, output is left to
        /// it: it arms the master itself before it waits, and arming it here would wake the loop for output the consumer
        /// is reading. On the loop, which applies the arm with its next wait.
        /// </summary>
        private void SetLoopInterest()
        {
            if (_loopSlot < 0)
                return;

            var read = _loopPhase == LoopDiscarding || (_loopPhase == LoopReading && !_loopParked);
            var write = _loopPhase == LoopReading && Volatile.Read(ref _inputHead) is not null;
            if (read && !write && _loopPhase == LoopReading && Volatile.Read(ref _consumerReads) == ConsumerReadsInline)
                return;

            var interest = (read ? ChildWatcher.InterestRead : 0) | (write ? ChildWatcher.InterestWrite : 0);
            if (interest == 0 && _loopInterest == 0)
                return;

            SetInterestOnLoop(_loopSlot, interest);
        }

        /// <summary>
        /// Sets and arms the master's interest in its registration at <paramref name="slot"/>, from a consumer
        /// (<see cref="ArmForConsumer"/>), at once.
        /// </summary>
        private void SetInterest(int slot, int interest)
        {
            _loopInterest = (byte)interest;
            ChildWatcher.SetInterest(_master, slot, _loopGeneration, interest);
        }

        /// <summary>
        /// Sets the master's interest from the loop's pass; the loop's next wait arms it, in one system call with the
        /// wait on kqueue (<see cref="ChildWatcher.SetInterestOnLoop"/>). On the loop.
        /// </summary>
        private void SetInterestOnLoop(int slot, int interest)
        {
            _loopInterest = (byte)interest;
            ChildWatcher.SetInterestOnLoop(_master, slot, _loopGeneration, interest);
        }

        /// <summary>End of output on the loop: the master is removed first, so a hung-up master cannot report forever.</summary>
        private void EndOnLoop(PtyOutputPump pump)
        {
            DeregisterFromLoop();
            if (_loopPhase == LoopReading)
                FinishOnLoop(pump, null);

            _loopPhase = LoopDone;
        }

        /// <summary>
        /// Finishes the pump from the loop. After disposal with the master still open for the hang-up, the session goes on
        /// to discard what the child writes until the master closes; otherwise it is done.
        /// </summary>
        private void FinishOnLoop(PtyOutputPump pump, Exception? error)
        {
            if (_loopParked)
            {
                _loopParked = false;
                pump.EndWaitForFreeSpace();
            }

            // Queued input is written by this loop; once it stops reading the session, nothing can deliver it. A disposal fails
            // it as disposed, also when the loop sees the disposal on the pump before the backend's own close of the input,
            // which would otherwise find the queue already failed with the end of output.
            CloseInput(pump.IsSessionDisposed || Volatile.Read(ref _disposed) ? InputDisposed : InputOutputEnded);
            // The pump's flag, not the backend's: session disposal marks the pump first and hangs the child up only after,
            // and a session ended between the two would leave the hung-up child's output to nobody.
            _loopPhase = _loopSlot >= 0 && (pump.IsSessionDisposed || Volatile.Read(ref _disposed)) && !IsMasterClosed
                ? LoopDiscarding
                : LoopDone;
            pump.Finish(error);
            if (_loopPhase == LoopDone)
                DeregisterFromLoop();
            else
                SetLoopInterest();
        }

        /// <summary>
        /// Reads and discards what a hung-up child writes while its master stays open after disposal, bounded per service,
        /// until the master closes or reports end of output.
        /// </summary>
        private unsafe void DiscardOnLoop(int report)
        {
            if (IsMasterClosed || !TryAcquireMaster(out var master))
            {
                DeregisterFromLoop();
                _loopPhase = LoopDone;
                return;
            }

            try
            {
                Span<byte> buffer = stackalloc byte[4096];
                fixed (byte* ptr = buffer)
                {
                    for (var reads = 0; reads < LoopReadsPerService; reads++)
                    {
                        var read = ReadMasterNow(master, ptr, buffer.Length);
                        if (read > 0)
                            continue;

                        if (read == 0)
                        {
                            DeregisterFromLoop();
                            _loopPhase = LoopDone;
                        }
                        else if (reads == 0)
                        {
                            RenewIfHangupIsStale(report);
                        }

                        break;
                    }
                }
            }
            finally
            {
                ReleaseMaster();
            }

            // The registration reported once; it reports the child's next output only once armed again.
            if (_loopPhase == LoopDiscarding)
                SetLoopInterest();
        }

        /// <summary>A failure in this session's work ends its output with the failure; the loop and other sessions go on.</summary>
        private void FailOnLoop(Exception error)
        {
            try
            {
                if (_loopPhase == LoopReading)
                {
                    _loopPhase = LoopDone;
                    if (_loopParked)
                    {
                        _loopParked = false;
                        _pump!.EndWaitForFreeSpace();
                    }

                    CloseInput(InputOutputEnded);
                    _pump!.Finish(error);
                }
            }
            catch (Exception)
            {
                // Finish never throws; nothing else is left to report.
            }

            _loopPhase = LoopDone;
            DeregisterFromLoop();
        }

        /// <summary>Removes the master from the loop, while it is still open, and drops the registration's reference.</summary>
        private void DeregisterFromLoop()
        {
            var slot = _loopSlot;
            if (slot < 0)
                return;

            // Before the removal, so a consumer that arms the master from now on sees it gone (ArmForConsumer).
            Volatile.Write(ref _loopSlot, -1);
            ChildWatcher.DeregisterMaster(_master, slot);
            ReleaseMaster();
        }

        /// <summary>
        /// One non-blocking read of the master, which the caller has pinned. Returns the byte count, 0 at end of output
        /// (EIO once every slave descriptor closed), or -1 when nothing is there.
        /// </summary>
        private static unsafe int ReadMasterNow(int master, byte* buffer, int length)
        {
            while (true)
            {
                var read = UnixInterop.Read(master, buffer, (nuint)length);
                if (read >= 0)
                    return read;

                var errno = Marshal.GetLastPInvokeError();
                if (errno == UnixInterop.EINTR)
                    continue;
                if (errno == UnixInterop.EAGAIN)
                    return -1;
                if (errno is 0 or UnixInterop.EIO or UnixInterop.EBADF)
                    return 0;

                throw new IOException($"read failed (errno {errno})");
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
            // The last reference after the close closes the descriptor, and the slave's with it. The master goes first,
            // so its close hangs the terminal up as before, also for a child held in its exit until the master is read.
            if (Interlocked.Add(ref _masterState, -MasterReference) == MasterClosedBit)
            {
                UnixInterop.close(_master);
                if (_slave >= 0)
                    UnixInterop.close(_slave);
                // No consumer waits on it: a wait holds a reference, so this is after the last one returned.
                var wait = Volatile.Read(ref _consumerWait);
                if (wait >= 0)
                    UnixInterop.close(wait);
            }
        }

        /// <summary>
        /// Stops new uses of the master and drops the owner's reference. The descriptor closes now, or when the loop's
        /// registration and any in-flight call release theirs, so its number is never reused while someone still uses it.
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
                    // The loop's registration still holds the descriptor open; the loop removes it, and the close completes.
                    if (ReadsOnLoop)
                        ChildWatcher.RequestService(this);

                    return;
                }

                state = seen;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // Read by the loop, which stops reading on it while the master stays open for a hung-up child.
            Volatile.Write(ref _disposed, true);
            // Queued writes fail now, on the disposing thread, without waiting for the loop to stop. This comes before
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
                // it saves its history dies before it hangs up its jobs. Output ends now all the same, and the watcher
                // discards what the child writes meanwhile.
                SendEotIfPending();
                // The watcher kills a child that ignores the hang-up and reaps it either way, so disposal never waits.
                ChildWatcher.ScheduleHangupKill(this);
            }
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
        /// hang-up and the loop no longer reads it (see <see cref="DiscardsOnLoop"/>). Without this a child that writes
        /// while it handles the hang-up would block on a full terminal until the kill. Called only by
        /// <see cref="ChildWatcher"/>, whenever the master's output (see <see cref="WatchOutputAfterHangup"/>) or the
        /// polling interval wakes it, and bounded per call so the watcher stays responsive.
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
        int* slave_out,
        int* start_stage_out);

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
