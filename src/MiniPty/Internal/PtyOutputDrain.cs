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
    /// Waits for <paramref name="task"/> to complete within <paramref name="timeout"/> without allocating a separate delay task.
    /// </summary>
    private static async Task<bool> WaitForCompletionAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
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
