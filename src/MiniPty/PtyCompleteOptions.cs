using System.Text;

namespace MiniPty;

/// <summary>Options for <see cref="PtySession.CompleteAsync"/>.</summary>
public sealed record PtyCompleteOptions
{
    /// <summary>Byte-to-text decoding for captured output (default UTF-8).</summary>
    public Encoding OutputEncoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// Stdin text to write before waiting for exit.
    /// <see langword="null"/> leaves stdin open (TUI). <see cref="string.Empty"/> signals EOF with no bytes.
    /// </summary>
    public string? Input { get; init; }

    /// <summary>Whether to call <see cref="PtySession.SendEof"/> after writing <see cref="Input"/>.</summary>
    public bool SendEofAfterInput { get; init; } = true;

    /// <summary>Maximum time to wait for the child process to exit after input is completed. Null means no timeout except cancellation.</summary>
    public TimeSpan? ExitTimeout { get; init; }

    /// <summary>Grace period to drain output after the child exits, before closing the PTY transport.</summary>
    public TimeSpan OutputDrainGrace { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum time to wait for the output reader to finish after the PTY transport is closed.</summary>
    public TimeSpan OutputReaderCloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>When <see langword="true"/>, cancellation during <see cref="PtySession.CompleteAsync"/> kills the child process.</summary>
    public bool KillOnCancellation { get; init; } = true;
}
