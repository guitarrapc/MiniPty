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
    Array.Clear(buffer, 0, ChunkSize);
    if (OperatingSystem.IsWindows())
        buffer.AsSpan(0, ChunkSize).Fill((byte)'A');

    var remaining = byteCount;
    if (OperatingSystem.IsWindows())
    {
        while (remaining > 0)
        {
            var write = Math.Min(remaining, ChunkSize);
            WriteWindowsConsole(buffer.AsSpan(0, write));
            remaining -= write;
        }
    }
    else
    {
        using var stdout = Console.OpenStandardOutput();
        while (remaining > 0)
        {
            var write = Math.Min(remaining, ChunkSize);
            stdout.Write(buffer.AsSpan(0, write));
            remaining -= write;
        }

        stdout.Flush();
    }

    return 0;
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}

static void WriteWindowsConsole(ReadOnlySpan<byte> data)
{
    // ConPTY children must use console APIs; WriteFile on stdout is not wired to the pseudo console.
    // WriteConsole rejects U+0000, so this benchmark uses printable 'A' bytes on Windows.
    Span<char> chars = stackalloc char[256];
    var offset = 0;
    while (offset < data.Length)
    {
        var chunk = Math.Min(data.Length - offset, chars.Length);
        for (var i = 0; i < chunk; i++)
            chars[i] = (char)data[offset + i];

        Console.Out.Write(chars[..chunk]);
        offset += chunk;
    }

    Console.Out.Flush();
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
