using System.Buffers;
using System.Runtime.CompilerServices;

namespace MiniPty.Console.Internal;

internal sealed class PtyConsoleAttach : IDisposable
{
    private static readonly Lock Gate = new();
    private static readonly ConditionalWeakTable<PtySession, object> ActiveSessions = new();
    private static readonly object Sentinel = new();

    private readonly PtySession _session;
    private readonly IHostTerminal _terminal;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _inputTask;
    private readonly Task _resizeTask;
    private int _disposed;

    internal PtyConsoleAttach(PtySession session)
    {
        _session = session;
        _terminal = HostTerminal.Create();
        SyncSize();

        _inputTask = Task.Run(InputPumpAsync);
        _resizeTask = Task.Run(ResizePollAsync);
    }

    internal static void Register(PtySession session)
    {
        lock (Gate)
        {
            if (ActiveSessions.TryGetValue(session, out _))
            {
                throw new InvalidOperationException(
                    "A PtyConsoleInput attach is already active for this session.");
            }

            ActiveSessions.Add(session, Sentinel);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();

        try
        {
            Task.WhenAll(_inputTask, _resizeTask).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _terminal.Dispose();
        Unregister(_session);
        _cts.Dispose();
    }

    internal static void Unregister(PtySession session)
    {
        lock (Gate)
        {
            ActiveSessions.Remove(session);
        }
    }

    private void SyncSize()
    {
        if (_terminal.TryGetSize(out var columns, out var rows) && columns > 0 && rows > 0)
            _session.Resize(new PtySize(columns, rows));
    }

    private async Task InputPumpAsync()
    {
        var cancellationToken = _cts.Token;
        var buffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = _terminal.ReadInput(buffer);
                if (read <= 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    continue;
                }

                try
                {
                    await _session.WriteInputAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task ResizePollAsync()
    {
        var cancellationToken = _cts.Token;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_terminal.TryPollResize(out var columns, out var rows) && columns > 0 && rows > 0)
                {
                    try
                    {
                        _session.Resize(new PtySize(columns, rows));
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
