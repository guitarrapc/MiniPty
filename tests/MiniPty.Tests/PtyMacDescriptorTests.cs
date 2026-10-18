using System.Diagnostics;
using System.Runtime.InteropServices;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// On macOS no child the host starts can inherit a descriptor of a session, not even one forked on another thread
/// while MiniPty opens it. The master is opened with <c>O_CLOEXEC</c>. macOS has no <c>pipe2</c>, so the wake
/// descriptor each output reader thread opens is a kqueue, which a fork child never inherits, rather than a pipe marked
/// close-on-exec only after it opened. .NET's <c>Process.Start</c> passes on every descriptor not marked, so a pipe
/// caught unmarked would stay open in that child.
/// </summary>
public sealed partial class PtyMacDescriptorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The instant between opening a descriptor and marking it cannot be hit on purpose, so the test recreates it: it
    /// opens a wake descriptor the way a new reader thread does and duplicates it, since <c>dup</c> leaves the copy
    /// unmarked, and a child the host then starts must find none of the copies.
    /// </summary>
    /// <remarks>
    /// The primitive is opened directly because a session opens one only when no parked reader thread is left, and
    /// parked threads from earlier tests outlast any fixed number of sessions started here. The exit watcher's kqueue
    /// opens once per process, before this test can look, so it is not covered here.
    /// </remarks>
    [Test]
    public async Task AReaderWakeDescriptorIsNotInheritedEvenWhileUnmarked()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        await Assert.That(OpenWake(out var read, out var write)).IsEqualTo(0);

        var unmarked = new List<int>();
        try
        {
            foreach (var fd in read == write ? [read] : new[] { read, write })
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
            UnixInterop.minipty_wake_close(read, write);
        }
    }

    /// <summary>Opens a wake descriptor pair as <see cref="PtyReaderThread"/> does; returns the native result.</summary>
    private static unsafe int OpenWake(out int read, out int write)
    {
        int r;
        int w;
        var result = UnixInterop.minipty_wake_open(&r, &w);
        read = r;
        write = w;
        return result;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}
