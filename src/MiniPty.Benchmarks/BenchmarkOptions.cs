using MiniPty;
using MiniPty.Capture;

namespace MiniPty.Benchmarks;

/// <summary>
/// Shared completion options for integration benchmarks (avoid per-iteration option allocations).
/// </summary>
internal static class BenchmarkOptions
{
    internal static readonly PtyCompleteOptions BytesOnly = new() { DecodeOutput = false };

    internal static readonly PtyCompleteOptions TextDecoded = new() { DecodeOutput = true };

    internal static readonly PtyCaptureOptions CaptureBytesOnly = new() { Completion = BytesOnly };

    internal static readonly PtyCaptureOptions CaptureTextDecoded = new() { Completion = TextDecoded };
}
