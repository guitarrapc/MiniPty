namespace MiniPty.Internal;

/// <summary>
/// <see cref="PtySession.Input"/>: a write-only stream over the backend's input members, shared by both platforms.
/// </summary>
/// <remarks>
/// Every asynchronous overload goes through <see cref="IPtyBackend.WriteInputAsync(ReadOnlyMemory{byte}, byte[], CancellationToken)"/>:
/// Stream's defaults would run the blocking write on a thread-pool worker and allocate a task per call.
/// <see cref="Write(ReadOnlySpan{byte})"/> blocks until the bytes are written, per the <see cref="Stream"/> contract.
/// The stream does not own the transport; disposing it does nothing.
/// </remarks>
internal sealed class PtyInputStream(IPtyBackend backend) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        backend.WriteInput(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer) => backend.WriteInput(buffer);

    public override void WriteByte(byte value)
    {
        Span<byte> one = [value];
        backend.WriteInput(one);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        backend.WriteInputAsync(buffer, ownedBuffer: null, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return backend.WriteInputAsync(buffer.AsMemory(offset, count), ownedBuffer: null, cancellationToken).AsTask();
    }

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        TaskToAsyncResult.Begin(WriteAsync(buffer, offset, count, CancellationToken.None), callback, state);

    public override void EndWrite(IAsyncResult asyncResult) => TaskToAsyncResult.End(asyncResult);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The session owns the transport.
    }
}
