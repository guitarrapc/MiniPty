using System.Runtime.InteropServices;
using System.Text;

namespace MiniPty.Internal;

public static partial class UnixPtyBackend
{
    private const byte InputEot = 0x04;
    private const int WaitPollMs = 100;
    private const int ReapPollMs = 10;
    private const int ReapDeadlineMs = 1_000;

    internal static IPtyBackend Start(PtyOptions options)
    {
        var size = options.Size;
        var winsize = new Winsize { ws_col = (ushort)size.Columns, ws_row = (ushort)size.Rows };
        if (OpenPty(out var master, out var slave, ref winsize) != 0)
            throw new IOException($"openpty failed (errno {Marshal.GetLastPInvokeError()})");

        var tiocSetCtty = TiocSetCtty();
        var arguments = options.Arguments as string[] ?? options.Arguments.ToArray();
        var exec = UnixExecPayload.Create(options.FileName, arguments, options.WorkingDirectory);
        try
        {
            var pid = fork();
            if (pid < 0)
            {
                UnixInterop.close(master);
                UnixInterop.close(slave);
                throw new IOException($"fork failed (errno {Marshal.GetLastPInvokeError()})");
            }

            if (pid == 0)
                ChildMainAfterFork(master, slave, tiocSetCtty, exec.Executable, exec.Argv, exec.WorkingDirectory);

            UnixInterop.close(slave);
            return new UnixPtyBackendInstance(master, pid, size);
        }
        finally
        {
            exec.Dispose();
        }
    }

    /// <summary>
    /// Child path after <c>fork()</c>. Only async-signal-safe libc calls — no managed allocation or runtime APIs.
    /// </summary>
    private static unsafe void ChildMainAfterFork(
        int master,
        int slave,
        ulong tiocSetCtty,
        IntPtr executable,
        IntPtr argv,
        IntPtr workingDirectory)
    {
        close(master);
        setsid();
        ioctl(slave, tiocSetCtty, 0);
        dup2(slave, 0);
        dup2(slave, 1);
        dup2(slave, 2);
        if (slave > 2)
            close(slave);
        if (workingDirectory != IntPtr.Zero)
            chdir((byte*)workingDirectory);
        execvp((byte*)executable, (byte**)argv);
        _exit(127);
    }

    private sealed class UnixExecPayload : IDisposable
    {
        private readonly List<IntPtr> _owned;

        private UnixExecPayload(List<IntPtr> owned, IntPtr executable, IntPtr argv, IntPtr workingDirectory)
        {
            _owned = owned;
            Executable = executable;
            Argv = argv;
            WorkingDirectory = workingDirectory;
        }

        public IntPtr Executable { get; }
        public IntPtr Argv { get; }
        public IntPtr WorkingDirectory { get; }

        public static unsafe UnixExecPayload Create(string fileName, string[] arguments, string? cwd)
        {
            var owned = new List<IntPtr>();
            try
            {
                var executable = AllocUtf8CString(fileName, owned);
                var argv = AllocUtf8Argv(fileName, arguments, owned);
                IntPtr workingDirectory = IntPtr.Zero;
                if (!string.IsNullOrWhiteSpace(cwd))
                    workingDirectory = (IntPtr)AllocUtf8CString(cwd, owned);
                return new UnixExecPayload(owned, (IntPtr)executable, (IntPtr)argv, workingDirectory);
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
        private bool _masterClosed;
        private bool _exited;
        private int _exitCode;
        private bool _disposed;

        public UnixPtyBackendInstance(int master, int pid, PtySize size)
        {
            _master = master;
            _pid = pid;
            Size = size;
            _inputStream = new InputTrackingWriteStream(master, () => _inputWritten = true);
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

        public PtySize Size { get; }

        public void Resize(int columns, int rows) =>
            throw new NotSupportedException("PTY resize is not supported on this platform.");

        public void SignalEof()
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
                    cancellationToken.ThrowIfCancellationRequested();

                    SendEotIfPending();

                    await Task.Delay(WaitPollMs, cancellationToken);
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

        private void WriteEotToMaster()
        {
            if (_eofSent || _exited || _masterClosed)
                return;

            PtyIo.WriteAll(_master, stackalloc byte[1] { InputEot });
            _eofPending = false;
            _eofSent = true;
        }
    }

    private sealed class InputTrackingWriteStream : Stream
    {
        private readonly PtyFdWriteStream _inner;
        private readonly Action _onWrite;

        public InputTrackingWriteStream(int fd, Action onWrite)
        {
            _inner = new PtyFdWriteStream(fd);
            _onWrite = onWrite;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!buffer.IsEmpty)
                _onWrite();
            _inner.Write(buffer);
        }

        protected override void Dispose(bool disposing) { }
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

    private static int OpenPty(out int master, out int slave, ref Winsize winsize)
    {
        if (OperatingSystem.IsLinux())
            return LinuxOpenPty(out master, out slave, IntPtr.Zero, IntPtr.Zero, ref winsize);
        if (OperatingSystem.IsMacOS())
            return MacOSOpenPty(out master, out slave, IntPtr.Zero, IntPtr.Zero, ref winsize);
        if (OperatingSystem.IsFreeBSD())
            return FreeBSDOpenPty(out master, out slave, IntPtr.Zero, IntPtr.Zero, ref winsize);

        throw new PlatformNotSupportedException("PTY is not supported on this Unix operating system.");
    }

    private static ulong TiocSetCtty()
    {
        if (OperatingSystem.IsLinux())
            return Linux.TIOCSCTTY;
        if (OperatingSystem.IsMacOS())
            return MacOS.TIOCSCTTY;
        if (OperatingSystem.IsFreeBSD())
            return FreeBSD.TIOCSCTTY;

        throw new PlatformNotSupportedException("PTY is not supported on this Unix operating system.");
    }

    private static class Linux
    {
        internal const ulong TIOCSCTTY = 0x540E;
    }

    private static class MacOS
    {
        internal const ulong TIOCSCTTY = 0x20007461;
    }

    private static class FreeBSD
    {
        internal const ulong TIOCSCTTY = 0x20007461;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int LinuxOpenPty(out int amaster, out int aslave, IntPtr name, IntPtr termp, ref Winsize winp);

    [LibraryImport("libutil", SetLastError = true)]
    private static partial int MacOSOpenPty(out int amaster, out int aslave, IntPtr name, IntPtr termp, ref Winsize winp);

    [LibraryImport("libutil", SetLastError = true)]
    private static partial int FreeBSDOpenPty(out int amaster, out int aslave, IntPtr name, IntPtr termp, ref Winsize winp);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fork();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int fd, ulong request, int arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(int oldfd, int newfd);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int chdir(byte* path);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int execvp(byte* file, byte** argv);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc")]
    private static partial void _exit(int status);

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }
}
