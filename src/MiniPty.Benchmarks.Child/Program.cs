using System.Buffers;
using System.Globalization;

if (!TryParseBytesArg(args, out var byteCount))
{
    Console.Error.WriteLine("Usage: MiniPty.Benchmarks.Child --bytes <count>");
    return 2;
}

if (byteCount == 0)
    return 0;

const int ChunkSize = 4096;
var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
try
{
    Array.Clear(buffer);
    using var stdout = Console.OpenStandardOutput();
    var remaining = byteCount;
    while (remaining > 0)
    {
        var write = Math.Min(remaining, ChunkSize);
        stdout.Write(buffer.AsSpan(0, write));
        remaining -= write;
    }

    return 0;
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}

static bool TryParseBytesArg(string[] args, out int byteCount)
{
    byteCount = 0;
    if (args.Length != 2)
        return false;

    if (!string.Equals(args[0], "--bytes", StringComparison.Ordinal))
        return false;

    return int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out byteCount)
        && byteCount >= 0;
}
