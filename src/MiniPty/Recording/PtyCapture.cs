namespace MiniPty.Recording;

/// <summary>Result of <see cref="Pty.CaptureAsync"/> — exit code plus merged stdout text.</summary>
public sealed class PtyCapture
{
    public required int ExitCode { get; init; }
    public required string Text { get; init; }
}
