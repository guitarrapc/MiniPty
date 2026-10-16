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

    internal PtySize ClampedSize => Internal.PtySizeLimits.Clamp(Size);
}
