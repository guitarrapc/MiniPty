namespace MiniPty;

/// <summary>
/// Process and terminal options passed to <see cref="Pty.Start"/>.
/// </summary>
public sealed record PtyStartInfo
{
    /// <summary>
    /// Gets or sets the executable file name or full path of the child process.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Gets or sets command-line arguments passed to the child (excluding <see cref="FileName"/>).
    /// </summary>
    /// <value>An empty list when no arguments are required. Default is an empty list.</value>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets or sets a pre-escaped Windows command line appended after <see cref="FileName"/>.
    /// </summary>
    /// <value>
    /// <see langword="null"/> to build the command line from <see cref="Arguments"/>. When set,
    /// <see cref="Arguments"/> must be empty. Unsupported on Unix.
    /// </value>
    /// <remarks>
    /// No quoting or escaping is applied to this value. The caller must follow the parsing rules
    /// of the target executable, matching node-pty's Windows string-arguments overload.
    /// </remarks>
    public string? CommandLine { get; init; }

    /// <summary>
    /// Gets or sets the working directory for the child process.
    /// </summary>
    /// <value>
    /// <see langword="null"/> to inherit the parent's working directory.
    /// </value>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the initial terminal size in character cells (columns × rows).
    /// </summary>
    /// <value>
    /// Default is 80×24. Each dimension is clamped at spawn time to the platform's limit: 1–32767 on Windows
    /// (ConPTY's signed 16-bit <c>COORD</c>), 1–65535 on Unix (<c>winsize</c>). <see cref="PtySession.Size"/>
    /// reports the value applied.
    /// </value>
    public PtySize Size { get; init; } = new(80, 24);

    /// <summary>
    /// Gets or sets environment variable overrides for the child process.
    /// </summary>
    /// <value>
    /// <see langword="null"/> to inherit the parent environment. A non-null dictionary overlays
    /// the parent environment; null values remove variables and empty strings set empty values on
    /// platforms that preserve empty environment variables.
    /// </value>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>
    /// Gets or sets the terminal name used for Unix <c>TERM</c>.
    /// </summary>
    /// <value>
    /// <see langword="null"/> or an empty string to use the default behavior. On Windows this
    /// value is currently ignored; set <see cref="Environment"/> explicitly to pass <c>TERM</c>.
    /// </value>
    public string? TerminalName { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the child starts drawing at the frontend's current cursor
    /// position instead of on a cleared screen.
    /// </summary>
    /// <value>
    /// <see langword="false"/> by default. Applies to Windows only: a Unix PTY passes bytes through without a
    /// screen model, so the child already starts at the frontend's cursor and the value is ignored there.
    /// </value>
    /// <remarks>
    /// <para>
    /// Without it, ConPTY assumes an empty screen and starts its output with a clear and cursor home
    /// (<c>ESC[2J ESC[H</c>), erasing whatever the frontend showed before the child started, such as an editor's
    /// banner written ahead of the shell. With it, ConPTY first asks the frontend for the cursor position
    /// (<c>ESC[6n</c>) and continues from the reported position. Equivalent to node-pty's
    /// <c>conptyInheritCursor</c> (<c>PSEUDOCONSOLE_INHERIT_CURSOR</c>).
    /// </para>
    /// <para>
    /// The frontend must answer by writing a cursor position report (<c>ESC[row;columnR</c>) to the session input;
    /// terminal emulators such as xterm.js do this automatically. ConPTY consumes the report, so it never reaches
    /// the child. Until it arrives, the in-box ConPTY holds the child's console output with no timeout, so a child
    /// that writes stalls. Leave this off for consumers that only read output, such as
    /// <see cref="PtySession.CompleteAsync"/>, unless they write the report themselves.
    /// </para>
    /// </remarks>
    public bool InheritCursor { get; init; }

    internal PtySize ClampedSize => Internal.PtySizeLimits.Clamp(Size);
}
