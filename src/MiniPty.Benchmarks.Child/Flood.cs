using System.Collections.Concurrent;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Flood child for the flood probe (<c>--flood &lt;bytes&gt;</c>): writes printable lines to its terminal as fast as it
/// can, <c>bytes</c> of them and then exits, or until it is ended when <c>bytes</c> is 0.
/// </summary>
/// <remarks>
/// <para>
/// Lines are 77 printable characters from <see cref="Alphabet"/>, shifted by one per line so no two consecutive lines
/// are the same, and never contain <c>%</c>, so a <c>%</c> in the output can only be an echo. On Unix a line ends with
/// <c>\n</c>, which the terminal's default output processing turns into <c>\r\n</c>; on Windows it ends with
/// <c>\r\n</c> written through the console.
/// </para>
/// <para>
/// Echo: on Unix the child never reads its input, so the terminal echoes typed bytes itself (canonical mode with echo,
/// the default), into the same output queue the flood fills. On Windows the console echoes nothing for a program that
/// is not reading, so with <c>MINIPTY_FLOOD_ECHO=1</c> a background thread reads keys and queues, for <c>%</c> followed
/// by a key, <c>%</c>, that key and a line break, which the flood loop writes after its current slice of about 8 KiB.
/// The loop writes the echo itself because a second writer waits on <c>Console.Out</c>'s lock, which the loop takes
/// back at once: echoes written that way came out 1.7 s late at the median, and nearly half not within 6 s. Ctrl+C
/// reaches the child as the terminal's interrupt on both.
/// </para>
/// </remarks>
internal static class Flood
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int Columns = 77;
    private const int LinesPerBlock = 832; // About 64 KiB per write.
    private const int EchoSliceLines = 104; // An eighth of a block, about 8 KiB.

    internal static int Run(long bytes)
    {
        var newline = OperatingSystem.IsWindows() ? "\r\n" : "\n";
        var builder = new StringBuilder(LinesPerBlock * (Columns + newline.Length));
        for (var line = 0; line < LinesPerBlock; line++)
        {
            for (var column = 0; column < Columns; column++)
                builder.Append(Alphabet[(line + column) % Alphabet.Length]);

            builder.Append(newline);
        }

        var block = builder.ToString();
        return OperatingSystem.IsWindows() ? RunWindows(block, bytes) : RunUnix(Encoding.ASCII.GetBytes(block), bytes);
    }

    private static int RunUnix(byte[] block, long bytes)
    {
        using var stdoutHandle = new SafeFileHandle(1, ownsHandle: false);
        using var stdout = new FileStream(stdoutHandle, FileAccess.Write, bufferSize: 0);
        try
        {
            if (bytes == 0)
            {
                while (true)
                    stdout.Write(block);
            }

            var remaining = bytes;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, block.Length);
                stdout.Write(block, 0, count);
                remaining -= count;
            }
        }
        catch (IOException)
        {
            // The terminal was hung up.
            return 1;
        }

        return 0;
    }

    private static int RunWindows(string block, long bytes)
    {
        // ConPTY children must use console APIs; Console.Out writes through the console (see Program.cs).
        var output = Console.Out;
        ConcurrentQueue<string>? echoes = null;
        if (Environment.GetEnvironmentVariable("MINIPTY_FLOOD_ECHO") == "1")
        {
            var queue = echoes = new ConcurrentQueue<string>();
            new Thread(() =>
            {
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.KeyChar != '%')
                        continue;

                    var next = Console.ReadKey(intercept: true);
                    queue.Enqueue(string.Concat("%", next.KeyChar.ToString(), "\r\n"));
                }
            })
            { IsBackground = true }.Start();
        }

        // Whole blocks without echo, as the throughput runs measured; slices of whole lines with it.
        var slice = echoes is null ? block.Length : EchoSliceLines * (Columns + 2);
        var remaining = bytes == 0 ? long.MaxValue : bytes;
        var offset = 0;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, Math.Min(slice, block.Length - offset));
            output.Write(block.AsSpan(offset, count));
            offset = (offset + count) % block.Length;
            if (bytes != 0)
                remaining -= count;

            while (echoes is not null && echoes.TryDequeue(out var echo))
                output.Write(echo);
        }

        output.Flush();
        return 0;
    }
}
