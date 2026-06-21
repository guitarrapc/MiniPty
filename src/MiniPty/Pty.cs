using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty.Internal;
using MiniPty.Recording;

namespace MiniPty;

/// <summary>Cross-platform pseudo-terminal factory and convenience APIs.</summary>
public static class Pty
{
    /// <summary>Whether PTY sessions are supported on the current operating system.</summary>
    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD);

    /// <summary>Spawns a child in a new pseudo-terminal. Does not wait for exit.</summary>
    public static PtySession Start(PtySpawnOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsSupported)
            throw new PlatformNotSupportedException("PTY is not supported on this operating system.");

        options = options with
        {
            Columns = options.Size.Columns,
            Rows = options.Size.Rows,
        };

        IPtyBackend backend;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            backend = WindowsPtyBackend.Start(options);
        else
            backend = UnixPtyBackend.Start(options);

        return new PtySession(backend);
    }

    /// <summary>
    /// Spawns a child, optionally writes stdin, records timestamped output, and waits for exit.
    /// </summary>
    public static async Task<PtyRecording> RecordAsync(
        PtySpawnOptions spawn,
        PtyRecordOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PtyRecordOptions();
        var session = Start(spawn);
        await using var _ = session.ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var pump = Task.Run(
            () => PtyChunkReader.Read(session.Output, stopwatch, options.OutputEncoding),
            cancellationToken);

        if (options.Input is not null)
        {
            if (options.Input.Length > 0)
                await PtyIo.WriteUtf8Async(session.Input, options.Input, cancellationToken);
            session.SignalEof();
        }

        var exitCode = options.KillOnCancellation
            ? await session.WaitForExitOrKillAsync(cancellationToken).ConfigureAwait(false)
            : await session.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var chunks = await PtyOutputDrain.AwaitPumpAsync(
            pump,
            session.CloseOutputTransport,
            options.OutputDrainTimeout,
            options.OutputCloseGrace,
            throwOnTimeout: true,
            transportAlreadyClosed: false,
            cancellationToken).ConfigureAwait(false);

        return new PtyRecording
        {
            ExitCode = exitCode,
            Chunks = chunks,
        };
    }

    /// <summary>One-shot capture: merged text output without exposing chunks.</summary>
    public static async Task<PtyCapture> CaptureAsync(
        PtySpawnOptions spawn,
        PtyRecordOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var recording = await RecordAsync(spawn, options, cancellationToken).ConfigureAwait(false);
        return new PtyCapture
        {
            ExitCode = recording.ExitCode,
            Text = recording.Text,
        };
    }

    /// <summary>Runs a child to completion and returns only the exit code.</summary>
    public static async Task<int> RunAsync(
        PtySpawnOptions spawn,
        PtyRecordOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var recording = await RecordAsync(spawn, options, cancellationToken).ConfigureAwait(false);
        return recording.ExitCode;
    }
}
