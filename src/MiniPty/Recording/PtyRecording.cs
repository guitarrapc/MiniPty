namespace MiniPty.Recording;

/// <summary>Result of <see cref="Pty.RecordAsync"/> — exit code plus timestamped output chunks.</summary>
public sealed class PtyRecording
{
    public required int ExitCode { get; init; }
    public required IReadOnlyList<PtyChunk> Chunks { get; init; }

    /// <summary>All chunk text concatenated in capture order.</summary>
    public string Text => string.Concat(Chunks.Select(static c => c.Data));
}
