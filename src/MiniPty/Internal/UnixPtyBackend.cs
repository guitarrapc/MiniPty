using System.Runtime.InteropServices;
using System.Text;

namespace MiniPty.Internal;

internal static partial class UnixPtyBackend
{
    private const byte InputEot = 0x04;
    private const int WaitPollMs = 100;
    private const int ReapPollMs = 10;
    private const int ReapDeadlineMs = 1_000;

    internal static IPtyBackend Start(PtyStartInfo startInfo)
    {
        var size = startInfo.ClampedSize;
        var winsize = new Winsize { ws_col = (ushort)size.Columns, ws_row = (ushort)size.Rows };
        var arguments = startInfo.Arguments as string[] ?? startInfo.Arguments.ToArray();
        var environment = PtyEnvironment.BuildUnix(startInfo);
        var exec = UnixExecPayload.Create(startInfo.FileName, arguments, startInfo.WorkingDirectory, environment);
        try
        {
            var pid = 0;
            var master = 0;
            unsafe
            {
                if (ForkPtyExec(&master, &winsize, exec.WorkingDirectory, exec.Executable, exec.Argv, exec.Envp, &pid) != 0)
                    throw new IOException($"PTY spawn failed (errno {Marshal.GetLastPInvokeError()})");
            }

            return new UnixPtyBackendInstance(master, pid, size);
        }
        finally
        {
            exec.Dispose();
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
        private readonly InputTrackingWriteStream _inputStream;
        private bool _eofSent;
        private bool _eofPending;
        private bool _inputWritten;
        private bool _inputEndsWithNewline;
        private bool _masterClosed;
        private bool _exited;
        private int _exitCode;
        private bool _disposed;
        private PtySize _size;

        public UnixPtyBackendInstance(int master, int pid, PtySize size)
        {
            _master = master;
            _pid = pid;
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

        public PtySize Size => _size;

        public void Resize(int columns, int rows)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_masterClosed)
                throw new InvalidOperationException("Cannot resize after the PTY has been closed.");

            columns = Math.Clamp(columns, 1, 512);
            rows = Math.Clamp(rows, 1, 512);
            if (minipty_set_winsize(_master, (ushort)rows, (ushort)columns) != 0)
                throw new IOException($"TIOCSWINSZ failed (errno {Marshal.GetLastPInvokeError()})");

            _size = new PtySize(columns, rows);
        }

        public void SendEof()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryRefreshExitState() || _eofSent)
                return;

            if (_inputWritten)
            {
                WriteEotToMaster();
                return;
            }

            // Empty stdin EOF: defer EOT until the wait loop gives the child time to attach.
            _eofPending = true;
        }

        public void Kill()
        {
            if (_disposed || TryRefreshExitState())
                return;

            UnixInterop.kill(_pid, UnixInterop.SigKill);
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken, bool killOnCancellation)
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

                    SendEotIfPending();

                    if (PollForChildExit(WaitPollMs, cancellationToken))
                        break;

                    await Task.Yield();
                }

                cancellationToken.ThrowIfCancellationRequested();
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
                Kill();
                TryReapChild();
            }

            CloseTransport();
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
            if (_eofPending)
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

            PtyIo.WriteAll(_master, stackalloc byte[1] { InputEot });
            // Canonical line discipline: one EOT on a non-empty buffer submits the line but
            // does not signal EOF; a second EOT on the empty buffer ends input for programs like cat.
            if (_inputWritten && !_inputEndsWithNewline)
                PtyIo.WriteAll(_master, stackalloc byte[1] { InputEot });

            _eofPending = false;
            _eofSent = true;
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

    private static bool WIFEXITED(int status) => (status & 0x7f) == 0;
    private static int WEXITSTATUS(int status) => (status >> 8) & 0xff;
    private static bool WIFSIGNALED(int status) => (((status & 0x7f) + 1) >> 1) > 0;
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

    [LibraryImport("minipty_unix", SetLastError = true)]
    private static unsafe partial int minipty_fork_pty_exec(
        int* master,
        Winsize* winp,
        byte* working_directory,
        byte* file,
        byte** argv,
        byte** envp,
        int* pid_out);

    [LibraryImport("minipty_unix", SetLastError = true)]
    private static partial int minipty_set_winsize(int master, ushort rows, ushort cols);

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }
}
