namespace MiniPty;

/// <summary>Process and terminal options for <see cref="Pty.Start"/>.</summary>
public sealed record PtyStartInfo
{
    /// <summary>Executable file name or path.</summary>
    public required string FileName { get; init; }

    /// <summary>Command-line arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory for the child process.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Initial terminal size in character cells.</summary>
    public PtySize Size { get; init; } = new(80, 24);

    internal PtySize ClampedSize => new(
        Math.Clamp(Size.Columns, 1, 512),
        Math.Clamp(Size.Rows, 1, 512));
}
