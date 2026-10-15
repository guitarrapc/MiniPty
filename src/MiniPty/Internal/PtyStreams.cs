using Microsoft.Win32.SafeHandles;

namespace MiniPty.Internal;

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

/// <summary>Reference-counted access to a Unix PTY master, so a close never lands while the descriptor is in use.</summary>
internal interface IPtyMasterLease
{
    /// <summary>Pins the master for one use. Fails once the transport is closed.</summary>
    bool TryAcquireMaster(out int master);

    void ReleaseMaster();
}

/// <summary>Write stream over a Unix PTY master. Does not close the master.</summary>
internal sealed class PtyFdWriteStream : Stream
{
    private readonly IPtyMasterLease _master;

    public PtyFdWriteStream(IPtyMasterLease master) => _master = master;

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

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_master.TryAcquireMaster(out var master))
            throw new IOException("The PTY transport is closed.");

        try
        {
            PtyIo.WriteAll(master, buffer);
        }
        finally
        {
            _master.ReleaseMaster();
        }
    }

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
