using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

/// <summary>Read stream over a PTY output handle. Does not close the underlying handle.</summary>
internal sealed class PtyHandleReadStream : Stream
{
    private readonly SafeFileHandle handle;
    private PtySession? session;
    private int rawHoldActive;

    public PtyHandleReadStream(SafeFileHandle handle) => this.handle = handle;

    internal void BindOutputGate(PtySession outputSession) => session = outputSession;

    internal void SignalTransportPumpStarted() => session?.SignalTransportPumpHandshake();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)offset, (uint)buffer.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)(buffer.Length - offset));
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        session?.BeforeRawOutputRead(ref rawHoldActive);
        try
        {
            return EndRawOutputRead(ReadTransport(buffer));
        }
        catch
        {
            session?.AfterRawOutputRead(ref rawHoldActive);
            throw;
        }
    }

    /// <inheritdoc cref="Stream.ReadAsync(Memory{byte}, CancellationToken)" />
    /// <remarks>
    /// <para>
    /// Intentionally not <c>async</c>: when <see cref="rawHoldActive"/> is already set, this returns
    /// <see cref="ValueTask.FromResult{TResult}(TResult)"/> so tight <c>Output.ReadAsync</c> loops do not pay
    /// an async state machine per call (see <c>Session_32KiB_OutputStreamBytes</c>).
    /// </para>
    /// <para>
    /// <see cref="PtySession.BeforeRawOutputRead"/> runs synchronously at call start so a second
    /// <c>ReadOutputAsync</c> / <c>CompleteAsync</c> fails immediately even if transport I/O has not begun.
    /// </para>
    /// <para>
    /// Only the first read of an exclusive session delegates to <see cref="ReadFirstAsync"/>, which
    /// <see cref="Task.Yield"/>s before blocking transport I/O. That lets callers start concurrent pumps
    /// without deadlocking on an empty pipe; later reads in the same session use the synchronous fast path above.
    /// </para>
    /// </remarks>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return ValueTask.FromResult(0);

        cancellationToken.ThrowIfCancellationRequested();
        var continuing = Volatile.Read(ref rawHoldActive) != 0;
        session?.BeforeRawOutputRead(ref rawHoldActive);
        try
        {
            if (continuing)
                return ValueTask.FromResult(EndRawOutputRead(ReadTransport(buffer.Span)));

            return ReadFirstAsync(buffer, cancellationToken);
        }
        catch
        {
            session?.AfterRawOutputRead(ref rawHoldActive);
            throw;
        }
    }

    /// <summary>First gated read of a raw-output session; yields before blocking transport I/O.</summary>
    private async ValueTask<int> ReadFirstAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return EndRawOutputRead(ReadTransport(buffer.Span));
        }
        catch
        {
            session?.AfterRawOutputRead(ref rawHoldActive);
            throw;
        }
    }

    internal int ReadTransport(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                if (!WindowsInterop.ReadFile(handle, ptr, (uint)buffer.Length, out var read, IntPtr.Zero))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 109) // ERROR_BROKEN_PIPE
                        return 0;

                    throw new IOException($"ReadFile failed (Win32 {error})");
                }

                return (int)read;
            }
        }
    }

    /// <summary>
    /// Reads when bytes are immediately available; returns 0 when the pipe would block.
    /// ConPTY anonymous pipes use <c>PIPE_NOWAIT</c> because <see cref="TryGetAvailableBytes"/> is unreliable there.
    /// </summary>
    internal unsafe int TryReadTransportIfReady(Span<byte> buffer, out bool eof)
    {
        eof = false;
        if (buffer.IsEmpty)
            return 0;

        uint nowait = WindowsInterop.PipeNowait;
        if (!WindowsInterop.SetNamedPipeHandleState(handle, &nowait, IntPtr.Zero, IntPtr.Zero))
            return 0;

        try
        {
            fixed (byte* ptr = buffer)
            {
                if (!WindowsInterop.ReadFile(handle, ptr, (uint)buffer.Length, out var read, IntPtr.Zero))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 109) // ERROR_BROKEN_PIPE
                    {
                        eof = true;
                        return 0;
                    }

                    if (error == WindowsInterop.ErrorNoData)
                        return 0;

                    throw new IOException($"ReadFile failed (Win32 {error})");
                }

                return (int)read;
            }
        }
        finally
        {
            uint wait = WindowsInterop.PipeWait;
            _ = WindowsInterop.SetNamedPipeHandleState(handle, &wait, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Returns immediately readable bytes without consuming them. Peek failure is reported as <see langword="false"/>.</summary>
    internal unsafe bool TryGetAvailableBytes(out int available)
    {
        available = 0;
        if (WindowsInterop.PeekNamedPipe(handle, null, 0, out _, out var totalAvail, IntPtr.Zero))
        {
            available = (int)totalAvail;
            if (available > 0)
                return true;
        }
        else if (Marshal.GetLastPInvokeError() != 109) // ERROR_BROKEN_PIPE
        {
            return false;
        }

        // ConPTY anonymous pipes often report zero via byte-count peek; buffer peek can still observe pending bytes.
        Span<byte> scratch = stackalloc byte[1];
        fixed (byte* ptr = scratch)
        {
            if (!WindowsInterop.PeekNamedPipe(handle, ptr, 1, out _, out totalAvail, IntPtr.Zero))
            {
                if (Marshal.GetLastPInvokeError() != 109) // ERROR_BROKEN_PIPE
                    return false;

                return true;
            }

            available = (int)totalAvail;
            return true;
        }
    }

    private int EndRawOutputRead(int read)
    {
        if (read == 0)
            session?.AfterRawOutputRead(ref rawHoldActive);

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Handle lifetime is owned by the session backend.
    }
}

/// <summary>Write stream over a PTY input handle. Does not close the underlying handle.</summary>
internal sealed class PtyHandleWriteStream : Stream
{
    private readonly SafeFileHandle _handle;

    public PtyHandleWriteStream(SafeFileHandle handle) => _handle = handle;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) => PtyIo.WriteAll(_handle, buffer);

    protected override void Dispose(bool disposing)
    {
        // Handle lifetime is owned by the session backend.
    }
}

/// <summary>Read stream over a Unix PTY master fd. Does not close the fd.</summary>
internal sealed class PtyFdReadStream : Stream
{
    private readonly int fd;
    private PtySession? session;
    private int rawHoldActive;

    public PtyFdReadStream(int fd) => this.fd = fd;

    internal void BindOutputGate(PtySession outputSession) => session = outputSession;

    internal void SignalTransportPumpStarted() => session?.SignalTransportPumpHandshake();

    internal bool IsChildExited => session?.HasExited ?? true;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)offset, (uint)buffer.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)count, (uint)(buffer.Length - offset));
        return Read(buffer.AsSpan(offset, count));
    }

    public override unsafe int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        session?.BeforeRawOutputRead(ref rawHoldActive);
        try
        {
            return EndRawOutputRead(ReadTransport(buffer));
        }
        catch
        {
            session?.AfterRawOutputRead(ref rawHoldActive);
            throw;
        }
    }

    /// <inheritdoc cref="Stream.ReadAsync(Memory{byte}, CancellationToken)" />
    /// <remarks>
    /// <para>
    /// Intentionally not <c>async</c>: when <see cref="rawHoldActive"/> is already set, this returns
    /// <see cref="ValueTask.FromResult{TResult}(TResult)"/> so tight <c>Output.ReadAsync</c> loops do not pay
    /// an async state machine per call (see <c>Session_32KiB_OutputStreamBytes</c>).
    /// </para>
    /// <para>
    /// <see cref="PtySession.BeforeRawOutputRead"/> runs synchronously at call start so a second
    /// <c>ReadOutputAsync</c> / <c>CompleteAsync</c> fails immediately even if transport I/O has not begun.
    /// </para>
    /// <para>
    /// Only the first read of an exclusive session delegates to <see cref="ReadFirstAsync"/>, which
    /// <see cref="Task.Yield"/>s before blocking transport I/O. That lets callers start concurrent pumps
    /// without deadlocking on an empty pipe; later reads in the same session use the synchronous fast path above.
    /// </para>
    /// </remarks>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return ValueTask.FromResult(0);

        cancellationToken.ThrowIfCancellationRequested();
        var continuing = Volatile.Read(ref rawHoldActive) != 0;
        session?.BeforeRawOutputRead(ref rawHoldActive);
        try
        {
            if (continuing)
                return ValueTask.FromResult(EndRawOutputRead(ReadTransport(buffer.Span)));

            return ReadFirstAsync(buffer, cancellationToken);
        }
        catch
        {
            session?.AfterRawOutputRead(ref rawHoldActive);
            throw;
        }
    }

    /// <summary>First gated read of a raw-output session; yields before blocking transport I/O.</summary>
    private async ValueTask<int> ReadFirstAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return EndRawOutputRead(ReadTransport(buffer.Span));
        }
        catch
        {
            session?.AfterRawOutputRead(ref rawHoldActive);
            throw;
        }
    }

    internal unsafe int ReadTransport(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        fixed (byte* ptr = buffer)
        {
            while (true)
            {
                var read = UnixInterop.Read(fd, ptr, (nuint)buffer.Length);
                if (read < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == UnixInterop.EINTR)
                        continue;
                    if (errno is 0 or UnixInterop.EIO or UnixInterop.EBADF)
                        return 0;

                    throw new IOException($"read failed (errno {errno})");
                }

                return read;
            }
        }
    }

    /// <summary>Returns immediately readable bytes without consuming them. Peek failure is reported as <see langword="false"/>.</summary>
    internal unsafe bool TryGetAvailableBytes(out int available)
    {
        available = 0;
        int count;
        if (UnixInterop.minipty_peek_readable_bytes(fd, &count) != 0)
            return false;

        available = count;
        return true;
    }

    /// <summary>
    /// Reads when bytes are immediately available; returns 0 when the pipe would block.
    /// Linux PTY masters report pending bytes via <c>FIONREAD</c>; macOS often reports zero while data is
    /// readable, so macOS uses a temporary <c>O_NONBLOCK</c> read via <see cref="UnixInterop.minipty_try_read"/>.
    /// </summary>
    internal unsafe int TryReadTransportIfReady(Span<byte> buffer, out bool eof)
    {
        eof = false;
        if (buffer.IsEmpty)
            return 0;

        if (!OperatingSystem.IsMacOS())
        {
            if (!TryGetAvailableBytes(out var available) || available == 0)
                return 0;

            var blockingRead = ReadTransport(buffer);
            if (blockingRead == 0)
                eof = true;

            return blockingRead;
        }

        int tryRead;
        int isEof;
        fixed (byte* ptr = buffer)
        {
            if (UnixInterop.minipty_try_read(fd, ptr, (uint)buffer.Length, &tryRead, &isEof) != 0)
                throw new IOException($"minipty_try_read failed (errno {Marshal.GetLastPInvokeError()})");
        }

        if (isEof != 0)
            eof = true;

        return tryRead;
    }

    private int EndRawOutputRead(int read)
    {
        if (read == 0)
            session?.AfterRawOutputRead(ref rawHoldActive);

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) { }
}

/// <summary>Write stream over a Unix PTY master fd. Does not close the fd.</summary>
internal sealed class PtyFdWriteStream : Stream
{
    private readonly int _fd;

    public PtyFdWriteStream(int fd) => _fd = fd;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) => PtyIo.WriteAll(_fd, buffer);

    protected override void Dispose(bool disposing) { }
}

/// <summary>Notifies on each non-empty stdin write (EOF staging and line-ending tracking).</summary>
internal sealed class InputTrackingWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<ReadOnlySpan<byte>> _onWrite;

    public InputTrackingWriteStream(Stream inner, Action<ReadOnlySpan<byte>> onWrite)
    {
        _inner = inner;
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
            _onWrite(buffer);
        _inner.Write(buffer);
    }

    protected override void Dispose(bool disposing) { }
}
