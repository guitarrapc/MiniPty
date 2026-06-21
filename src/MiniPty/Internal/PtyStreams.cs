using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

/// <summary>Read stream over a PTY output handle. Does not close the underlying handle.</summary>
internal sealed class PtyHandleReadStream : Stream
{
    private readonly SafeFileHandle _handle;

    public PtyHandleReadStream(SafeFileHandle handle) => _handle = handle;

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

        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                if (!WindowsInterop.ReadFile(_handle, ptr, (uint)buffer.Length, out var read, IntPtr.Zero))
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
    private readonly int _fd;

    public PtyFdReadStream(int fd) => _fd = fd;

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

        fixed (byte* ptr = buffer)
        {
            while (true)
            {
                var read = UnixInterop.Read(_fd, ptr, (nuint)buffer.Length);
                if (read < 0)
                {
                    if (Marshal.GetLastPInvokeError() == UnixInterop.EINTR)
                        continue;
                    if (Marshal.GetLastPInvokeError() == 0)
                        return 0;
                    throw new IOException($"read failed (errno {Marshal.GetLastPInvokeError()})");
                }

                return read;
            }
        }
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
