using System.Collections;
using System.Runtime.CompilerServices;
using Porta.Pty;

namespace MiniPty.Benchmarks;

/// <summary>
/// The serve child on Porta.Pty, the comparison library: written through <c>WriterStream</c> and read through
/// <c>ReaderStream</c>, as its README shows.
/// </summary>
/// <remarks>
/// <para>
/// The child, the terminal size, and the marker scan are the ones MiniPty runs with. Output is read into one 16 KiB
/// buffer, the size of MiniPty's output ring, so both libraries can hand over at most 16 KiB per read. Porta.Pty's
/// streams are unbuffered, so a write needs no flush.
/// </para>
/// <para>
/// The child gets this process's whole environment: Porta.Pty passes exactly the dictionary it is given, and on
/// Windows an empty one would start the child with no environment at all, while MiniPty inherits it by default.
/// </para>
/// <para>
/// On Windows <c>PORTAPTY_CONPTY</c> selects the ConPTY. Porta.Pty reads it once per process, which is enough here
/// because BenchmarkDotNet runs every case in its own process, and the setup fails unless
/// <see cref="PtyProvider.PseudoConsoleImplementation"/> confirms the choice: Porta.Pty falls back to the in-box ConPTY
/// without an error when the out-of-band files are missing, which would make both arms measure the same thing.
/// </para>
/// </remarks>
internal sealed class PortaPtyServeSession : BenchmarkServeSession
{
    private readonly IPtyConnection connection;
    private readonly Stream reader;
    private readonly Stream writer;
    private readonly byte[] buffer = new byte[16 * 1024];

    private PortaPtyServeSession(IPtyConnection connection)
    {
        this.connection = connection;
        reader = connection.ReaderStream;
        writer = connection.WriterStream;
    }

    internal static PortaPtyServeSession Start(int floodBytes, string conPty, bool asyncIo)
    {
        if (OperatingSystem.IsWindows())
        {
            var outOfBand = conPty == BenchmarkPtyCommands.ConPtyOutOfBand;
            Environment.SetEnvironmentVariable("PORTAPTY_CONPTY", outOfBand ? "oob" : "inbox");
            var expected = outOfBand ? "oob" : "inbox";
            if (PtyProvider.PseudoConsoleImplementation != expected)
            {
                throw new InvalidOperationException(
                    $"Porta.Pty runs on the \"{PtyProvider.PseudoConsoleImplementation}\" ConPTY, not the \"{expected}\" one " +
                    "this case measures (\"oob-no-host\" means OpenConsole.exe is not beside conpty.dll in the output root).");
            }
        }

        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;

        var options = new PtyOptions
        {
            App = BenchmarkPtyCommands.BenchmarkChildPath,
            CommandLine = BenchmarkPtyCommands.ServeArguments(floodBytes),
            Cwd = Environment.CurrentDirectory,
            Cols = BenchmarkPtyCommands.Columns,
            Rows = BenchmarkPtyCommands.Rows,
            Environment = environment,
            UseAsyncIo = asyncIo,
        };

        return new PortaPtyServeSession(PtyProvider.SpawnAsync(options, CancellationToken.None).GetAwaiter().GetResult());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One pooled async method, like <see cref="MiniPtyServeSession.RoundTripAsync"/>, so the harness adds the same
    /// state as it does for MiniPty and the allocation column shows the libraries' own costs.
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal override async ValueTask<long> RoundTripAsync(ReadOnlyMemory<byte> input, byte[] marker)
    {
        long total = 0;
        var matched = 0;
        Arm(marker);
        try
        {
            if (!input.IsEmpty)
                await writer.WriteAsync(input).ConfigureAwait(false);

            while (true)
            {
                var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
                if (Accept(buffer.AsSpan(0, read), marker, ref matched))
                    return total;
            }
        }
        catch (Exception) when (Stalled)
        {
            throw StallException(marker, total);
        }

        throw new EndOfStreamException("The serve child's output ended before the expected marker.");
    }

    protected override void WriteNudge() => _ = writer.WriteAsync(Nudge).AsTask();

    protected override void Abort() => DisposeSession();

    protected override void DisposeSession()
    {
        try
        {
            connection.Kill();
        }
        catch (Exception)
        {
            // The child has already exited.
        }

        connection.Dispose();
    }
}
