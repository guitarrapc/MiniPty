using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MiniPty.Internal;

namespace MiniPty;

/// <summary>Cross-platform pseudo-terminal factory and convenience APIs.</summary>
public static class Pty
{
    /// <summary>Whether the current operating system supports pseudo-terminals.</summary>
    public static bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD);

    /// <summary>Spawns a child in a new pseudo-terminal. Does not wait for exit.</summary>
    /// <param name="options">Process and terminal options.</param>
    /// <returns>A session with <see cref="PtySession.Input"/> and <see cref="PtySession.Output"/> streams.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">PTY is not supported on this operating system.</exception>
    public static PtySession Start(PtyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsSupported)
            throw new PlatformNotSupportedException("PTY is not supported on this operating system.");

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
    /// <param name="options">Process, terminal, and capture options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Merged PTY output, exit code, and timestamped chunks.</returns>
    public static async Task<PtyCaptureResult> Run(
        PtyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var session = Start(options);
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

        var output = string.Concat(chunks.Select(static c => c.Data));
        return new PtyCaptureResult(output, exitCode, chunks);
    }

    /// <summary>Runs a child to completion and returns only the exit code.</summary>
    /// <param name="options">Process, terminal, and capture options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The child process exit code.</returns>
    public static async Task<int> RunExitCodeAsync(
        PtyOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = await Run(options, cancellationToken).ConfigureAwait(false);
        return result.ExitCode;
    }
}
