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
        RunWithTransportPumpAsync(session, options, pump, cancellationToken);

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

        using var orchestration = session.EnterCompletionOrchestration();
        session.ResetTransportPumpHandshake();
        var pumpTask = pump(session.OutputTransport, cancellationToken);
        session.WaitForTransportPumpHandshake(TimeSpan.FromSeconds(5), cancellationToken);

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

        using var orchestration = session.EnterCompletionOrchestration();
        var pumpTask = pump(session, cancellationToken);
        await ApplyInputAsync(session, options, cancellationToken).ConfigureAwait(false);
        // Match transport-pump ordering (exit then drain). Defer CloseTransport on Windows until after
        // ReadOutputAsync finishes so BoundedOutputBuffer's producer is not mid-read on a disposed handle.
        var exitCode = await WaitForExitAsync(session, options, cancellationToken, closeTransportOnExit: false).ConfigureAwait(false);
        var output = await PtyOutputDrain.AwaitSessionPumpAsync(
            pumpTask,
            session.CloseOutputTransport,
            options.OutputDrainGrace,
            options.OutputReaderCloseTimeout,
            cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken,
        bool closeTransportOnExit = true)
    {
        var waitTask = session.WaitForExitInternalAsync(cancellationToken, options.KillOnCancellation, closeTransportOnExit);

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
