using BenchmarkDotNet.Attributes;
using MiniPty.Console;
using MiniPty.Console.Internal;

namespace MiniPty.Benchmarks;

[MemoryDiagnoser]
public class PtyConsoleInputForwardBenchmarks
{
    private byte[] _buffer = null!;
    private MemoryStream _stream = null!;
    private TimeProvider _timeProvider = null!;
    private long _attachTimestamp;
    private NoOpObserver _noOpObserver = null!;
    private const int Read = 8;
    private const int Iterations = 10_000;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[64];
        "abc\r\n"u8.CopyTo(_buffer);
        _stream = new MemoryStream(64 * 1024);
        _timeProvider = TimeProvider.System;
        _attachTimestamp = _timeProvider.GetTimestamp();
        _noOpObserver = NoOpObserver.Instance;
    }

    [Benchmark(Baseline = true)]
    public void WriteOnly()
    {
        for (var i = 0; i < Iterations; i++)
        {
            _stream.Position = 0;
            _stream.Write(_buffer, 0, Read);
        }
    }

    [Benchmark]
    public void Forward_NullObserver()
    {
        var data = _buffer.AsSpan(0, Read);
        for (var i = 0; i < Iterations; i++)
        {
            _stream.Position = 0;
            PtyConsoleInputForward.Forward(_stream, data, null, _timeProvider, _attachTimestamp);
        }
    }

    [Benchmark]
    public void Forward_NoOpObserver()
    {
        var data = _buffer.AsSpan(0, Read);
        var observer = _noOpObserver;
        for (var i = 0; i < Iterations; i++)
        {
            _stream.Position = 0;
            PtyConsoleInputForward.Forward(_stream, data, observer, _timeProvider, _attachTimestamp);
        }
    }

    private sealed class NoOpObserver : IPtyConsoleInputObserver
    {
        internal static readonly NoOpObserver Instance = new();

        public void OnForwardedInput(TimeSpan elapsed, ReadOnlySpan<byte> data)
        {
        }
    }
}
