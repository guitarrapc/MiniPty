namespace MiniPty.Internal;

/// <summary>
/// Which output driver a platform backend starts (<see cref="IPtyBackend.StartOutput"/>).
/// </summary>
/// <remarks>
/// <para>
/// A platform with a driver that holds no thread uses it by default: Windows reads through I/O completions
/// (<see cref="WindowsOutputDriver"/>), and Linux, macOS, and FreeBSD on the exit watcher's loop (epoll, kqueue). The
/// <see cref="UseReaderThreadsSwitch"/> <see cref="AppContext"/> switch selects the reader-thread driver
/// (<see cref="PtyReaderThread"/>) instead, as a temporary rollback in the field while the new engines are young: set it
/// in the host's runtime configuration, or with <see cref="AppContext.SetSwitch"/> before the first session starts. It
/// is read when each session starts, so changing it affects later sessions only.
/// </para>
/// </remarks>
internal static class PtyOutputDrivers
{
    internal const string UseReaderThreadsSwitch = "MiniPty.UseOutputReaderThreads";

    /// <summary>Whether sessions read output with reader threads: the rollback switch is set.</summary>
    internal static bool UseReaderThreads => AppContext.TryGetSwitch(UseReaderThreadsSwitch, out var enabled) && enabled;
}
