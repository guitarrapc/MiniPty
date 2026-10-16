using System.Buffers;

namespace MiniPty.Internal;

/// <summary>
/// <see cref="PtySession.Input"/>: a write-only stream over the backend's input queue, shared by both platforms.
/// </summary>
/// <remarks>
/// Every asynchronous overload goes through <see cref="PtyInputWrites"/>: Stream's defaults would run the blocking
/// write on a thread-pool worker and allocate a task per call. <see cref="Write(ReadOnlySpan{byte})"/> blocks until
/// the bytes are written, per the <see cref="Stream"/> contract. The stream does not own the transport, so disposing
/// it does nothing.
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
        PtyInputWrites.Write(backend, buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer) => PtyInputWrites.Write(backend, buffer);

    public override void WriteByte(byte value)
    {
        Span<byte> one = [value];
        PtyInputWrites.Write(backend, one);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        PtyInputWrites.WriteAsync(backend, buffer, ownedBuffer: null, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return PtyInputWrites.WriteAsync(backend, buffer.AsMemory(offset, count), ownedBuffer: null, cancellationToken).AsTask();
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

/// <summary>
/// The caller's side of a stdin write into the backend's input queue, shared by
/// <see cref="PtySession"/> and <see cref="PtyInputStream"/> so both report the same way on every platform.
/// </summary>
/// <remarks>
/// A canceled token and a failed write are reported through the returned task, not thrown from the call: a caller
/// that starts a write and awaits it later, or never, still finds the failure in the task. Disposal is a usage error
/// and still throws.
/// </remarks>
internal static class PtyInputWrites
{
    /// <summary>
    /// Writes <paramref name="bytes"/>. The task completes once all of them are written. The write owns
    /// <paramref name="ownedBuffer"/>, the pooled array behind the bytes when set, and returns it.
    /// </summary>
    internal static ValueTask WriteAsync(IPtyBackend backend, ReadOnlyMemory<byte> bytes, byte[]? ownedBuffer, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            if (ownedBuffer is not null)
                ArrayPool<byte>.Shared.Return(ownedBuffer);

            return ValueTask.FromCanceled(cancellationToken);
        }

        try
        {
            return backend.QueueInput(bytes, ownedBuffer) is { } entry ? entry.ToValueTask(cancellationToken) : default;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            return ValueTask.FromException(ex);
        }
    }

    /// <summary>As the memory overload. Bytes that must wait are copied first, so the span need not outlive the call.</summary>
    internal static ValueTask WriteAsync(IPtyBackend backend, ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        try
        {
            return backend.QueueInput(bytes) is { } entry ? entry.ToValueTask(cancellationToken) : default;
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            return ValueTask.FromException(ex);
        }
    }

    /// <summary>Writes <paramref name="bytes"/> and blocks until all of them are written, per the Stream contract.</summary>
    internal static void Write(IPtyBackend backend, ReadOnlySpan<byte> bytes) => backend.QueueInput(bytes)?.Wait();
}
