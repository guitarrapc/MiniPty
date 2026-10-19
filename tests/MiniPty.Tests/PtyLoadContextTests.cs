using System.Runtime.Loader;

namespace MiniPty.Tests;

public sealed class PtyLoadContextTests
{
    /// <summary>
    /// Two loads of MiniPty in one process, as a plugin host makes with a second <see cref="AssemblyLoadContext"/>,
    /// share the native library but each runs its own exit watcher. Every child of each load must still be reaped and
    /// reported. Children that all exit at once make a watcher kept in the native library hand some exits to the
    /// other load's thread, which knows none of them, so those sessions never report their exit.
    /// </summary>
    [Test]
    // A second load context exists only under the JIT, which runs this suite. The warnings are about trimming and
    // single-file publishing, which this test never meets.
#pragma warning disable IL2026, IL2072, IL2075, IL3000
    public async Task SessionsInTwoLoadContextsAllReportExit()
    {
        if (OperatingSystem.IsWindows())
            return;

        const int PerContext = 8;
        var other = new AssemblyLoadContext(nameof(SessionsInTwoLoadContextsAllReportExit));
        var assembly = other.LoadFromAssemblyPath(typeof(Pty).Assembly.Location);
        var startInfoType = assembly.GetType("MiniPty.PtyStartInfo", throwOnError: true)!;
        var start = assembly.GetType("MiniPty.Pty", throwOnError: true)!.GetMethod(nameof(Pty.Start), [startInfoType])!;

        var sessions = new List<IAsyncDisposable>();
        var exits = new List<Task>();
        try
        {
            for (var i = 0; i < PerContext; i++)
            {
                var startInfo = Activator.CreateInstance(startInfoType)!;
                startInfoType.GetProperty(nameof(PtyStartInfo.FileName))!.SetValue(startInfo, "sh");
                startInfoType.GetProperty(nameof(PtyStartInfo.Arguments))!.SetValue(startInfo, new[] { "-c", "sleep 0.5" });
                var session = start.Invoke(null, [startInfo])!;
                sessions.Add((IAsyncDisposable)session);
                exits.Add((Task)session.GetType().GetMethod(nameof(PtySession.WaitForExitAsync))!.Invoke(session, [CancellationToken.None])!);
            }

            for (var i = 0; i < PerContext; i++)
            {
                var session = Pty.Start(new PtyStartInfo { FileName = "sh", Arguments = ["-c", "sleep 0.5"] });
                sessions.Add(session);
                exits.Add(session.WaitForExitAsync());
            }

            // WaitAsync(TimeSpan) so a lost exit surfaces as TimeoutException.
            await Task.WhenAll(exits).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            foreach (var session in sessions)
                await session.DisposeAsync();
        }
    }
#pragma warning restore IL2026, IL2072, IL2075, IL3000
}
