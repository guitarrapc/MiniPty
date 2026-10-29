using System.Diagnostics;

namespace MiniPty.Internal;

/// <summary>
/// Bounds how long an output consumer on a UI thread can keep receiving buffered output without handing the
/// thread back to its dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReadOutputAsync</c> and <c>Output.ReadAsync</c> complete synchronously while output is buffered, which keeps
/// bulk reads allocation-free. A consumer that processes each chunk more slowly than the child writes them always
/// finds the next one buffered, so on a UI thread its <c>await</c> loop never returns to the dispatcher: input,
/// rendering, and every other posted callback wait until the child stops writing. Measured on Windows with a
/// flooding child, a consumer spending 2 ms per chunk left a posted callback waiting for over a second.
/// </para>
/// <para>
/// Each thread therefore times its run of synchronous completions. Once a run has lasted <see cref="BudgetMs"/>, the
/// next read that has output completes asynchronously instead, through the consumer's captured
/// <see cref="SynchronizationContext"/> or <see cref="TaskScheduler"/>, so the dispatcher runs what was queued
/// meanwhile before the loop continues. A run starts when a consumer starts reading and whenever one resumes from an
/// asynchronous completion.
/// </para>
/// <para>
/// The run belongs to the thread, not to a session: what starves a dispatcher is its thread going without a return
/// to it, and while one consumer completes synchronously no other consumer on that thread can run. Keeping it
/// thread-static also costs a session nothing.
/// </para>
/// <para>
/// Only a consumer running under such a context yields. On a thread-pool thread, or a dedicated thread without a
/// context, there is no dispatcher to starve, and yielding would only move the loop to another thread. A consumer that
/// returns to its dispatcher on its own between reads (it awaits something else) is not seen doing so, and gets one
/// yield per read once a budget has passed since its last resume; that costs what a read that had to wait costs.
/// </para>
/// </remarks>
internal static class PtyConsumerYield
{
    /// <summary>
    /// Half of a 60 Hz frame: under an output flood, a frame's input and rendering still get the UI thread, while a
    /// consumer that keeps up with its output (the usual case) completes in far less and never yields.
    /// </summary>
    internal const int BudgetMs = 8;

    private static readonly long s_budgetTicks = Stopwatch.Frequency * BudgetMs / 1000;

    [ThreadStatic]
    private static long t_runStart;

    /// <summary>Starts a run of synchronous completions on the current thread.</summary>
    internal static void StartRun() => t_runStart = Stopwatch.GetTimestamp();

    /// <summary>
    /// Reports whether a read that is about to complete synchronously should complete asynchronously instead, because
    /// the current thread's run has spent the budget and the thread has a scheduling context. A spent budget restarts
    /// the run either way.
    /// </summary>
    internal static bool ShouldYield()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - t_runStart < s_budgetTicks)
            return false;

        t_runStart = now;
        return HasSchedulingContext();
    }

    /// <summary>
    /// The test an awaiter applies when it captures a context to resume on. Also whether a consumer may wait on its own
    /// thread for output (<c>UnixPtyBackend</c>): a thread with such a context, a UI thread, must never block.
    /// </summary>
    internal static bool HasSchedulingContext()
    {
        var context = SynchronizationContext.Current;
        if (context is not null && context.GetType() != typeof(SynchronizationContext))
            return true;

        return TaskScheduler.Current != TaskScheduler.Default;
    }
}
