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
    private readonly uint _attachThreadId;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _inputTask;
    private readonly Task _resizeTask;
    private readonly byte[] _inputBuffer = ArrayPool<byte>.Shared.Rent(4096);
    private int _disposed;

    internal PtyConsoleAttach(PtySession session)
    {
        _session = session;
        _attachThreadId = OperatingSystem.IsWindows()
            ? ConsoleWindowsInterop.GetCurrentThreadId()
            : 0;
        _terminal = HostTerminal.Create();
        SyncSize();

        if (OperatingSystem.IsWindows())
        {
            _inputTask = null;
        }
        else
        {
            _inputTask = Task.Run(InputPumpAsync);
        }

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

    internal void PumpInputUntil(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, typeof(PtyConsoleInputHandle));

        if (OperatingSystem.IsWindows())
        {
            if (!cancellationToken.CanBeCanceled)
            {
                throw new InvalidOperationException(
                    "PumpInputUntil requires a cancelable token on Windows.");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                PumpInputOnce(cancellationToken);
            }

            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled)
            return;

        cancellationToken.WaitHandle.WaitOne();
    }

    internal void PumpInputOnce(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return;

        ObjectDisposedException.ThrowIf(_disposed != 0, typeof(PtyConsoleInputHandle));

        if (ConsoleWindowsInterop.GetCurrentThreadId() != _attachThreadId)
        {
            throw new InvalidOperationException(
                "On Windows, PtyConsoleInputHandle.PumpInputOnce must be called from the thread that invoked Attach.");
        }

        CancellationToken token;
        CancellationTokenSource? linked = null;
        if (cancellationToken.CanBeCanceled)
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            token = linked.Token;
        }
        else
        {
            token = _cts.Token;
        }

        try
        {
            int read;
            try
            {
                read = _terminal.ReadInput(_inputBuffer, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }

            if (read <= 0)
            {
                if (token.IsCancellationRequested)
                    return;

                Thread.Sleep(1);
                return;
            }

            try
            {
                _session.Input.Write(_inputBuffer, 0, read);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            linked?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();

        if (_terminal is WindowsHostTerminal windowsTerminal)
            windowsTerminal.CancelPendingRead();

        try
        {
            if (_inputTask is not null)
                Task.WhenAll(_inputTask, _resizeTask).GetAwaiter().GetResult();
            else
                _resizeTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _terminal.Dispose();
        ArrayPool<byte>.Shared.Return(_inputBuffer);
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

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = _terminal.ReadInput(_inputBuffer, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (read <= 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                _session.Input.Write(_inputBuffer, 0, read);
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
