namespace MiniPty;

/// <summary>
/// How the terminal handles typed input at the moment: whether it echoes it, and whether it hands it to the child a
/// line at a time. The child sets both itself, with <c>stty</c> or <c>tcsetattr</c>, so they tell a host what kind of
/// input the child is waiting for.
/// </summary>
/// <param name="Echo">
/// Whether the terminal echoes typed characters (termios <c>ECHO</c>). A program that reads a password turns it off.
/// </param>
/// <param name="Canonical">
/// Whether the terminal collects typed characters into a line, applying erase and kill, and hands the line to the child
/// when Enter is pressed (termios <c>ICANON</c>). A full-screen program such as vim or less, and a shell that edits its
/// own command line such as bash or zsh, turn it off to read each key.
/// </param>
/// <remarks>
/// <para>
/// A program that reads a password (<c>sudo</c>, <c>passwd</c>, <c>read -s</c>, <c>ssh</c> asking for one) turns echo
/// off and keeps canonical mode on, so <c>session.InputMode is { Echo: false, Canonical: true }</c> tells a host that
/// what is typed next will not be shown. The host can then hand the terminal to a person or keep the line out of a
/// record. It is a heuristic: any program can read in that mode.
/// </para>
/// <para>
/// It describes this terminal only. A program that runs a pseudo-terminal of its own, such as tmux, or ssh connected to
/// a remote shell, keeps this one in raw mode and passes keys through, so a password read on its far side is not seen.
/// </para>
/// </remarks>
public readonly record struct PtyInputMode(bool Echo, bool Canonical);
