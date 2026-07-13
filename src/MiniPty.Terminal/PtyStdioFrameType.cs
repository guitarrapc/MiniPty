namespace MiniPty.Terminal;

/// <summary>
/// Identifies a frame in the <see cref="PtyStdioBridge"/> protocol.
/// </summary>
public enum PtyStdioFrameType : byte
{
    /// <summary>Raw PTY output sent from helper to frontend.</summary>
    Output = 1,

    /// <summary>Raw PTY input sent from frontend to helper.</summary>
    Input = 2,

    /// <summary>UTF-8 JSON control message sent in either direction.</summary>
    Control = 3,
}
