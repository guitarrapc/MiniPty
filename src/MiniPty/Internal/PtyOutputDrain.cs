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
            if (await WaitAsync(pump, outputDrainGrace, cancellationToken).ConfigureAwait(false))
                return await pump.ConfigureAwait(false);

            closeOutputTransport();
        }

        if (await WaitAsync(pump, outputReaderCloseTimeout, cancellationToken).ConfigureAwait(false))
            return await pump.ConfigureAwait(false);

        if (throwOnTimeout)
            throw new TimeoutException("PTY output did not finish draining within the configured timeout.");

        return pump.IsCompleted
            ? await pump.ConfigureAwait(false)
            : throw new InvalidOperationException("PTY output pump did not complete.");
    }

    private static async Task<bool> WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
            return true;

        var delay = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        return completed == task;
    }
}
