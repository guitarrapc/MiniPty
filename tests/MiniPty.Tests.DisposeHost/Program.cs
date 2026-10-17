using System.Text;
using MiniPty;

// Starts a session whose child ignores SIGHUP, prints the child's pid once the child is ready, disposes the session, and
// exits at once. The first argument names how it exits: returning from Main, calling Environment.Exit, or returning
// first and disposing from its own exit handler. The child loops for about 30 s, and MiniPty's child watcher stops
// when this process exits, so a child that is gone right after this process exits was killed during exit.
var mode = args.Length > 0 ? args[0] : "return";
var session = Pty.Start(new PtyStartInfo
{
    FileName = "sh",
    Arguments = ["-c", "trap '' HUP; printf ready; i=0; while [ $i -lt 300 ]; do sleep 0.1; i=$((i+1)); done"],
});

var output = new StringBuilder();
await foreach (var chunk in session.ReadOutputAsync())
{
    output.Append(Encoding.UTF8.GetString(chunk.Data.Span));
    if (output.ToString().Contains("ready", StringComparison.Ordinal))
        break;
}

Console.WriteLine(session.ProcessId);
Console.Out.Flush();

switch (mode)
{
    case "return":
        session.Dispose();
        return 0;
    case "exit":
        session.Dispose();
        Environment.Exit(0);
        return 0;
    case "exit-handler":
        // Subscribed after Pty.Start, so it runs after MiniPty's own exit handler. The disposal then has to kill at once,
        // because the deadline it would otherwise wait for may never come.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => session.Dispose();
        return 0;
    default:
        Console.Error.WriteLine($"Unknown mode '{mode}'.");
        return 2;
}
