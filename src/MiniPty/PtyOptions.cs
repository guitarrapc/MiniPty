using System.Text;

namespace MiniPty;

/// <summary>Spawn, I/O, and capture options for <see cref="Pty.Start"/> and <see cref="Pty.Run"/>.</summary>
public sealed record PtyOptions
{
    /// <summary>Executable file name or path.</summary>
    public required string FileName { get; init; }

    /// <summary>Command-line arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory for the child process.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Terminal width in character cells.</summary>
    public int Columns { get; init; } = 80;

    /// <summary>Terminal height in character cells.</summary>
    public int Rows { get; init; } = 24;

    /// <summary>Byte-to-text decoding for captured output (default UTF-8).</summary>
    public Encoding OutputEncoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// Stdin for <see cref="Pty.Run"/> only. <see langword="null"/> leaves stdin open (TUI).
    /// <see cref="string.Empty"/> signals EOF with no bytes.
    /// </summary>
    public string? Input { get; init; }

    /// <summary>Maximum time to wait for output drain after the child exits.</summary>
    public TimeSpan OutputDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Grace period after closing the output transport before timing out.</summary>
    public TimeSpan OutputCloseGrace { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether cancellation kills the child process.</summary>
    public bool KillOnCancellation { get; init; } = true;

    internal PtySize Size => new(
        Math.Clamp(Columns, 1, 512),
        Math.Clamp(Rows, 1, 512));
}
