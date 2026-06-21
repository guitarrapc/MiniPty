using System.Text;

namespace MiniPty.Recording;

/// <summary>Options for <see cref="Pty.RecordAsync"/> and related convenience methods.</summary>
public sealed record PtyRecordOptions
{
    /// <summary>Byte-to-text decoding for captured output (default UTF-8).</summary>
    public Encoding OutputEncoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// Stdin payload. <c>null</c> leaves stdin open (TUI). <c>""</c> signals EOF with no bytes.
    /// Non-empty text is written as UTF-8 before EOF.
    /// </summary>
    public string? Input { get; init; }

    /// <summary>After child exit, how long to wait for natural output EOF before closing transport.</summary>
    public TimeSpan OutputDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>After transport close, how long to wait for the read pump to finish.</summary>
    public TimeSpan OutputCloseGrace { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>When true, cancellation kills the child (used by Record/Capture/Run).</summary>
    public bool KillOnCancellation { get; init; } = true;
}
