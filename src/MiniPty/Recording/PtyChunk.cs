namespace MiniPty.Recording;

/// <summary>A timestamped slice of PTY output captured during recording.</summary>
public readonly record struct PtyChunk(double TimeSeconds, string Data);
