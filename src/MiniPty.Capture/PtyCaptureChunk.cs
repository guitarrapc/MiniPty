namespace MiniPty.Capture;

/// <summary>
/// A single timestamped slice of PTY output produced by one read from the master output stream.
/// </summary>
/// <param name="Time">
/// Elapsed time since the capture session started (immediately after <see cref="Pty.Start"/>).
/// Consumers combine chunk times with an external session origin to build timelines.
/// </param>
/// <param name="Data">Text decoded from the bytes read in this slice.</param>
public readonly record struct PtyCaptureChunk(TimeSpan Time, string Data);
