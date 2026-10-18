using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MiniPty.Tests;

/// <summary>
/// On macOS no child the host starts can inherit a descriptor of a session, not even one forked on another thread
/// while MiniPty opens it. The master is opened with <c>O_CLOEXEC</c>. macOS has no <c>pipe2</c>, so every other
/// descriptor MiniPty keeps is a kqueue, which a fork child never inherits, rather than a pipe marked close-on-exec
/// only after it opened. .NET's <c>Process.Start</c> passes on every descriptor not marked, so a pipe caught unmarked
/// would stay open in that child.
/// </summary>
public sealed partial class PtyMacDescriptorTests
{
    private const int FGetFd = 1;
    private const int MaxSessions = 64;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The instant between opening a descriptor and marking it cannot be hit on purpose, so the test recreates it: it
    /// duplicates each descriptor a new session added other than its terminal, since <c>dup</c> leaves the copy
    /// unmarked, and a child the host then starts must find none of the copies. Sessions are started and kept running
    /// until one has added a descriptor, so reader threads parked by earlier tests are used up and a new one opens its
    /// wake descriptor. A first session ends before the count, so the assemblies a start loads, which the runtime keeps
    /// open as plain files, are already open by then.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task DescriptorsASessionAddsAreNotInheritedEvenWhileUnmarked()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        await using (Pty.Start(Blocks()))
        {
        }

        var before = OpenDescriptors();
        var sessions = new List<PtySession>();
        var added = new List<int>();
        var unmarked = new List<int>();
        try
        {
            while (added.Count == 0 && sessions.Count < MaxSessions)
            {
                sessions.Add(Pty.Start(Blocks()));
                foreach (var fd in OpenDescriptors())
                {
                    if (!before.Contains(fd) && isatty(fd) == 0 && !added.Contains(fd))
                        added.Add(fd);
                }
            }

            await Assert.That(added.Count).IsGreaterThan(0).Because($"no descriptor appeared after {sessions.Count} sessions");

            foreach (var fd in added)
            {
                var copy = dup(fd);
                await Assert.That(copy).IsGreaterThanOrEqualTo(0);
                unmarked.Add(copy);
            }

            var probe = string.Join(' ', unmarked);
            using var child = Process.Start(new ProcessStartInfo("sh", ["-c", $"for i in {probe}; do if [ -e /dev/fd/$i ]; then printf '%s ' $i; fi; done"])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var inherited = (await child.StandardOutput.ReadToEndAsync().WaitAsync(Timeout)).Trim();
            await child.WaitForExitAsync().WaitAsync(Timeout);

            await Assert.That(inherited).IsEmpty().Because($"the child inherited {inherited} of {probe}");
        }
        finally
        {
            foreach (var fd in unmarked)
                close(fd);
            foreach (var session in sessions)
                await session.DisposeAsync();
        }
    }

    /// <summary>Waits on stdin, which nobody writes, so the session keeps its reader thread until it is disposed.</summary>
    private static PtyStartInfo Blocks() => new() { FileName = "sh", Arguments = ["-c", "IFS= read -r _"] };

    private static HashSet<int> OpenDescriptors()
    {
        var fds = new HashSet<int>();
        foreach (var entry in Directory.GetFileSystemEntries("/dev/fd"))
        {
            // The listing's own directory descriptor is gone once it returns, so only descriptors still open count.
            if (int.TryParse(Path.GetFileName(entry), out var fd) && fd > 2 && fcntl(fd, FGetFd, 0) >= 0)
                fds.Add(fd);
        }

        return fds;
    }

    // Only F_GETFD, which takes no argument: fcntl is variadic, and Apple arm64 passes variadic arguments on the stack.
    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc")]
    private static partial int isatty(int fd);
}
