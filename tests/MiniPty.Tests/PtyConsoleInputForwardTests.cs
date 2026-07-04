using MiniPty.Console;
using MiniPty.Console.Internal;

namespace MiniPty.Tests;

public sealed class PtyConsoleInputForwardTests
{
    [Test]
    public async Task Forward_ToPtyInput_DoesNotThrow()
    {
        await using var session = Pty.Start(Exit0StartInfo());
        var payload = "abc"u8.ToArray();

        PtyConsoleInputForward.Forward(
            session.Input,
            payload,
            observer: null,
            TimeProvider.System,
            TimeProvider.System.GetTimestamp());

        session.SendEof();
        await session.WaitForExitAsync();
    }

    [Test]
    public async Task Forward_WithObserver_InvokesBeforeWrite()
    {
        using var stream = new MemoryStream();
        var observer = new OrderRecordingObserver(stream);
        var timeProvider = TimeProvider.System;
        var origin = timeProvider.GetTimestamp();
        var payload = "xy"u8;

        PtyConsoleInputForward.Forward(stream, payload, observer, timeProvider, origin);

        var written = stream.ToArray();
        var observed = observer.LastData.ToArray();
        byte[] expected = [(byte)'x', (byte)'y'];
        await Assert.That(observer.InvocationCount).IsEqualTo(1);
        await Assert.That(observer.ObservedBeforeWrite).IsTrue();
        await Assert.That(observer.LastElapsed).IsGreaterThanOrEqualTo(TimeSpan.Zero);
        await Assert.That(observed.SequenceEqual(expected)).IsTrue();
        await Assert.That(written.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task Forward_WhenObserverThrows_StillWrites()
    {
        using var stream = new MemoryStream();
        var observer = new ThrowingObserver();

        PtyConsoleInputForward.Forward(
            stream,
            "q"u8,
            observer,
            TimeProvider.System,
            TimeProvider.System.GetTimestamp());

        await Assert.That(stream.ToArray().SequenceEqual([(byte)'q'])).IsTrue();
    }

    [Test]
    public async Task Forward_WithNullObserver_DoesNotInvoke()
    {
        using var stream = new MemoryStream();
        var observer = new OrderRecordingObserver(stream);

        PtyConsoleInputForward.Forward(
            stream,
            "z"u8,
            observer: null,
            TimeProvider.System,
            TimeProvider.System.GetTimestamp());

        await Assert.That(observer.InvocationCount).IsEqualTo(0);
        await Assert.That(stream.ToArray().SequenceEqual([(byte)'z'])).IsTrue();
    }

    private static PtyStartInfo Exit0StartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            return new PtyStartInfo { FileName = cmd, Arguments = ["/c", "exit 0"] };
        }

        return new PtyStartInfo { FileName = "/bin/sh", Arguments = ["-c", "exit 0"] };
    }

    private sealed class OrderRecordingObserver(Stream target) : IPtyConsoleInputObserver
    {
        internal int InvocationCount { get; private set; }

        internal bool ObservedBeforeWrite { get; private set; }

        internal TimeSpan LastElapsed { get; private set; }

        internal ReadOnlyMemory<byte> LastData { get; private set; }

        public void OnForwardedInput(TimeSpan elapsed, ReadOnlySpan<byte> data)
        {
            InvocationCount++;
            ObservedBeforeWrite = target.Length == 0;
            LastElapsed = elapsed;
            LastData = data.ToArray();
        }
    }

    private sealed class ThrowingObserver : IPtyConsoleInputObserver
    {
        public void OnForwardedInput(TimeSpan elapsed, ReadOnlySpan<byte> data) =>
            throw new InvalidOperationException("observer fault");
    }
}
