using System.Buffers;
using System.Runtime.CompilerServices;

namespace MiniPty.Console.Internal;

internal sealed class PtyConsoleAttach : IDisposable
{
    private static readonly Lock Gate = new();
    private static readonly ConditionalWeakTable<PtySession, object> ActiveSessions = new();
    private static readonly object Sentinel = new();

    private readonly PtySession _session;
    private readonly bool _syncHostSize;
    private readonly IPtyConsoleInputObserver? _inputObserver;
    private readonly TimeProvider _timeProvider;
    private readonly long _attachTimestamp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _resizeTask;
    private readonly byte[] _inputBuffer = ArrayPool<byte>.Shared.Rent(4096);
    private readonly Task? _inputTask;
    private readonly Thread? _windowsInputThread;

    private IHostTerminal _terminal = null!;
    private int _disposed;

    internal PtyConsoleAttach(PtySession session, PtyConsoleAttachOptions options)
    {
        _session = session;
        _syncHostSize = options.SyncHostSize;
        _inputObserver = options.InputObserver;
        _timeProvider = options.TimeProvider;
        try
        {
            _attachTimestamp = _timeProvider.GetTimestamp();
        }
        catch
        {
            CleanupInitFailure();
            throw;
        }

        if (OperatingSystem.IsWindows())
        {
            using var ready = new ManualResetEventSlim(initialState: false);
            Exception? initFailure = null;

            _windowsInputThread = new Thread(() =>
            {
                try
                {
                    _terminal = HostTerminal.Create();
                    if (_syncHostSize)
                        SyncSize();
                    ready.Set();
                    RunInputPumpSync();
                }
                catch (Exception ex)
                {
                    initFailure = ex;
                    ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "MiniPty.Console.Input",
            };

            _windowsInputThread.Start();
            ready.Wait();

            if (initFailure is not null)
                ThrowInitFailure(initFailure);
        }
        else
        {
            try
            {
                _terminal = HostTerminal.Create();
                if (_syncHostSize)
                    SyncSize();
                _inputTask = Task.Run(InputPumpAsync);
            }
            catch
            {
                CleanupInitFailure();
                throw;
            }
        }

        if (_syncHostSize)
            _resizeTask = Task.Run(ResizePollAsync);
        else
            _resizeTask = Task.CompletedTask;
    }

    private void ThrowInitFailure(Exception inner)
    {
        CleanupInitFailure();
        throw new InvalidOperationException("Failed to configure the host terminal.", inner);
    }

    private void CleanupInitFailure()
    {
        if (_terminal is not null)
            _terminal.Dispose();

        ArrayPool<byte>.Shared.Return(_inputBuffer);
        _cts.Dispose();
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

        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled)
            return;

        cancellationToken.WaitHandle.WaitOne();
    }

    internal void PumpInputOnce(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, typeof(PtyConsoleInputHandle));
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
            if (_windowsInputThread is not null)
                _windowsInputThread.Join();
            else if (_inputTask is not null)
                _inputTask.GetAwaiter().GetResult();

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

    private void RunInputPumpSync()
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
            catch (IOException)
            {
                break;
            }

            if (read <= 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                Thread.Sleep(1);
                continue;
            }

            try
            {
                PtyConsoleInputForward.Forward(
                    _session.Input,
                    _inputBuffer.AsSpan(0, read),
                    _inputObserver,
                    _timeProvider,
                    _attachTimestamp);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
        }
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
            catch (IOException)
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
                PtyConsoleInputForward.Forward(
                    _session.Input,
                    _inputBuffer.AsSpan(0, read),
                    _inputObserver,
                    _timeProvider,
                    _attachTimestamp);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (IOException)
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
