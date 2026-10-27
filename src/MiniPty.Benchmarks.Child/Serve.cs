using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Persistent child for the steady-state benchmarks (<c>--serve &lt;bytes&gt;</c>): it stays running and answers each
/// input byte, so one session serves every operation and spawn stays out of the measurement.
/// </summary>
/// <remarks>
/// <para>
/// Protocol, kept in sync with <c>BenchmarkServeSession</c> in MiniPty.Benchmarks: after setup the child writes
/// <see cref="Ready"/>. Then <c>g</c> writes the flood (the requested number of bytes as 77-character lines) followed
/// by <see cref="EndA"/> on a line of its own, <c>h</c> the same flood followed by <see cref="EndB"/>, and any other
/// byte is echoed back after a carriage return, so the echo always lands in column 0.
/// </para>
/// <para>
/// The answers alternate because ConPTY sends only what changed on its screen and may repaint what is already there:
/// the previous marker or echo can reappear in the output, but never the one the benchmark waits for next. For the same
/// reason an echo overwrites column 0 instead of extending a line, so every echo changes a cell.
/// </para>
/// <para>
/// Input is raw so that the terminal neither waits for a line nor echoes on its own: on Unix the child runs
/// <c>stty raw -echo</c>, and on Windows it reads key events, which the console never echoes.
/// </para>
/// </remarks>
internal static class Serve
{
    private const string Ready = "#MINIPTY-READY#";
    private const string EndA = "#MINIPTY-END-A#";
    private const string EndB = "#MINIPTY-END-B#";
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int LineLength = 79; // 77 characters plus CR LF: fits an 80-column terminal without wrapping.

    internal static int Run(int floodBytes)
    {
        var floodA = BuildFlood(floodBytes, EndA);
        var floodB = BuildFlood(floodBytes, EndB);
        return OperatingSystem.IsWindows() ? RunWindows(floodA, floodB) : RunUnix(floodA, floodB);
    }

    /// <summary>
    /// <paramref name="floodBytes"/> bytes of printable lines, then <paramref name="marker"/> on a line of its own.
    /// </summary>
    /// <remarks>Lines shift through the alphabet so no two consecutive lines are the same, and none holds a '#'.</remarks>
    private static string BuildFlood(int floodBytes, string marker)
    {
        var builder = new StringBuilder(floodBytes + marker.Length + 4);
        for (var i = 0; i < floodBytes; i++)
        {
            var column = i % LineLength;
            builder.Append(column switch
            {
                LineLength - 2 => '\r',
                LineLength - 1 => '\n',
                _ => Alphabet[(i / LineLength + column) % Alphabet.Length],
            });
        }

        return builder.Append("\r\n").Append(marker).Append("\r\n").ToString();
    }

    private static int RunUnix(string floodA, string floodB)
    {
        using (var stty = Process.Start(new ProcessStartInfo("stty", ["raw", "-echo"]) { UseShellExecute = false }))
        {
            if (stty is null)
                return 3;

            stty.WaitForExit();
            if (stty.ExitCode != 0)
                return 3;
        }

        var answerA = Encoding.ASCII.GetBytes(floodA);
        var answerB = Encoding.ASCII.GetBytes(floodB);

        // Descriptors 0 and 1 directly, not Console streams: on a terminal those initialize .NET's terminal handling,
        // which writes terminfo strings, switches the modes back from what stty set, and reads input a line at a time.
        using var stdinHandle = new SafeFileHandle(0, ownsHandle: false);
        using var stdoutHandle = new SafeFileHandle(1, ownsHandle: false);
        using var stdin = new FileStream(stdinHandle, FileAccess.Read, bufferSize: 0);
        using var stdout = new FileStream(stdoutHandle, FileAccess.Write, bufferSize: 0);
        stdout.Write(Encoding.ASCII.GetBytes(Ready));

        var input = new byte[64];
        var echo = new byte[] { (byte)'\r', 0 };
        while (true)
        {
            var read = stdin.Read(input, 0, input.Length);
            if (read <= 0)
                return 0;

            for (var i = 0; i < read; i++)
            {
                switch (input[i])
                {
                    case (byte)'g':
                        stdout.Write(answerA);
                        break;
                    case (byte)'h':
                        stdout.Write(answerB);
                        break;
                    default:
                        echo[1] = input[i];
                        stdout.Write(echo);
                        break;
                }
            }
        }
    }

    private static int RunWindows(string floodA, string floodB)
    {
        // ConPTY children must use console APIs; Console.Out writes through the console (see Program.cs).
        var output = Console.Out;
        output.Write(Ready);
        var echo = new char[] { '\r', '\0' };
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.KeyChar)
            {
                case 'g':
                    output.Write(floodA);
                    break;
                case 'h':
                    output.Write(floodB);
                    break;
                default:
                    echo[1] = key.KeyChar;
                    output.Write(echo);
                    break;
            }
        }
    }
}
