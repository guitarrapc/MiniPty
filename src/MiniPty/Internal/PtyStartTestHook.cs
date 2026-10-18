namespace MiniPty.Internal;

/// <summary>Where <see cref="PtyStartTestHook.Fault"/> runs during <see cref="Pty.Start"/>.</summary>
internal enum PtyStartStage
{
    /// <summary>The platform spawn succeeded, and no backend owns the child yet, so no exit watcher knows it.</summary>
    Spawned,

    /// <summary>The backend owns the child, and the session around it is not built yet.</summary>
    BackendBuilt,
}

/// <summary>
/// Test seam for the window in which a start can fail with its child already running. The steps there fail only on
/// resource exhaustion (a descriptor flag, the exit watcher's thread or handle duplicate, the output reader's thread),
/// which a test cannot cause on purpose, so a test makes <see cref="Fault"/> throw at a stage instead and checks that
/// the failed start ends the child and leaks nothing.
/// </summary>
/// <remarks>
/// Null outside tests; a start reads it once per stage. It is process-wide, so a test that sets it runs alone.
/// </remarks>
internal static class PtyStartTestHook
{
    /// <summary>Called with the stage and the child's process id; throwing fails the start at that point.</summary>
    internal static Action<PtyStartStage, int>? Fault;

    internal static void Run(PtyStartStage stage, int processId) => Volatile.Read(ref Fault)?.Invoke(stage, processId);
}
