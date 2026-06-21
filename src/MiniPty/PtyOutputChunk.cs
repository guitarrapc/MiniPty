namespace MiniPty;

/// <summary>A timestamped slice of PTY output.</summary>
/// <param name="Time">Elapsed seconds since capture started.</param>
/// <param name="Data">Decoded text captured in this slice.</param>
public readonly record struct PtyOutputChunk(double Time, string Data);
