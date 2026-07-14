namespace MiniPty.Terminal.Internal;

/// <summary>
/// Fixed-size byte ring retaining unacknowledged terminal output by absolute stream offset.
/// Allocates once per persistent session; producer and consumer hot paths only copy/slice.
/// </summary>
internal sealed class PersistentOutputBuffer : IDisposable
{
    private readonly byte[] _buffer;
    private readonly Lock _lock = new();
    private TaskCompletionSource _dataAvailable = NewSignal();
    private TaskCompletionSource _spaceAvailable = NewSignal();
    private bool _dataWaiting;
    private bool _spaceWaiting;
    private long _startOffset;
    private long _endOffset;
    private PtyExitStatus? _completion;
    private Exception? _error;
    private bool _disposed;

    public PersistentOutputBuffer(int capacity) => _buffer = new byte[capacity];

    public int Capacity => _buffer.Length;

    public (long Start, long End) OffsetRange
    {
        get
        {
            lock (_lock)
                return (_startOffset, _endOffset);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var consumed = 0;
        while (consumed < data.Length)
        {
            Task waitTask;
            lock (_lock)
            {
                ThrowIfUnavailable();
                var free = _buffer.Length - checked((int)(_endOffset - _startOffset));
                if (free == 0)
                {
                    _spaceWaiting = true;
                    waitTask = _spaceAvailable.Task;
                }
                else
                {
                    var count = Math.Min(free, data.Length - consumed);
                    var writeIndex = (int)(_endOffset % _buffer.Length);
                    var first = Math.Min(count, _buffer.Length - writeIndex);
                    data.Span.Slice(consumed, first).CopyTo(_buffer.AsSpan(writeIndex));
                    if (first < count)
                        data.Span.Slice(consumed + first, count - first).CopyTo(_buffer);

                    _endOffset += count;
                    consumed += count;
                    PulseDataAvailable();
                    continue;
                }
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<PersistentOutputRead> ReadAsync(
        long offset,
        int maxLength,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (offset < _startOffset || offset > _endOffset)
                    throw new ArgumentOutOfRangeException(nameof(offset), $"Available replay offsets are {_startOffset} through {_endOffset}.");
                if (offset < _endOffset)
                {
                    var index = (int)(offset % _buffer.Length);
                    var available = checked((int)Math.Min(_endOffset - offset, int.MaxValue));
                    var count = Math.Min(Math.Min(available, maxLength), _buffer.Length - index);
                    return new PersistentOutputRead(_buffer.AsMemory(index, count), null);
                }

                if (_error is not null)
                    throw new IOException("The persistent terminal output pump failed.", _error);

                if (_completion is { } status)
                    return new PersistentOutputRead(default, status);

                waitTask = _dataAvailable.Task;
                _dataWaiting = true;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void ResumeFrom(long acknowledgedOffset)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (acknowledgedOffset < _startOffset || acknowledgedOffset > _endOffset)
                throw new ArgumentOutOfRangeException(
                    nameof(acknowledgedOffset),
                    $"Available replay offsets are {_startOffset} through {_endOffset}.");

            AdvanceStart(acknowledgedOffset);
        }
    }

    public bool TryAcknowledge(long acknowledgedOffset, long sentOffset)
    {
        lock (_lock)
        {
            if (_disposed || acknowledgedOffset < _startOffset || acknowledgedOffset > sentOffset || sentOffset > _endOffset)
                return false;

            AdvanceStart(acknowledgedOffset);
            return true;
        }
    }

    public void Complete(PtyExitStatus status)
    {
        lock (_lock)
        {
            if (_disposed || _completion is not null || _error is not null)
                return;
            _completion = status;
            PulseDataAvailable();
        }
    }

    public void Fault(Exception error)
    {
        lock (_lock)
        {
            if (_disposed || _completion is not null || _error is not null)
                return;
            _error = error;
            PulseDataAvailable();
            PulseSpaceAvailable();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            PulseDataAvailable();
            PulseSpaceAvailable();
        }
    }

    private void AdvanceStart(long offset)
    {
        if (offset == _startOffset)
            return;
        _startOffset = offset;
        PulseSpaceAvailable();
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_error is not null)
            throw new IOException("The persistent terminal output pump failed.", _error);
        if (_completion is not null)
            throw new InvalidOperationException("Cannot write output after terminal completion.");
    }

    private void PulseDataAvailable()
    {
        if (!_dataWaiting)
            return;
        _dataWaiting = false;
        var signal = _dataAvailable;
        _dataAvailable = NewSignal();
        signal.TrySetResult();
    }

    private void PulseSpaceAvailable()
    {
        if (!_spaceWaiting)
            return;
        _spaceWaiting = false;
        var signal = _spaceAvailable;
        _spaceAvailable = NewSignal();
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal readonly record struct PersistentOutputRead(ReadOnlyMemory<byte> Data, PtyExitStatus? Completion);
