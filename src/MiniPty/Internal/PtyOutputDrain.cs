namespace MiniPty.Internal;

internal static class PtyOutputDrain
{
    internal static async Task<T> AwaitPumpAsync<T>(
        Task<T> pump,
        Action closeOutputTransport,
        TimeSpan outputDrainGrace,
        TimeSpan outputReaderCloseTimeout,
        bool throwOnTimeout,
        bool transportAlreadyClosed,
        CancellationToken cancellationToken)
    {
        if (pump.IsCompleted)
            return await pump.ConfigureAwait(false);

        if (!transportAlreadyClosed)
        {
            if (await WaitForCompletionAsync(pump, outputDrainGrace, cancellationToken).ConfigureAwait(false))
                return await pump.ConfigureAwait(false);

            closeOutputTransport();
        }

        if (await WaitForCompletionAsync(pump, outputReaderCloseTimeout, cancellationToken).ConfigureAwait(false))
            return await pump.ConfigureAwait(false);

        if (throwOnTimeout)
            throw new TimeoutException("PTY output did not finish draining within the configured timeout.");

        return pump.IsCompleted
            ? await pump.ConfigureAwait(false)
            : throw new InvalidOperationException("PTY output pump did not complete.");
    }

    /// <summary>
    /// Drains a <see cref="PtySession.ReadOutputAsync"/> pump after child exit.
    /// Unlike the transport pump, forcing transport close while the bounded producer is still
    /// reading can truncate capture on slow runners, so an extra pre-close wait is allowed.
    /// </summary>
    internal static async Task<T> AwaitSessionPumpAsync<T>(
        Task<T> pump,
        Action closeOutputTransport,
        TimeSpan outputDrainGrace,
        TimeSpan outputReaderCloseTimeout,
        CancellationToken cancellationToken)
    {
        if (pump.IsCompleted)
            return await pump.ConfigureAwait(false);

        if (await WaitForCompletionAsync(pump, outputDrainGrace, cancellationToken).ConfigureAwait(false))
            return await pump.ConfigureAwait(false);

        // Still draining after grace: keep waiting without closing so CI/slow consumers can finish
        // copying bytes out of BoundedOutputBuffer before post-exit transport close.
        if (await WaitForCompletionAsync(pump, outputReaderCloseTimeout, cancellationToken).ConfigureAwait(false))
            return await pump.ConfigureAwait(false);

        closeOutputTransport();

        if (await WaitForCompletionAsync(pump, outputReaderCloseTimeout, cancellationToken).ConfigureAwait(false))
            return await pump.ConfigureAwait(false);

        throw new TimeoutException("PTY output did not finish draining within the configured timeout.");
    }

    /// <summary>
    /// Waits for <paramref name="task"/> to complete within <paramref name="timeout"/> without allocating a separate delay task.
    /// </summary>
    internal static async Task<bool> WaitForCompletionAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
            return true;

        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
