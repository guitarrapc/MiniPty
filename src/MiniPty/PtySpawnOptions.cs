namespace MiniPty;

/// <summary>Options for spawning a child in a pseudo-terminal.</summary>
public sealed record PtySpawnOptions
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public int Columns { get; init; } = 80;
    public int Rows { get; init; } = 24;

    internal PtySize Size => new(
        Math.Clamp(Columns, 1, 512),
        Math.Clamp(Rows, 1, 512));
}
