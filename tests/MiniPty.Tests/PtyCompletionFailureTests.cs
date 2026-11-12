using System.Text;
using MiniPty.Capture;
using MiniPty.Internal;

namespace MiniPty.Tests;

/// <summary>
/// One-shot completion whose output can no longer be collected: past <see cref="PtyCompleteOptions.MaxOutputBytes"/>, or
/// when its sink fails. The completion waits for the child's exit before it drains, so the sink kills the child and the
/// completion then fails, instead of waiting for good on a child blocked on a full terminal: a 2.2 GiB flood once stopped
/// being collected at 2 GiB, where a buffer's length wrapped, and the completion never returned.
/// </summary>
public sealed class PtyCompletionFailureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task AChildThatWritesPastTheLimitIsKilledAndTheCompletionFails()
    {
        if (!Pty.IsSupported)
            return;

        await using var session = Pty.Start(EndlessChild());
        var completion = session.CompleteAsync(new PtyCompleteOptions { MaxOutputBytes = 16 * 1024, DecodeOutput = true });

        var error = await Assert.That(async () => await completion.WaitAsync(Timeout)).Throws<IOException>();
        await Assert.That(error!.Message).Contains("16384");
        await Assert.That(await PtyBaselineTests.WaitUntilAsync(() => session.HasExited, Timeout)).IsTrue();
    }

    [Test]
    public async Task ACaptureThatWritesPastTheLimitFails()
    {
        if (!Pty.IsSupported)
            return;

        var capture = PtyCapture.RunAsync(EndlessChild(), new PtyCaptureOptions { Completion = new PtyCompleteOptions { MaxOutputBytes = 16 * 1024 } });

        await Assert.That(async () => await capture.WaitAsync(Timeout)).Throws<IOException>();
    }

    /// <summary>
    /// With a child that only a kill ends, the limit is the only way out of the completion's exit wait: the sink must kill
    /// the child, and the completion fail once the exit is observed.
    /// </summary>
    [Test]
    public async Task PastTheLimitTheSinkKillsTheChildSoTheExitWaitEnds()
    {
        var backend = new KillableEndlessBackend();
        await using var session = new PtySession(backend);

        var completion = session.CompleteAsync(new PtyCompleteOptions { MaxOutputBytes = 10_000 });

        await Assert.That(async () => await completion.WaitAsync(Timeout)).Throws<IOException>();
        await Assert.That(backend.Kills).IsEqualTo(1);
    }

    /// <summary>A sink whose write throws fails the completion with that exception, instead of leaving it waiting.</summary>
    [Test]
    public async Task ASinkThatThrowsKillsTheChildAndFailsTheCompletionWithItsException()
    {
        var backend = new KillableEndlessBackend();
        await using var session = new PtySession(backend);
        var sink = new ThrowingSink();

        var completion = PtyCompletion.RunAsync(session, new PtyCompleteOptions(), sink, CancellationToken.None);

        await Assert.That(async () => await completion.WaitAsync(Timeout)).Throws<InvalidDataException>();
        await Assert.That(backend.Kills).IsEqualTo(1);
        await Assert.That(sink.Writes).IsEqualTo(1);
    }

    /// <summary>
    /// Output up to the limit is collected; the write that would pass it fails the sink once, which kills the child and
    /// gives the buffers back, and output after that is dropped.
    /// </summary>
    [Test]
    public async Task TheSinkTakesOutputUpToTheLimitAndFailsOnceAtTheFirstByteBeyondIt()
    {
        var backend = new KillableEndlessBackend(produce: false);
        await using var session = new PtySession(backend);
        var sink = new PtyCompletionSink(Encoding.UTF8, decodeOutput: true);
        sink.Configure(session, maxOutputBytes: 10);

        sink.Deliver("0123456789"u8, timestamp: 0);
        await Assert.That(sink.Failure).IsNull();
        await Assert.That(backend.Kills).IsEqualTo(0);

        sink.Deliver("x"u8, timestamp: 0);
        sink.Deliver("y"u8, timestamp: 0);
        sink.EndOutput(error: null);

        await Assert.That(sink.Failure).IsTypeOf<IOException>();
        await Assert.That(backend.Kills).IsEqualTo(1);
    }

    /// <summary>A transport that fails mid-output ends the child too, so the completion reports the failure.</summary>
    [Test]
    public async Task ATransportFailureKillsTheChild()
    {
        var backend = new KillableEndlessBackend(produce: false);
        await using var session = new PtySession(backend);
        var sink = new PtyCompletionSink(Encoding.UTF8, decodeOutput: false);
        sink.Configure(session, maxOutputBytes: null);
        var failure = new IOException("transport failed");

        sink.Deliver("data"u8, timestamp: 0);
        sink.EndOutput(failure);

        await Assert.That(sink.Failure).IsSameReferenceAs(failure);
        await Assert.That(backend.Kills).IsEqualTo(1);
    }

    /// <summary>
    /// Through <see cref="PtySession.CompleteAsync"/>: output exactly at the limit is the whole result, and one byte more
    /// fails the completion, so the limit is inclusive end to end and not only in the sink.
    /// </summary>
    [Test]
    [Arguments(0, false)]
    [Arguments(1, true)]
    public async Task TheCompletionCollectsOutputExactlyAtTheLimitAndFailsOneBytePastIt(int extra, bool fails)
    {
        const int Limit = 100_000;
        var backend = new FixedOutputBackend(Limit + extra);
        await using var session = new PtySession(backend);

        var completion = session.CompleteAsync(new PtyCompleteOptions { MaxOutputBytes = Limit, DecodeOutput = true });

        if (fails)
        {
            await Assert.That(async () => await completion.WaitAsync(Timeout)).Throws<IOException>();
            return;
        }

        var result = await completion.WaitAsync(Timeout);
        await Assert.That(result.Output.Length).IsEqualTo(Limit);
        await Assert.That(result.GetText().Length).IsEqualTo(Limit);
    }

    [Test]
    public async Task TheLimitMustBePositive()
    {
        await Assert.That(() => new PtyCompleteOptions { MaxOutputBytes = 0 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new PtyCompleteOptions { MaxOutputBytes = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(new PtyCompleteOptions { MaxOutputBytes = 1 }.MaxOutputBytes).IsEqualTo(1);
        await Assert.That(new PtyCompleteOptions().MaxOutputBytes).IsNull();
    }

    /// <summary>A child that writes without end and never reads its input.</summary>
    private static PtyStartInfo EndlessChild() =>
        OperatingSystem.IsWindows()
            ? new PtyStartInfo { FileName = "cmd.exe", Arguments = ["/c", "for /L %i in (1,0,2) do @echo flood-line-that-never-ends"] }
            : new PtyStartInfo { FileName = "yes", Arguments = ["flood-line-that-never-ends"] };

    /// <summary>
    /// A child that writes without end until it is killed, which ends its output and reports its exit as the exit watcher
    /// would. <see cref="IPtyBackend.Kill()"/> is implemented again here, since the base backend's kill does nothing.
    /// </summary>
    private sealed class KillableEndlessBackend(bool produce = true) : PtyOutputPumpTests.FakeBackend, IPtyBackend
    {
        private volatile bool _killed;
        private int _kills;

        public int Kills => Volatile.Read(ref _kills);

        void IPtyBackend.Kill()
        {
            Interlocked.Increment(ref _kills);
            _killed = true;
            Exit(137);
        }

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            if (!produce)
            {
                // Output the test delivers itself: wait for the end.
                while (!_killed && !IsDisposed)
                    Thread.Sleep(5);

                return 0;
            }

            if (_killed || IsDisposed)
                return 0;

            buffer.Fill((byte)'x');
            return buffer.Length;
        }
    }

    /// <summary>A child that writes <paramref name="total"/> bytes of ASCII and then exits on its own.</summary>
    private sealed class FixedOutputBackend(int total) : PtyOutputPumpTests.FakeBackend
    {
        private int _written;

        public override int ReadOutput(Span<byte> buffer, ScriptedOutputDriver driver)
        {
            var count = Math.Min(buffer.Length, total - _written);
            if (count == 0)
            {
                if (!HasExited)
                    Exit(0);

                return 0;
            }

            buffer[..count].Fill((byte)'x');
            _written += count;
            return count;
        }
    }

    /// <summary>A sink whose first write throws.</summary>
    private sealed class ThrowingSink : PtyOutputSink
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        internal override void Write(ReadOnlySpan<byte> data, long timestamp)
        {
            Interlocked.Increment(ref _writes);
            throw new InvalidDataException("The sink could not take the output.");
        }
    }
}
