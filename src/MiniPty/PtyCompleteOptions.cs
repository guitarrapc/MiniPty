using System.Text;

namespace MiniPty;

/// <summary>
/// Options that control stdin handling, output draining, and cancellation behavior for
/// <see cref="PtySession.CompleteAsync"/> and <c>MiniPty.Capture.PtyCapture.RunAsync</c>.
/// </summary>
public sealed record PtyCompleteOptions
{
    /// <summary>
    /// Gets or sets the encoding used to decode PTY output bytes into text.
    /// </summary>
    /// <value>Default is <see cref="Encoding.UTF8"/>.</value>
    public Encoding OutputEncoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// Gets or sets stdin text to write before waiting for the child to exit.
    /// </summary>
    /// <value>
    /// <list type="bullet">
    /// <item><description><see langword="null"/> — leave stdin open (interactive TUI programs).</description></item>
    /// <item><description><see cref="string.Empty"/> — signal end-of-input without writing bytes.</description></item>
    /// <item><description>Non-empty text — written before EOF when <see cref="SendEofAfterInput"/> is <see langword="true"/>.</description></item>
    /// </list>
    /// </value>
    public string? Input { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="PtySession.CompleteAsync"/> decodes
    /// <see cref="PtyResult.Output"/> from the captured byte stream.
    /// </summary>
    /// <value>
    /// Default is <see langword="true"/>. When <see langword="false"/>, only
    /// <see cref="PtyResult.Output"/> is populated; <see cref="PtyResult.GetText"/> decodes on demand.
    /// </value>
    public bool DecodeOutput { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="PtySession.SendEof"/> is called after
    /// <see cref="Input"/> is written.
    /// </summary>
    /// <value>Default is <see langword="true"/>. Ignored when <see cref="Input"/> is <see langword="null"/>.</value>
    public bool SendEofAfterInput { get; init; } = true;

    /// <summary>
    /// Gets or sets the maximum time to wait for the child process to exit after input handling completes.
    /// </summary>
    /// <value>
    /// <see langword="null"/> (default) — wait until the child exits or the operation is canceled.
    /// A finite value causes <see cref="TimeoutException"/> when exceeded.
    /// </value>
    public TimeSpan? ExitTimeout { get; init; }

    /// <summary>
    /// Gets or sets how long to drain PTY output after the child exits, before closing the transport.
    /// </summary>
    /// <value>Default is 1 second.</value>
    public TimeSpan OutputDrainGrace { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum time to wait for the output reader to finish after the PTY transport is closed.
    /// </summary>
    /// <value>Default is 5 seconds. Exceeding this value throws <see cref="TimeoutException"/>.</value>
    public TimeSpan OutputReaderCloseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets a value indicating whether cancellation during <see cref="PtySession.CompleteAsync"/>
    /// terminates the child process.
    /// </summary>
    /// <value>
    /// Default is <see langword="true"/>. Does not apply to <see cref="PtySession.WaitForExitAsync"/>,
    /// where cancellation only stops waiting.
    /// </value>
    public bool KillOnCancellation { get; init; } = true;
}
