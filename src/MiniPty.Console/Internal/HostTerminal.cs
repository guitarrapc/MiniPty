namespace MiniPty.Console.Internal;

/// <summary>
/// Platform host terminal: TTY detection, mode save/restore, size queries, and raw input reads.
/// </summary>
internal interface IHostTerminal : IDisposable
{
    bool TryGetSize(out int columns, out int rows);

    /// <summary>Blocks until at least one input byte is available, then writes into <paramref name="buffer"/>.</summary>
    /// <returns>Bytes written to <paramref name="buffer"/>, or zero when interrupted.</returns>
    int ReadInput(Span<byte> buffer);

    /// <summary>Non-blocking check for a host resize. Returns true when size changed since the last call.</summary>
    bool TryPollResize(out int columns, out int rows);
}

internal static class HostTerminal
{
    public static bool IsInteractiveHost()
    {
        if (OperatingSystem.IsWindows())
            return WindowsHostTerminal.IsInteractiveHost();

        return UnixHostTerminal.IsInteractiveHost();
    }

    public static IHostTerminal Create() =>
        OperatingSystem.IsWindows()
            ? new WindowsHostTerminal()
            : new UnixHostTerminal();
}
