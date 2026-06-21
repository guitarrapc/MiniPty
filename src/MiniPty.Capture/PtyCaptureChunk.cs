namespace MiniPty.Capture;

/// <summary>A timestamped slice of PTY output.</summary>
/// <param name="Time">Elapsed time since the capture session started.</param>
/// <param name="Data">Decoded text captured in this slice.</param>
public readonly record struct PtyCaptureChunk(TimeSpan Time, string Data);
