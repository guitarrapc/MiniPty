namespace MiniPty.Tests;

/// <summary>
/// <c>NotInParallel</c> key for tests that attach to this process's console, read or inject its input, or change its
/// modes. The console is process-wide, and two attaches open at once share its input queue and modes. On Windows,
/// disposing them in nested order hangs, because the earlier attach's blocked read takes the other's wake record.
/// Disposing them in start order leaves the console in raw mode, because the later attach restores the modes it saw.
/// </summary>
internal static class HostConsoleGroup
{
    internal const string Key = "host-console";
}
