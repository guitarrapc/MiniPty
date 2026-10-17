namespace MiniPty.Tests;

/// <summary>
/// A file of numbered lines for Windows children that must flood the console quickly.
/// </summary>
/// <remarks>
/// <para>
/// Every line a child writes is a frame the console host renders and relays through the output pipe, so a flood
/// costs one chain of cross-process wake-ups per line, whatever the line holds, and under CPU oversubscription each
/// frame took about 8 ms: 20,000 short lines drained in 0.3 s on an idle 32-core desktop and outlasted a 30 s test
/// timeout both there with 48 busy processes and on a hosted ARM64 runner. Tests therefore flood few, wide lines:
/// the bytes back up the ring and the pipe several times over, and the frames stay in the hundreds.
/// </para>
/// <para>
/// <c>for /L ... do @echo</c> adds a console write per line on top of that and was four times slower still, so
/// the lines are written once to a file and the child <c>type</c>s it. The file is created once per process and
/// deleted when the process exits.
/// </para>
/// </remarks>
internal static class WindowsFloodFile
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<(int Lines, int Width), string> s_files = new();

    /// <summary>
    /// A cmd.exe command that writes <paramref name="lines"/> lines of <c>flood&lt;n&gt;</c> to the console, each
    /// padded to <paramref name="width"/> characters when that is longer.
    /// </summary>
    public static string TypeCommand(int lines, int width = 0) => $"type \"{PathFor(lines, width)}\"";

    private static string PathFor(int lines, int width)
    {
        lock (s_lock)
        {
            if (s_files.TryGetValue((lines, width), out var existing))
                return existing;

            var path = Path.Combine(Path.GetTempPath(), $"minipty-tests-flood-{lines}x{width}-{Environment.ProcessId}.txt");
            using (var writer = new StreamWriter(path))
            {
                for (var i = 1; i <= lines; i++)
                {
                    var line = "flood" + i;
                    writer.Write(width > line.Length ? line.PadRight(width, 'x') : line);
                    writer.Write("\r\n");
                }
            }

            if (s_files.Count == 0)
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => DeleteAll();

            s_files[(lines, width)] = path;
            return path;
        }
    }

    private static void DeleteAll()
    {
        lock (s_lock)
        {
            foreach (var path in s_files.Values)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A child that still has it open; the temp directory is cleaned up by other means.
                }
            }
        }
    }
}
