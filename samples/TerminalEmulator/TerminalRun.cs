namespace TerminalEmulator;

/// <summary>
/// Coordinates a GUI host's exit wait and output pump on the UI thread. Closing is recorded before session
/// disposal so its expected exceptions can be distinguished from failures during normal operation.
/// </summary>
internal sealed class TerminalRun
{
    public bool IsClosing { get; private set; }

    public void BeginClose() => IsClosing = true;

    /// <summary>Observes both tasks and returns whether the host should display the completed process.</summary>
    public async Task<bool> WaitAsync(Task exit, Task output)
    {
        var completion = Task.WhenAll(exit, output);
        try
        {
            await completion;
        }
        catch (ObjectDisposedException) when (IsClosing && OnlyDisposalFailures(completion.Exception))
        {
            return false;
        }
        return !IsClosing;
    }

    private static bool OnlyDisposalFailures(AggregateException? error)
    {
        if (error is null)
            return false;
        foreach (var inner in error.InnerExceptions)
        {
            if (inner is not ObjectDisposedException)
                return false;
        }
        return true;
    }
}
