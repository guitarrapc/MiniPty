using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;

namespace MiniPty.Tests;

/// <summary>
/// Two loads of MiniPty in one process, as a plugin host makes with a second <see cref="AssemblyLoadContext"/>. Each load
/// runs its own exit watchers and its own output loop: on Unix one thread with its own epoll or kqueue, on Windows one
/// thread on a completion port of its own. A watcher or loop shared between loads hands one load's events to the other
/// load's thread, which knows none of its sessions.
/// </summary>
/// <remarks>
/// A second load context exists only under the JIT, which runs this suite. The warnings suppressed here are about trimming
/// and single-file publishing, which these tests never meet.
/// </remarks>
#pragma warning disable IL2026, IL2072, IL2075, IL2077, IL2080, IL3000
public sealed class PtyLoadContextTests
{
    private const int PerContext = 8;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every child of each load must be reaped and reported. Children that all exit at once made a Unix watcher kept in the
    /// native library hand some exits to the other load's thread, so those sessions never reported their exit.
    /// </summary>
    [Test]
    public async Task SessionsInTwoLoadContextsAllReportExit()
    {
        var other = new OtherLoad(nameof(SessionsInTwoLoadContextsAllReportExit));
        var sessions = new List<IAsyncDisposable>();
        var exits = new List<Task>();
        try
        {
            for (var i = 0; i < PerContext; i++)
            {
                var session = other.Start(ShortChild(null));
                sessions.Add((IAsyncDisposable)session);
                exits.Add((Task)session.GetType().GetMethod(nameof(PtySession.WaitForExitAsync))!.Invoke(session, [CancellationToken.None])!);
            }

            for (var i = 0; i < PerContext; i++)
            {
                var session = Pty.Start(ShortChild(null));
                sessions.Add(session);
                exits.Add(session.WaitForExitAsync());
            }

            // WaitAsync(TimeSpan) so a lost exit surfaces as TimeoutException.
            await Task.WhenAll(exits).WaitAsync(Timeout);
        }
        finally
        {
            foreach (var session in sessions)
                await session.DisposeAsync();
        }
    }

    /// <summary>
    /// Each load's output loop reads its own sessions, and only those: every session's one-shot result holds its own
    /// marker. A loop whose port or poll set another load also used would take that load's completions or readiness
    /// reports, for sessions it does not know.
    /// </summary>
    [Test]
    public async Task SessionsInTwoLoadContextsAllReadTheirOwnOutput()
    {
        var other = new OtherLoad(nameof(SessionsInTwoLoadContextsAllReadTheirOwnOutput));
        var sessions = new List<IAsyncDisposable>();
        var results = new List<(string Marker, Task<string> Output)>();
        try
        {
            for (var i = 0; i < PerContext; i++)
            {
                var marker = $"minipty-other-load-{i}";
                var session = other.Start(ShortChild(marker));
                sessions.Add((IAsyncDisposable)session);
                results.Add((marker, OtherLoad.CompleteAsync(session)));
            }

            for (var i = 0; i < PerContext; i++)
            {
                var marker = $"minipty-this-load-{i}";
                var session = Pty.Start(ShortChild(marker));
                sessions.Add(session);
                results.Add((marker, CompleteAsync(session)));
            }

            await Task.WhenAll(results.Select(r => r.Output)).WaitAsync(Timeout);
            foreach (var (marker, output) in results)
            {
                // ConPTY puts the command line, marker and all, in a window title sequence; only the echo counts.
                var text = Regex.Replace(await output, "\x1b\\][^\x07\x1b]*(\x07|\x1b\\\\)", "");
                await Assert.That(text).Contains(marker);
                foreach (var (otherMarker, _) in results)
                {
                    if (otherMarker != marker)
                        await Assert.That(text).DoesNotContain(otherMarker);
                }
            }
        }
        finally
        {
            foreach (var session in sessions)
                await session.DisposeAsync();
        }
    }

    /// <summary>A child that prints <paramref name="marker"/>, if any, and exits at once.</summary>
    private static PtyStartInfo ShortChild(string? marker)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            return new PtyStartInfo { FileName = cmd, Arguments = marker is null ? ["/c", "exit 0"] : ["/c", "echo " + marker] };
        }

        // Unix children sleep a little, so they all exit at once while every session is registered.
        return new PtyStartInfo { FileName = "sh", Arguments = ["-c", marker is null ? "sleep 0.5" : $"sleep 0.5; echo {marker}"] };
    }

    private static async Task<string> CompleteAsync(PtySession session) =>
        Encoding.UTF8.GetString((await session.CompleteAsync()).Output.Span);

    /// <summary>MiniPty loaded a second time, into a load context of its own, used through reflection.</summary>
    private sealed class OtherLoad
    {
        private readonly Type _startInfoType;
        private readonly MethodInfo _start;

        public OtherLoad(string name)
        {
            var assembly = new AssemblyLoadContext(name).LoadFromAssemblyPath(typeof(Pty).Assembly.Location);
            _startInfoType = assembly.GetType("MiniPty.PtyStartInfo", throwOnError: true)!;
            _start = assembly.GetType("MiniPty.Pty", throwOnError: true)!.GetMethod(nameof(Pty.Start), [_startInfoType])!;
        }

        /// <summary>Starts a session in the other load with what <paramref name="startInfo"/> of this load names.</summary>
        public object Start(PtyStartInfo startInfo)
        {
            var other = Activator.CreateInstance(_startInfoType)!;
            _startInfoType.GetProperty(nameof(PtyStartInfo.FileName))!.SetValue(other, startInfo.FileName);
            _startInfoType.GetProperty(nameof(PtyStartInfo.Arguments))!.SetValue(other, startInfo.Arguments.ToArray());
            return _start.Invoke(null, [other])!;
        }

        /// <summary>A session of the other load's one-shot result, decoded.</summary>
        public static async Task<string> CompleteAsync(object session)
        {
            var complete = session.GetType().GetMethod(nameof(PtySession.CompleteAsync))!;
            var task = (Task)complete.Invoke(session, [null, CancellationToken.None])!;
            await task;
            var result = task.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(task)!;
            var output = (ReadOnlyMemory<byte>)result.GetType().GetProperty(nameof(PtyResult.Output))!.GetValue(result)!;
            return Encoding.UTF8.GetString(output.Span);
        }
    }
}
#pragma warning restore IL2026, IL2072, IL2075, IL2077, IL2080, IL3000
