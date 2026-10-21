namespace MiniPty;

/// <summary>
/// Process and terminal options passed to <see cref="Pty.Start"/>.
/// </summary>
public sealed record PtyStartInfo
{
    /// <summary>
    /// Gets or sets the executable file name or full path of the child process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Windows, a batch file (<c>.bat</c> or <c>.cmd</c>, judged by the name Windows opens, after <c>.</c> and
    /// <c>..</c> segments and trailing spaces and dots) runs through the system directory's <c>cmd.exe</c> with AutoRun
    /// commands skipped, delayed expansion off, and command extensions on (<c>/d /v:OFF /e:ON</c>).
    /// <see cref="Arguments"/> are escaped for <c>cmd.exe</c> instead of quoted with the MSVCRT rules, so
    /// <c>cmd.exe</c> reads each one as data. A script can still run what an argument holds, for example with
    /// <c>cmd /c %1</c> or <c>echo %~1</c>. The script is the file <c>CreateProcess</c> would find for this name,
    /// looked up from this process's directories and <c>PATH</c> as for a program, and <c>cmd.exe</c> receives its
    /// full path, so it does not search the child's <see cref="WorkingDirectory"/> for it. A file that starts with
    /// the <c>MZ</c> header runs as a program, from that same file. Without a raw <see cref="CommandLine"/>, a batch
    /// file name, and the full path found for it, cannot contain a quote, a carriage return, a line feed, or a NUL.
    /// With one, <c>CreateProcess</c> starts <c>cmd.exe</c> with its own defaults instead.
    /// </para>
    /// <para>
    /// A name without a root and without a leading <c>.\</c> or <c>..\</c> is searched for. On Unix the child
    /// resolves it against its own <c>PATH</c>. On Windows <c>CreateProcess</c> searches its fixed locations and this
    /// process's <c>PATH</c>, never the child's, so when <see cref="Environment"/> sets or removes <c>PATH</c>, MiniPty
    /// runs the same search with the child's <c>PATH</c> instead and starts the file it finds, for a program and for a
    /// batch file alike; <c>.exe</c> is appended when the last path component has no <c>.</c>, as <c>CreateProcess</c>
    /// does, and a missing program then fails <see cref="Pty.Start"/> with
    /// <see cref="System.ComponentModel.Win32Exception"/> before anything is created.
    /// </para>
    /// </remarks>
    public required string FileName { get; init; }

    /// <summary>
    /// Gets or sets command-line arguments passed to the child (excluding <see cref="FileName"/>).
    /// </summary>
    /// <value>An empty list when no arguments are required. Default is an empty list.</value>
    /// <remarks>
    /// Each element reaches a program as one argument. On Windows it is quoted by the MSVCRT rules, which the UCRT,
    /// <c>msvcrt.dll</c>, and <c>CommandLineToArgvW</c> parsers all read back the same. For a batch file it is
    /// escaped for <c>cmd.exe</c> instead and becomes one parameter of the script (<c>%1</c>, <c>%2</c>, and so on).
    /// A program the script forwards <c>%*</c> to reads each element back unchanged when it parses its command line
    /// with the UCRT. The pre-2008 rules, which <c>CommandLineToArgvW</c> and <c>msvcrt.dll</c> follow, read a quote
    /// inside an element as one that also ends the quoting. After such an element the boundaries move: later elements
    /// can merge into it, and its spaces can start new arguments.
    /// An argument for a batch file cannot contain a carriage return, a line feed, or a NUL, which <c>cmd.exe</c>
    /// cannot receive. The batch file's whole <c>cmd.exe</c> command line cannot exceed the 8191 characters
    /// <c>cmd.exe</c> runs. The count includes the script's full path and the escaping: the quotes around an argument,
    /// a doubled quote and the backslashes doubled before it, and 8 characters for each <c>%</c>.
    /// <see cref="Pty.Start"/> throws <see cref="ArgumentException"/> for either. When the child is <c>cmd.exe</c>
    /// itself, what follows <c>/c</c> is a command for it to parse, so never pass user input there.
    /// </remarks>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets or sets a pre-escaped Windows command line appended after <see cref="FileName"/>.
    /// </summary>
    /// <value>
    /// <see langword="null"/> to build the command line from <see cref="Arguments"/>. When set,
    /// <see cref="Arguments"/> must be empty. Unsupported on Unix.
    /// </value>
    /// <remarks>
    /// No quoting or escaping is applied to this value. The caller must follow the parsing rules of the target
    /// executable. For a batch file those are <c>cmd.exe</c>'s rules, and <c>CreateProcess</c> starts <c>cmd.exe</c>
    /// with its defaults, AutoRun commands included. Use <see cref="Arguments"/> to have MiniPty escape for it.
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
    /// <remarks>
    /// On Windows, setting or removing <c>PATH</c> here also decides where a <see cref="FileName"/> without a root is
    /// looked up: on the child's <c>PATH</c>, as on Unix. See <see cref="FileName"/>.
    /// </remarks>
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

    /// <summary>
    /// Gets or sets a value indicating whether ending the session ends every process the child started, not only the
    /// child.
    /// </summary>
    /// <value>
    /// <see langword="false"/> by default. Applies to Windows only; Unix ignores it, and there disposal hangs the child
    /// up, which ends the descendants in the terminal's foreground process group.
    /// </value>
    /// <remarks>
    /// <para>
    /// Without it, ending the session ends the child and the processes attached to its pseudo-console, while a
    /// descendant in a console of its own, or a GUI program, keeps running, and so does every descendant when the host
    /// process crashes. With it, the child runs in a job object that is closed when the session is disposed and when
    /// the host process ends in any way, including a crash, and closing it ends every process in it.
    /// <see cref="PtySession.Kill()"/> then ends them too, also after the child itself has exited.
    /// </para>
    /// <para>
    /// The option is off by default because it also ends programs the user started from the terminal on purpose, such
    /// as an editor window. No process can leave the job: a program that asks to (<c>CREATE_BREAKAWAY_FROM_JOB</c>)
    /// fails to start inside such a session. A host that itself runs inside a job can still use it, because jobs nest.
    /// </para>
    /// </remarks>
    public bool KillProcessTree { get; init; }

    /// <summary>
    /// Gets or sets the out-of-band ConPTY implementation the session runs on, supplied by the application.
    /// </summary>
    /// <value>
    /// <see langword="null"/> by default: the session uses the ConPTY that Windows ships. Applies to Windows only; a
    /// <see cref="ConPtyLibrary"/> cannot be loaded elsewhere.
    /// </value>
    /// <remarks>
    /// <para>
    /// The in-box ConPTY renders the child's output through the <c>conhost.exe</c> of the user's Windows build, which
    /// decides which VT sequences reach the frontend; the build measured for this option withheld mouse tracking modes,
    /// several terminal queries, and graphics protocols. The out-of-band implementation, <c>conpty.dll</c> plus
    /// <c>OpenConsole.exe</c> from the <c>Microsoft.Windows.Console.ConPTY</c> package, behaves the same on every
    /// Windows build and supports <see cref="PtySession.Clear"/>. MiniPty ships neither file; see
    /// <see cref="ConPtyLibrary"/> for how an application supplies them.
    /// </para>
    /// <para>
    /// On this implementation the host process exits as soon as its last client has, so output ends when the child
    /// exits without waiting for the post-exit quiet interval the in-box path needs. Its host also asks the frontend
    /// what terminal it is (<c>ESC[c</c>) when the session starts; a terminal emulator answers that on its own, and a
    /// consumer that only reads output can ignore it with package versions from 1.25, which no longer wait for the
    /// answer.
    /// </para>
    /// </remarks>
    public ConPtyLibrary? ConPty { get; init; }

    internal PtySize ClampedSize => Internal.PtySizeLimits.Clamp(Size);
}
