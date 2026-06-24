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
    /// Gets or sets the working directory for the child process.
    /// </summary>
    /// <value>
    /// <see langword="null"/> to inherit the parent's working directory.
    /// </value>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the initial terminal size in character cells (columns × rows).
    /// </summary>
    /// <value>Default is 80×24. Values are clamped to 1–512 per dimension at spawn time.</value>
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

    internal PtySize ClampedSize => new(
        Math.Clamp(Size.Columns, 1, 512),
        Math.Clamp(Size.Rows, 1, 512));
}
