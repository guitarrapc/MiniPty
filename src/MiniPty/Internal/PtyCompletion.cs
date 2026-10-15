namespace MiniPty.Internal;

/// <summary>
/// One-shot completion shared by <see cref="PtySession.CompleteAsync"/> and <c>PtyCapture.RunAsync</c>: route
/// output into a sink, apply input, wait for exit, then wait for end of output within the drain deadlines.
/// </summary>
/// <remarks>
/// Output flows into the sink on the session's reader thread from the moment it attaches, so nothing waits for
/// output on the caller's thread and the exit wait and the drain wait hold no thread. The caller owns the
/// session's single-consumer ownership for the duration.
/// </remarks>
internal static class PtyCompletion
{
    internal static async Task<int> RunAsync(
        PtySession session,
        PtyCompleteOptions options,
        PtyOutputSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);

        var pump = session.OutputPump;
        pump.AttachSink(sink);
        try
        {
            await ApplyInputAsync(session, options, cancellationToken).ConfigureAwait(false);
            var exitCode = await WaitForExitAsync(session, options, cancellationToken).ConfigureAwait(false);
            while (!await sink.WaitForDrainAsync(pump, options.OutputDrainGrace, options.OutputReaderCloseTimeout, cancellationToken).ConfigureAwait(false))
            {
            }

            return exitCode;
        }
        catch
        {
            // Output read from here on stays in the ring for whichever consumer comes next. Detaching waits out a
            // write in progress, so the sink's pooled buffers can be returned right after.
            pump.DetachSink(sink);
            sink.Abort();
            throw;
        }
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
