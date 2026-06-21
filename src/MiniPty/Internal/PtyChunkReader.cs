using System.Diagnostics;
using System.Text;

namespace MiniPty.Internal;

internal static class PtyChunkReader
{
    internal static List<PtyOutputChunk> Read(Stream stream, Stopwatch stopwatch, Encoding outputEncoding)
    {
        var chunks = new List<PtyOutputChunk>();
        var bytes = new byte[4096];
        var chars = new char[outputEncoding.GetMaxCharCount(bytes.Length)];
        var decoder = outputEncoding.GetDecoder();

        while (true)
        {
            var read = stream.Read(bytes, 0, bytes.Length);
            if (read <= 0)
                break;

            var charCount = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            if (charCount > 0)
                chunks.Add(new PtyOutputChunk(stopwatch.Elapsed.TotalSeconds, new string(chars, 0, charCount)));
        }

        var trailing = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
        if (trailing > 0)
            chunks.Add(new PtyOutputChunk(stopwatch.Elapsed.TotalSeconds, new string(chars, 0, trailing)));

        return chunks;
    }
}

internal static class PtyOutputDrain
{
    internal static async Task<IReadOnlyList<PtyOutputChunk>> AwaitPumpAsync(
        Task<List<PtyOutputChunk>> pump,
        Action closeOutputTransport,
        TimeSpan drainTimeout,
        TimeSpan closeGrace,
        bool throwOnTimeout,
        bool transportAlreadyClosed,
        CancellationToken cancellationToken)
    {
        if (pump.IsCompleted)
            return pump.GetAwaiter().GetResult();

        if (!transportAlreadyClosed)
        {
            if (await WaitAsync(pump, drainTimeout, cancellationToken))
                return pump.GetAwaiter().GetResult();

            closeOutputTransport();
        }

        if (await WaitAsync(pump, closeGrace, cancellationToken))
            return pump.GetAwaiter().GetResult();

        if (throwOnTimeout)
            throw new TimeoutException("PTY output did not finish draining within the configured timeout.");

        return pump.IsCompleted ? pump.GetAwaiter().GetResult() : [];
    }

    private static async Task<bool> WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
            return true;

        var delay = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(task, delay);
        return completed == task;
    }
}
