namespace MiniPty.Internal;

/// <summary>
/// What waits for a session's output transport and hands what it reads to the session's <see cref="PtyOutputPump"/>.
/// The pump owns the ring, its consumers, and every decision about end of output; a driver owns only how reads are
/// waited for. The backend picks the driver when it starts output (<see cref="IPtyBackend.StartOutput"/>).
/// </summary>
/// <remarks>
/// <para>
/// A driver hands output to the pump through the pump's producer members (<see cref="PtyOutputPump.NextReadSpan"/>,
/// <see cref="PtyOutputPump.Publish"/>, <see cref="PtyOutputPump.Finish"/> and the free-space handshake), one call
/// at a time per pump. The pump calls back into the driver below whenever its state changes in a way the driver
/// cannot see from the transport: the consumer freed space, a sink attached, the transport close was requested, the
/// session is being disposed, or the post-exit drain gave up on the transport ending.
/// </para>
/// <para>
/// Windows reads through I/O completions (<see cref="WindowsOutputDriver"/>) and Unix on the exit watcher's loop, which
/// the session's backend drives itself; neither holds a thread per session. Under the rollback switch
/// (<see cref="PtyOutputDrivers"/>) every platform uses <see cref="PtyReaderThread"/>: a dedicated thread that blocks
/// in the transport (<see cref="IPtyBlockingOutput"/>). Every member takes the pump it is about, so a driver
/// serving several sessions over its lifetime ignores calls aimed at a pump it no longer serves.
/// </para>
/// </remarks>
internal interface IPtyOutputDriver
{
    /// <summary>
    /// Makes the driver re-check <paramref name="pump"/>'s state: read again when the ring has room, or observe a
    /// close, disposal, or an attached sink. Cheap, never blocks, and may be called from any thread; a wake that
    /// finds nothing to do is harmless.
    /// </summary>
    void Wake(PtyOutputPump pump);

    /// <summary>
    /// Releases a read that the transport does not end by itself, so the driver re-checks the pump's state: disposal,
    /// and the forced end of output after the close. A cancel that finds no read in progress may be lost; the pump
    /// repeats it while it still needs it.
    /// </summary>
    void CancelRead(PtyOutputPump pump);

    /// <summary>
    /// The pump will not read again. Called by <see cref="PtyOutputPump.Finish"/> before completion becomes observable,
    /// so the driver can release what it held for the pump (a reader thread parks and serves the next session).
    /// </summary>
    void OnPumpFinished(PtyOutputPump pump);
}

/// <summary>
/// A transport read with blocking calls on a dedicated thread: what <see cref="PtyReaderThread"/> drives. Only that
/// thread calls these members, between a successful <see cref="TryBeginOutputRead"/> and its
/// <see cref="EndOutputRead"/>.
/// </summary>
internal interface IPtyBlockingOutput
{
    /// <summary>
    /// Pins the transport for the reader. Returns <see langword="false"/> when it is already closed, which the
    /// reader treats as end of output. Paired with <see cref="EndOutputRead"/>.
    /// </summary>
    bool TryBeginOutputRead();

    void EndOutputRead();

    /// <summary>
    /// Blocks until output arrives. Returns the byte count, 0 at end of output, or -1 when
    /// <paramref name="reader"/> was woken first (Unix) or its read was canceled through
    /// <see cref="PtyReaderThread.CancelRead"/> (Windows), so the reader re-checks the pump's state.
    /// </summary>
    int ReadOutput(Span<byte> buffer, PtyReaderThread reader);

    /// <summary>
    /// Reads output that is available now or arrives within a short micro-window, for appending to the publish the
    /// last read started. Returns 0 when none arrived; <paramref name="endOfOutput"/> reports end of output. Once the
    /// post-exit drain has given up on the OS ending the transport, this is also the reader's only read, so it must
    /// never block beyond its micro-window.
    /// </summary>
    int ReadMoreOutput(Span<byte> buffer, PtyReaderThread reader, out bool endOfOutput);

    /// <summary>
    /// Parks the reader while the ring is full, until <paramref name="reader"/> is woken. On Unix it keeps writing
    /// queued input meanwhile, and may return after doing so; the reader re-checks the pump's state either way.
    /// </summary>
    void WaitWhileOutputBlocked(PtyReaderThread reader);

    /// <summary>
    /// Longest time, in <see cref="System.Diagnostics.Stopwatch"/> ticks, that one publish keeps appending
    /// <see cref="ReadMoreOutput"/> results after its first read; 0 or less appends until the publish is full or a
    /// micro-window passes with no output.
    /// </summary>
    long CoalesceBudgetTicks { get; }
}
