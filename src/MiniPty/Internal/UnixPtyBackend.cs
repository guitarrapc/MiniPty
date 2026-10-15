using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks.Sources;

namespace MiniPty.Internal;

internal static partial class UnixPtyBackend
{
    private const byte InputEot = 0x04;
    // EOF staging advances one step per tick, matching the cadence of the exit-wait poll loop it replaced.
    private const int EofTickMs = 100;
    // macOS posix_spawn children can still be running short -c scripts when the first EOF tick fires.
    // Sending EOT before they finish leaves /bin/sh waiting on stdin and never exiting.
    private const int MacOsEofAttachDeferPolls = 4;
    // How long a read keeps appending output that arrives right after it to the same ring slot.
    private const int CoalesceMicroWindowMs = 1;

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

    private sealed class UnixPtyBackendInstance : IPtyBackend, IValueTaskSource<int>, IPtyMasterLease
    {
        // _masterState: bit 0 is set once the master is closed to new users; the remaining bits count references
        // in units of MasterReference. The owner holds one reference from spawn until the transport closes.
        private const int MasterClosedBit = 1;
        private const int MasterReference = 2;

        private readonly int _master;
        private readonly int _pid;
        private readonly UnixExecPayload? _execHold;
        private readonly InputTrackingWriteStream _inputStream;
        // Serializes the child's reap with signal delivery, so Kill can never reach a pid the watcher has
        // already released for reuse.
        private readonly Lock _pidLock = new();
        // Mutable struct: used only through this field, never copied.
        private PtyExitSignal _exitSignal;
        private Timer? _eofTimer;
        private bool _eofSent;
        private bool _eofPending;
        private int _eofAttachPollsRemaining;
        private bool _eotLineSubmitSent;
        private bool _inputWritten;
        private bool _inputEndsWithNewline;
        private int _masterState = MasterReference;
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
            _inputStream = new InputTrackingWriteStream(new PtyFdWriteStream(this), OnInputWritten);
            Input = _inputStream;
            // Last, so the watcher never observes a partially constructed instance.
            ChildWatcher.Register(this);
        }

        public Stream Input { get; }
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
                columns = Math.Clamp(columns, 1, 512);
                rows = Math.Clamp(rows, 1, 512);
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
        // closes the master only when a grandchild keeps the slave open.
        public void CloseOutputTransport() => CloseMaster();

        public bool IsOutputClosed => IsMasterClosed;

        public bool TryBeginOutputRead() => TryAcquireMaster(out _);

        public void EndOutputRead() => ReleaseMaster();

        /// <summary>
        /// Waits on the master and the reader's wake descriptor together, then reads. Closing the master does not
        /// interrupt a read blocked on it in another thread, so a close or disposal wakes the reader instead, and
        /// the reader's reference keeps the descriptor number from being reused under it.
        /// </summary>
        public int ReadOutput(Span<byte> buffer, PtyReaderThread reader)
        {
            while (true)
            {
                if (IsMasterClosed)
                    return 0;

                var ready = UnixInterop.minipty_output_wait(_master, reader.WakeFd, Timeout.Infinite);
                if (ready < 0)
                    throw new IOException($"Waiting for PTY output failed (errno {Marshal.GetLastPInvokeError()})");

                if ((ready & UnixInterop.OutputWoken) != 0)
                {
                    reader.ConsumeWake();
                    return -1;
                }

                if ((ready & UnixInterop.OutputReady) != 0)
                    return ReadMaster(buffer);
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

            var ready = UnixInterop.minipty_output_wait(_master, reader.WakeFd, CoalesceMicroWindowMs);
            if (ready <= 0 || (ready & UnixInterop.OutputReady) == 0)
                return 0;

            var read = ReadMaster(buffer);
            endOfOutput = read == 0;
            return read;
        }

        /// <summary>Reads after the master reported ready, so the read does not block. Returns 0 at end of output.</summary>
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

                    // EIO: every slave descriptor closed (Linux reports end of output this way).
                    if (errno is 0 or UnixInterop.EIO or UnixInterop.EBADF)
                        return 0;

                    throw new IOException($"read failed (errno {errno})");
                }
            }
        }

        private bool IsMasterClosed => (Volatile.Read(ref _masterState) & MasterClosedBit) != 0;

        /// <summary>Pins the master for one use. Fails once it is closed; pair a success with <see cref="ReleaseMaster"/>.</summary>
        public bool TryAcquireMaster(out int master)
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

        public void ReleaseMaster()
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
            var exitObserved = true;
            lock (_pidLock)
            {
                // The watcher reaps the killed child, so disposal never waits for it on the caller's thread.
                if (!_exited)
                {
                    exitObserved = false;
                    KillCore();
                }
            }

            _eofTimer?.Dispose();
            if (!exitObserved)
                _exitSignal.SetDisposedBeforeExit();

            CloseTransport();
            _execHold?.Dispose();
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

            if (!TryAcquireMaster(out var master))
                return;

            try
            {
                DrainMasterOutputBeforeEot(master);

                if (IsExitObserved)
                {
                    CompleteEotSignaling();
                    return;
                }

                // Canonical line discipline: one EOT on a non-empty buffer submits the line but
                // does not signal EOF; a second EOT on the empty buffer ends input for programs like cat.
                // Stage the two EOT writes on separate ticks so the child can attach and consume the submit.
                if (_inputWritten && !_inputEndsWithNewline && !_eotLineSubmitSent)
                {
                    if (TryWriteEotByte(master))
                        _eotLineSubmitSent = true;
                    else
                        CompleteEotSignaling();
                    return;
                }

                TryWriteEotByte(master);
                CompleteEotSignaling();
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

        /// <summary>
        /// Writes one EOT byte. Returns <see langword="false"/> when the slave is gone (EIO/EPIPE), which is benign after exit.
        /// </summary>
        private static unsafe bool TryWriteEotByte(int master)
        {
            Span<byte> eot = stackalloc byte[1] { InputEot };
            fixed (byte* ptr = eot)
            {
                while (true)
                {
                    var written = UnixInterop.Write(master, ptr, 1);
                    if (written == 1)
                        return true;
                    if (written < 0)
                    {
                        switch (Marshal.GetLastPInvokeError())
                        {
                            case UnixInterop.EINTR:
                                continue;
                            case UnixInterop.EIO:
                            case UnixInterop.EPIPE:
                                // The slave closed: the child exited or is exiting, and the watcher will reap it.
                                return false;
                            default:
                                throw new IOException($"write failed (errno {Marshal.GetLastPInvokeError()})");
                        }
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
