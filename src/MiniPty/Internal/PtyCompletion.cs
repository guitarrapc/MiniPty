namespace MiniPty.Internal;

internal static class PtyCompletion
{
    internal delegate Task<TOutput> OutputPump<TOutput>(Stream output, CancellationToken cancellationToken);

    internal delegate Task<TOutput> SessionOutputPump<TOutput>(PtySession session, CancellationToken cancellationToken);

    internal static Task<(TOutput Output, int ExitCode)> RunAsync<TOutput>(
        PtySession session,
        PtyCompleteOptions options,
        OutputPump<TOutput> pump,
        CancellationToken cancellationToken) =>
        RunWithTransportPumpAsync(session, options, (output, ct) => pump(output, ct), cancellationToken);

    internal static Task<(TOutput Output, int ExitCode)> RunAsync<TOutput>(
        PtySession session,
        PtyCompleteOptions options,
        SessionOutputPump<TOutput> pump,
        CancellationToken cancellationToken) =>
        RunWithSessionPumpAsync(session, options, pump, cancellationToken);

    private static async Task<(TOutput Output, int ExitCode)> RunWithTransportPumpAsync<TOutput>(
        PtySession session,
        PtyCompleteOptions options,
        OutputPump<TOutput> pump,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pump);

        var pumpTask = pump(session.OutputTransport, cancellationToken);
        await ApplyInputAsync(session, options, cancellationToken).ConfigureAwait(false);
        var exitCode = await WaitForExitAsync(session, options, cancellationToken).ConfigureAwait(false);
        var output = await PtyOutputDrain.AwaitPumpAsync(
            pumpTask,
            session.CloseOutputTransport,
            options.OutputDrainGrace,
            options.OutputReaderCloseTimeout,
            throwOnTimeout: true,
            transportAlreadyClosed: false,
            cancellationToken).ConfigureAwait(false);

        return (output, exitCode);
    }

    private static async Task<(TOutput Output, int ExitCode)> RunWithSessionPumpAsync<TOutput>(
        PtySession session,
        PtyCompleteOptions options,
        SessionOutputPump<TOutput> pump,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pump);

        var pumpTask = pump(session, cancellationToken);
        await ApplyInputAsync(session, options, cancellationToken).ConfigureAwait(false);
        var exitCodeTask = WaitForExitAsync(session, options, cancellationToken);
        var output = await PtyOutputDrain.AwaitPumpAsync(
            pumpTask,
            session.CloseOutputTransport,
            options.OutputDrainGrace,
            options.OutputReaderCloseTimeout,
            throwOnTimeout: true,
            transportAlreadyClosed: false,
            cancellationToken).ConfigureAwait(false);
        var exitCode = await exitCodeTask.ConfigureAwait(false);

        return (output, exitCode);
    }

    private static async Task ApplyInputAsync(PtySession session, PtyCompleteOptions options, CancellationToken cancellationToken)
    {
        if (options.Input is null)
            return;

        if (options.Input.Length > 0)
            await session.WriteInputAsync(options.Input, options.OutputEncoding, cancellationToken).ConfigureAwait(false);

        if (options.SendEofAfterInput)
            session.SendEof();
    }

    private static async Task<int> WaitForExitAsync(
        PtySession session,
        PtyCompleteOptions options,
        CancellationToken cancellationToken)
    {
        var waitTask = session.WaitForExitInternalAsync(cancellationToken, options.KillOnCancellation);

        if (!options.ExitTimeout.HasValue)
            return await waitTask.ConfigureAwait(false);

        try
        {
            return await waitTask.WaitAsync(options.ExitTimeout.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("PTY child did not exit within the configured exit timeout.");
        }
    }
}
