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
/// the session's backend drives itself; neither holds a thread per session. Every member takes the pump it is about,
/// so a driver that outlives one pump ignores calls aimed at a pump it no longer serves.
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
    /// so the driver can release what it held for the pump.
    /// </summary>
    void OnPumpFinished(PtyOutputPump pump);

    /// <summary>
    /// Reads what the transport holds into the ring on the consumer's thread, without waiting, and publishes it. Called
    /// by a consumer that found the ring empty. Returns <see langword="true"/> when it published output. Otherwise the
    /// consumer is about to wait, and the driver has made sure it reads again itself, woken by the transport.
    /// </summary>
    /// <remarks>
    /// A flood then costs the consumer's thread its reads and nothing else: no other thread is woken for output that
    /// the consumer takes as fast as it arrives. On a machine with 2 cores, each such wake was a sleep and a wake of
    /// another thread, and floods read by the driver alone fell behind libraries that read on the consumer's thread.
    /// A driver that reads only on its own thread keeps this default.
    /// </remarks>
    bool TryReadInline(PtyOutputPump pump) => false;

    /// <summary>
    /// The consumer stops reading inline for longer than a read (its enumeration ended): the driver reads for the
    /// pump again, so output keeps arriving in the ring for the next consumer. Never blocks. A driver that reads only on
    /// its own thread keeps this default.
    /// </summary>
    void HandReadingBack(PtyOutputPump pump)
    {
    }
}

