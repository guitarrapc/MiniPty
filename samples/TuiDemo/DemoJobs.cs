internal enum JobStatus : byte { Queued, Running, Done, Failed }

internal readonly record struct JobState(JobStatus Status, int Progress, int Run);

/// <summary>
/// Simulated jobs driven by the animation frame (one frame per <see cref="FrameMilliseconds"/>). A job's state is a
/// pure function of the frame, so the renderer can diff two frames and tests can predict the screen without any
/// shared mutable state. Every job loops forever: queued until its start, then runs that finish or fail, each held
/// on screen for a while before the next run.
/// </summary>
internal static class DemoJobs
{
    internal const int Count = 3;
    internal const int FrameMilliseconds = 100;
    private const int HoldFrames = 20;

    private static readonly string[] Names = ["Build", "Test", "Deploy"];

    internal static string Name(int job) => Names[job];

    internal static JobState Get(int job, long frame)
    {
        var (start, duration) = job switch { 0 => (0, 80), 1 => (15, 120), _ => (30, 60) };
        if (frame < start)
            return new(JobStatus.Queued, 0, 0);

        var elapsed = frame - start;
        var run = (int)(elapsed / (duration + HoldFrames)) + 1;
        var phase = (int)(elapsed % (duration + HoldFrames));
        // Test fails at 70 % on every second run, so a red state shows up regularly.
        var stop = job == 1 && run % 2 == 0 ? duration * 7 / 10 : duration;
        if (phase < stop)
            return new(JobStatus.Running, phase * 100 / duration, run);
        return stop < duration
            ? new(JobStatus.Failed, stop * 100 / duration, run)
            : new(JobStatus.Done, 100, run);
    }

    /// <summary>The log line a job produces when the clock reaches <paramref name="frame"/>, if any.</summary>
    internal static bool TryGetEvent(int job, long frame, out DemoLogEntry entry)
    {
        var previous = Get(job, frame - 1);
        var current = Get(job, frame);
        var kind = current.Status != previous.Status
            ? current.Status switch
            {
                JobStatus.Running => DemoLogKind.Started,
                JobStatus.Done => DemoLogKind.Done,
                JobStatus.Failed => DemoLogKind.Failed,
                _ => DemoLogKind.None,
            }
            : current.Status == JobStatus.Running && previous.Progress < 50 && current.Progress >= 50
                ? DemoLogKind.Half
                : DemoLogKind.None;
        entry = new(frame, job, kind, kind == DemoLogKind.Failed ? current.Progress : current.Run);
        return kind != DemoLogKind.None;
    }
}

internal enum DemoLogKind : byte { None, Started, Half, Done, Failed }

/// <summary>A log line kept as data and formatted only when drawn, so logging allocates nothing.</summary>
internal readonly record struct DemoLogEntry(long Frame, int Job, DemoLogKind Kind, int Value);

/// <summary>Spinner styles shown side by side; fonts and terminals differ in which glyphs they draw.</summary>
internal static class DemoSpinners
{
    internal const int Count = 5;

    private static readonly string[][] Frames =
    [
        ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"],
        ["|", "/", "-", "\\"],
        ["▖", "▘", "▝", "▗"],
        [".  ", ".. ", "...", "   "],
        ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█", "▇", "▆", "▅", "▄", "▃", "▂"],
    ];

    private static readonly int[] FramesPerStep = [1, 1, 2, 3, 1];
    private static readonly string[] Labels = ["braille", "ascii", "quadrant", "dots", "bar"];
    private static readonly string[] Colors = ["\u001b[36m", "\u001b[97m", "\u001b[35m", "\u001b[33m", "\u001b[32m"];

    internal static string Glyph(int spinner, long frame)
    {
        var frames = Frames[spinner];
        return frames[(int)(frame / FramesPerStep[spinner] % frames.Length)];
    }

    internal static string Label(int spinner) => Labels[spinner];

    internal static string Color(int spinner) => Colors[spinner];
}
