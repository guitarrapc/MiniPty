using System.Runtime.InteropServices;
using System.Text;

namespace MiniPty.Internal;

internal static partial class UnixPtyBackend
{
    private const byte InputEot = 0x04;
    private const int WaitPollMs = 100;
    private const int ReapPollMs = 10;
    private const int ReapDeadlineMs = 1_000;
    // macOS posix_spawn children can still be running short -c scripts when the first wait poll ends.
    // Sending EOT before they finish leaves /bin/sh waiting on stdin and never exiting.
    private const int MacOsEofAttachDeferPolls = 4;

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

    private sealed class UnixPtyBackendInstance : IPtyBackend
    {
        private readonly int _master;
        private readonly int _pid;
        private readonly UnixExecPayload? _execHold;
        private readonly InputTrackingWriteStream _inputStream;
        private bool _eofSent;
        private bool _eofPending;
        private int _eofAttachPollsRemaining;
        private bool _eotLineSubmitSent;
        private bool _inputWritten;
        private bool _inputEndsWithNewline;
        private bool _masterClosed;
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
            _inputStream = new InputTrackingWriteStream(new PtyFdWriteStream(master), OnInputWritten);
            Input = _inputStream;
            Output = new PtyFdReadStream(master);
        }

        public Stream Input { get; }
        public Stream Output { get; }
        public int ProcessId => _pid;

        public bool HasExited
        {
            get
            {
                TryRefreshExitState();
                return _exited;
            }
        }

        public int ExitCode
        {
            get
            {
                TryRefreshExitState();
                return _exitCode;
            }
        }

        public int? ExitSignal
        {
            get
            {
                TryRefreshExitState();
                return _exited && _termSignal != 0 ? _termSignal : null;
            }
        }

        public PtySize Size => _size;

        public string? ActiveProcessName
        {
            get
            {
                if (_disposed || _masterClosed || TryRefreshExitState())
                    return null;

                Span<byte> name = stackalloc byte[256];
                int length;
                unsafe
                {
                    fixed (byte* buffer = name)
                        length = minipty_get_active_process_name(_master, buffer, name.Length);
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
            if (_masterClosed)
                throw new InvalidOperationException("Cannot resize after the PTY has been closed.");

            columns = Math.Clamp(columns, 1, 512);
            rows = Math.Clamp(rows, 1, 512);
            if (minipty_set_winsize(
                    _master,
                    (ushort)rows,
                    (ushort)columns,
                    (ushort)pixelWidth,
                    (ushort)pixelHeight) != 0)
                throw new IOException($"TIOCSWINSZ failed (errno {Marshal.GetLastPInvokeError()})");

            _size = new PtySize(columns, rows);
        }

        public void SendEof()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryRefreshExitState() || _eofSent || _eofPending)
                return;

            // Defer EOT until the wait loop gives the child time to attach (same attach race as ConPTY).
            _eofPending = true;
            if (OperatingSystem.IsMacOS())
                _eofAttachPollsRemaining = MacOsEofAttachDeferPolls;
        }

        public void Kill()
        {
            if (_disposed || TryRefreshExitState())
                return;

            KillCore();
        }

        public void Kill(PtySignal signal)
        {
            // Validate before the disposed/exited guards so misuse always throws.
            var nativeSignal = MapSignal(signal);
            if (_disposed || TryRefreshExitState())
                return;

            // Fire-and-forget like KillCore; ESRCH after a racing exit is benign.
            UnixInterop.kill(_pid, nativeSignal);
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

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken, bool killOnCancellation, bool closeTransportOnExit = true) =>
            WaitForExitCoreAsync(cancellationToken, killOnCancellation);

        public void PollForChildExitUntilExited(CancellationToken cancellationToken, bool closeTransportOnExit)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryRefreshExitState())
                return;

            while (!TryRefreshExitState())
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();

                if (PollForChildExit(WaitPollMs, cancellationToken))
                    break;

                SendEotIfPending();
            }

            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private async Task<int> WaitForExitCoreAsync(CancellationToken cancellationToken, bool killOnCancellation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryRefreshExitState())
                return _exitCode;

            CancellationTokenRegistration registration = default;
            if (killOnCancellation)
            {
                registration = cancellationToken.Register(static state =>
                {
                    var session = (UnixPtyBackendInstance)state!;
                    session.Kill();
                }, this);
            }

            try
            {
                while (!TryRefreshExitState())
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (PollForChildExit(WaitPollMs, cancellationToken))
                        break;

                    SendEotIfPending();

                    await Task.Yield();
                }

                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _exitCode;
            }
            finally
            {
                registration.Dispose();
            }
        }

        public void CloseOutputTransport()
        {
            if (_masterClosed)
                return;

            UnixInterop.close(_master);
            _masterClosed = true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (!TryRefreshExitState())
            {
                KillCore();
                TryReapChild();
            }

            CloseTransport();
            _execHold?.Dispose();
        }

        private void TryReapChild()
        {
            var deadline = Environment.TickCount64 + ReapDeadlineMs;
            while (Environment.TickCount64 < deadline)
            {
                if (TryRefreshExitState())
                    return;

                Thread.Sleep(ReapPollMs);
            }
        }

        private bool TryRefreshExitState()
        {
            if (_exited)
                return true;

            if (!TryWaitPid(_pid, UnixInterop.WaitNoHang, out var status, out var result))
            {
                if (Marshal.GetLastPInvokeError() == UnixInterop.ECHILD)
                {
                    _exited = true;
                    return true;
                }

                return false;
            }

            if (result != _pid)
                return false;

            _exitCode = MapWaitStatusToExitCode(status);
            _termSignal = WIFSIGNALED(status) ? WTERMSIG(status) : 0;
            _exited = true;
            return true;
        }

        private void CloseTransport()
        {
            SendEotIfPending();
            if (_masterClosed)
                return;

            UnixInterop.close(_master);
            _masterClosed = true;
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
            if (_eofSent || _exited || _masterClosed)
                return;

            if (TryRefreshExitState())
            {
                CompleteEotSignaling();
                return;
            }

            DrainMasterOutputBeforeEot();

            if (TryRefreshExitState())
            {
                CompleteEotSignaling();
                return;
            }

            // Canonical line discipline: one EOT on a non-empty buffer submits the line but
            // does not signal EOF; a second EOT on the empty buffer ends input for programs like cat.
            // Stage the two EOT writes on separate wait polls so the child can attach and consume the submit.
            if (_inputWritten && !_inputEndsWithNewline && !_eotLineSubmitSent)
            {
                if (TryWriteEotByte())
                    _eotLineSubmitSent = true;
                else
                    CompleteEotSignaling();
                return;
            }

            TryWriteEotByte();
            CompleteEotSignaling();
        }

        private void CompleteEotSignaling()
        {
            _eofPending = false;
            _eofSent = true;
        }

        /// <summary>
        /// Writes one EOT byte. Returns <see langword="false"/> when the slave is gone (EIO/EPIPE), which is benign after exit.
        /// </summary>
        private unsafe bool TryWriteEotByte()
        {
            Span<byte> eot = stackalloc byte[1] { InputEot };
            fixed (byte* ptr = eot)
            {
                while (true)
                {
                    var written = UnixInterop.Write(_master, ptr, 1);
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
                                TryRefreshExitState();
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
        private void DrainMasterOutputBeforeEot()
        {
            if (!_inputWritten)
                return;

            if (TryRefreshExitState())
                return;

            if (OperatingSystem.IsMacOS())
                return;

            while (UnixInterop.tcdrain(_master) != 0)
            {
                if (Marshal.GetLastPInvokeError() is UnixInterop.EINTR)
                    continue;
                return;
            }
        }

        /// Polls for child exit for up to <paramref name="timeoutMs"/> without allocating a delay task.
        /// Mirrors the Windows backend's timed <c>WaitForSingleObject</c> loop.
        private bool PollForChildExit(int timeoutMs, CancellationToken cancellationToken)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryRefreshExitState())
                    return true;

                var remaining = (int)Math.Min(10, deadline - Environment.TickCount64);
                if (remaining > 0)
                    Thread.Sleep(remaining);
            }

            return TryRefreshExitState();
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
