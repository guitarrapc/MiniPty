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

        using var orchestration = session.EnterCompletionOrchestration();
        var pumpTask = pump(session.OutputTransport, cancellationToken);
        await ApplyInputAsync(session, options, cancellationToken).ConfigureAwait(false);
        // Defer CloseTransport on child exit so ConPTY bulk stdout is not truncated; post-exit drain
        // may close the transport early on Windows when output has been quiet long enough.
        var exitCode = await WaitForExitAsync(session, options, cancellationToken).ConfigureAwait(false);
        var output = await PtyOutputDrain.AwaitPumpAsync(
            pumpTask,
            session.OutputTransport,
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
        var exitCode = await WaitForExitAsync(session, options, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Waits for exit. Without an exit timeout this hands back the session's allocation-free wait as is;
    /// only a configured timeout needs a task and a state machine.
    /// </summary>
    private static ValueTask<int> WaitForExitAsync(
        PtySession session,
        PtyCompleteOptions options,
        CancellationToken cancellationToken)
    {
        // The drain below closes the transport itself, after output has been quiet long enough, so the exit
        // wait never closes it: closing at exit would truncate ConPTY bulk output.
        var waitTask = session.WaitForExitForCompletionAsync(cancellationToken, options.KillOnCancellation);
        return options.ExitTimeout is { } exitTimeout
            ? WaitForExitWithTimeoutAsync(waitTask, exitTimeout, cancellationToken)
            : waitTask;
    }

    private static async ValueTask<int> WaitForExitWithTimeoutAsync(
        ValueTask<int> waitTask,
        TimeSpan exitTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await waitTask.AsTask().WaitAsync(exitTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("PTY child did not exit within the configured exit timeout.");
        }
    }
}
