#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property AllowUnsafeBlocks=true
#:property PublishAot=true
#:project ../../src/MiniPty/MiniPty.csproj

// Interactive: drive a running program through a PTY, the way a test harness or an "expect" script does.
//
//   dotnet samples/SingleFiles/Interactive.cs
//
// One reader takes the session's output for its whole life. The code waits until the child prints a prompt, answers
// it, resizes the terminal while the child runs, and lets the child exit on its own.

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using MiniPty;

// The Enter key: a PTY treats CR as Enter on every platform (see Session.cs).
const string Enter = "\r";

if (!Pty.IsSupported)
{
    Console.Error.WriteLine("PTY is not supported on this operating system.");
    return 1;
}

try
{
    await RunConversationAsync();
    Console.Error.WriteLine("ok Interactive sample: ReadOutputAsync reader, prompted writes, Resize, natural exit");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

static async Task RunConversationAsync()
{
    Console.Error.WriteLine("=== Prompted conversation over ReadOutputAsync ===");

    // One deadline for the whole conversation, so a child that never prints what we wait for cannot hang the sample.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await using var session = Pty.Start(CreateInteractiveStartInfo());

    // Start the reader before the first write and keep it running to the end. It is the session's only output
    // consumer; the writes below happen while it reads.
    var output = new OutputLog();
    var reader = output.ReadAllAsync(session, cts.Token);

    await output.WaitForAsync("ready", cts.Token);
    await session.WriteInputAsync("alpha" + Enter, cancellationToken: cts.Token);
    await output.WaitForAsync("first:alpha", cts.Token);

    // Resize while the child runs. On Unix the child's `stty size` below reports the new size.
    session.Resize(new PtySize(100, 28));
    Console.Error.WriteLine($"parent reports {session.Size.Columns}x{session.Size.Rows}");

    await session.WriteInputAsync("beta" + Enter, cancellationToken: cts.Token);
    var finalMarker = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? "second:beta"
        : "second:beta size:28 100";
    await output.WaitForAsync(finalMarker, cts.Token);

    // The reader ends at end of output, which follows the child's exit.
    await reader;
    var status = await session.WaitForExitAsync(cts.Token);

    var text = output.Text;
    Console.Error.WriteLine($"pid={session.ProcessId} exit={status.ExitCode}");
    // On Windows, ConPTY repaints the screen after a resize, so the earlier lines appear a second time. A terminal
    // frontend draws that repaint over the same cells; as plain text it reads as a repeat.
    Console.Error.WriteLine("child output (plain):");
    Console.Error.WriteLine(PtyOutput.ToDisplayText(text, PtyOutputDisplayMode.PlainText).TrimEnd());

    if (status.ExitCode != 0)
        throw new InvalidOperationException($"interactive child exited with {status.ExitCode}");
}

/// <summary>
/// A child that prints "ready", reads two lines, and answers each. The Unix child also prints its terminal size, which
/// shows the resize.
/// </summary>
static PtyStartInfo CreateInteractiveStartInfo()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            throw new InvalidOperationException("Windows PowerShell is required for the Interactive sample.");

        return new PtyStartInfo
        {
            FileName = powershell,
            Arguments =
            [
                "-NoLogo",
                "-NoProfile",
                "-Command",
                "[Console]::Out.WriteLine('ready'); $a=[Console]::In.ReadLine(); [Console]::Out.WriteLine('first:' + $a); $b=[Console]::In.ReadLine(); [Console]::Out.WriteLine('second:' + $b)",
            ],
            Size = new PtySize(80, 24),
        };
    }

    return new PtyStartInfo
    {
        FileName = "/bin/sh",
        Arguments =
        [
            "-c",
            "printf 'ready\\n'; IFS= read -r a; printf 'first:%s\\n' \"$a\"; IFS= read -r b; set -- $(stty size); printf 'second:%s size:%s %s\\n' \"$b\" \"$1\" \"$2\"",
        ],
        Size = new PtySize(80, 24),
    };
}

/// <summary>
/// The session's output as text, and a way to wait until a marker appears in it. One task reads; any number of
/// waiters are woken each time new output arrives, so nothing polls.
/// </summary>
/// <remarks>
/// The child's own echo of what we type also lands here, so a marker such as "first:alpha" is chosen to be text only
/// the child's answer contains. Searching the whole text on every chunk is fine for a short conversation.
/// </remarks>
sealed class OutputLog
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();
    // A chunk can end in the middle of a UTF-8 character. The decoder keeps those bytes until the next chunk.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _ended;

    public string Text
    {
        get
        {
            lock (_gate)
                return _text.ToString();
        }
    }

    /// <summary>Reads the session's output until it ends, appending it as text.</summary>
    public async Task ReadAllAsync(PtySession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in session.ReadOutputAsync(cancellationToken))
                Append(chunk.Data.Span); // chunk.Data is valid only until the next iteration: decode it now.
        }
        finally
        {
            Signal(ended: true);
        }
    }

    /// <summary>Completes once <paramref name="marker"/> has appeared in the output.</summary>
    public async Task WaitForAsync(string marker, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_text.ToString().Contains(marker, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"marker {marker}");
                    return;
                }

                if (_ended)
                    throw new InvalidOperationException($"Output ended before '{marker}' appeared. Output: {Escape(_text.ToString())}");

                changed = _changed.Task;
            }

            try
            {
                await changed.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for '{marker}'. Output so far: {Escape(Text)}");
            }
        }
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
        try
        {
            var count = _decoder.GetChars(bytes, chars, flush: false);
            lock (_gate)
                _text.Append(chars, 0, count);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }

        Signal(ended: false);
    }

    /// <summary>Wakes every waiter, and gives later waiters a fresh signal unless output has ended.</summary>
    private void Signal(bool ended)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _ended |= ended;
            changed = _changed;
            if (!_ended)
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(ch switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                < ' ' or > '~' => $"\\u{(int)ch:X4}",
                _ => ch.ToString(),
            });
        }

        return builder.ToString();
    }
}
