using MiniPty;

namespace MiniPty.Capture;

/// <summary>Options for <see cref="PtyCapture.RunAsync"/>.</summary>
public sealed record PtyCaptureOptions
{
    /// <summary>Core completion options (stdin, drain, exit timeout).</summary>
    public PtyCompleteOptions Completion { get; init; } = new();
}
