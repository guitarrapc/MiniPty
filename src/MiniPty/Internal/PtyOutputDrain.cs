namespace MiniPty.Internal;

internal static class PtyOutputDrain
{
    private const int PostExitStallBeforeCloseMs = 100;
    private const int StallPollMs = 10;
    private const int CoalesceMicroWindowMs = 1;

    internal static async Task<T> AwaitPumpAsync<T>(
        Task<T> pump,
        Stream? outputTransport,
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
            if (outputTransport is PtyHandleReadStream or PtyFdReadStream)
            {
                if (TryPostExitQuietDrain(pump, outputTransport, closeOutputTransport, outputDrainGrace, cancellationToken))
                    return await pump.ConfigureAwait(false);
                // Quiet drain consumed grace and closed transport when it returns false.
            }
            else
            {
                if (await WaitForCompletionAsync(pump, outputDrainGrace, cancellationToken).ConfigureAwait(false))
                    return await pump.ConfigureAwait(false);

                closeOutputTransport();
            }
        }

        if (await WaitForCompletionAsync(pump, outputReaderCloseTimeout, cancellationToken).ConfigureAwait(false))
            return await pump.ConfigureAwait(false);

        if (throwOnTimeout)
            throw new TimeoutException("PTY output did not finish draining within the configured timeout.");

        return pump.IsCompleted
            ? await pump.ConfigureAwait(false)
            : throw new InvalidOperationException("PTY output pump did not complete.");
    }

    private static bool TryPostExitQuietDrain<T>(
        Task<T> pump,
        Stream transport,
        Action closeOutputTransport,
        TimeSpan outputDrainGrace,
        CancellationToken cancellationToken)
    {
        var exitObservedAt = Environment.TickCount64;
        var graceDeadline = exitObservedAt + (long)outputDrainGrace.TotalMilliseconds;
        var transportClosed = false;

        while (Environment.TickCount64 < graceDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pump.IsCompleted)
                return true;

            if (ShouldCloseAfterQuietPeriod(exitObservedAt, ReadLastTransportReadTick64(transport)))
            {
                RunMicroWindowQuietCheck(pump, cancellationToken);
                if (pump.IsCompleted)
                    return true;

                if (ShouldCloseAfterQuietPeriod(exitObservedAt, ReadLastTransportReadTick64(transport)))
                {
                    closeOutputTransport();
                    transportClosed = true;
                    break;
                }
            }

            PollSleep(StallPollMs, cancellationToken);
        }

        if (!transportClosed)
        {
            if (pump.IsCompleted)
                return true;

            closeOutputTransport();
        }

        return false;
    }

    private static long ReadLastTransportReadTick64(Stream transport) =>
        transport switch
        {
            PtyHandleReadStream windows => windows.LastTransportReadTick64,
            PtyFdReadStream unix => unix.LastTransportReadTick64,
            _ => 0
        };

    /// <summary>
    /// F3 stall condition: exit observed for at least <see cref="PostExitStallBeforeCloseMs"/> and the last
    /// successful transport read was at least that long ago. A tick of 0 means no read yet and is not quiet.
    /// </summary>
    private static bool ShouldCloseAfterQuietPeriod(long exitObservedAt, long lastReadTick)
    {
        var now = Environment.TickCount64;
        if (now - exitObservedAt < PostExitStallBeforeCloseMs)
            return false;

        if (lastReadTick == 0)
            return false;

        return now - lastReadTick >= PostExitStallBeforeCloseMs;
    }

    private static void RunMicroWindowQuietCheck<T>(
        Task<T> pump,
        CancellationToken cancellationToken)
    {
        var microDeadline = Environment.TickCount64 + CoalesceMicroWindowMs;
        while (Environment.TickCount64 < microDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pump.IsCompleted)
                return;

            PtySleep.Sleep(0);
        }
    }

    /// <summary>
    /// Bounded poll loop that uses a Windows waitable timer when available to keep short post-exit
    /// delays precise without introducing a separate delay task or allocation churn.
    /// </summary>
    private static void PollSleep(int milliseconds, CancellationToken cancellationToken)
    {
        var pollDeadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < pollDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = (int)Math.Min(milliseconds, pollDeadline - Environment.TickCount64);
            if (remaining > 0)
                PtySleep.Sleep(remaining);
        }
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
