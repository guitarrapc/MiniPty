namespace MiniPty.Capture;

/// <summary>
/// Options for <see cref="PtyCapture.RunAsync"/>.
/// </summary>
public sealed record PtyCaptureOptions
{
    /// <summary>
    /// Gets or sets completion options shared with <see cref="PtySession.CompleteAsync"/>.
    /// </summary>
    /// <value>
    /// Default is a new <see cref="PtyCompleteOptions"/> instance.
    /// Controls stdin, output encoding, drain timeouts, exit timeout, and cancellation behavior.
    /// </value>
    public PtyCompleteOptions Completion { get; init; } = new();
}
