namespace MiniPty.Capture;

/// <summary>
/// A single timestamped slice of raw PTY output bytes produced by one read from the master output stream.
/// </summary>
/// <param name="Time">
/// Elapsed time since the capture session started (immediately after <see cref="Pty.Start"/>).
/// Consumers combine chunk times with an external session origin to build timelines.
/// </param>
/// <param name="Data">Raw bytes read in this slice.</param>
public readonly record struct PtyCaptureChunk(TimeSpan Time, ReadOnlyMemory<byte> Data);
