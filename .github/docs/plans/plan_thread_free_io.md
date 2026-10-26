# Thread-Free I/O Plan (Web Hosting Scale)

Goal: let a web host (ASP.NET Core, `HttpListener`, or any WebSocket server) keep hundreds to thousands of PTY sessions open while paying only for the ones producing output. No session may hold a thread, an idle session should hold no output buffer, and reading must stay allocation-free. Latency, throughput, backpressure, and every existing lifecycle guarantee must be at least as good as today. This replaces the deferred "Thread-free async I/O" item (G6 of the retired GUI host readiness plan) with a concrete design.

**Status:** Phases 0 and 1 are implemented (2026-10-23) and measured on Windows, Linux, and macOS. Phases 2 and 3 are implemented and measured on Windows and Linux (2026-10-23), and the full suite passed on macOS the same day. Phase 4 is done (2026-10-23): macOS and FreeBSD use `kqueue`, probed on macOS and decided from source for FreeBSD. Phase 5 is implemented and measured on Windows (2026-10-24): Windows sessions read through I/O completions and hold no thread; the full suite with it passed on macOS on 2026-10-25. Phase 6 is done on Linux and on macOS (2026-10-25): one loop reads every Unix session, on epoll and on kqueue; FreeBSD runs the kqueue code from source, unverified. On macOS a flood through the WebSocket bridge took about four times the frames of Phase 0 and was about 12% slower; the core stays as it is, and Phase 7 has the bridges coalesce bursts instead ([decision](#phase-6-result-macos)). Phase 7 is implemented and measured on macOS (2026-10-25): a flood there takes half Phase 0's frames and runs faster than with reader threads ([Phase 7 result](#phase-7-result-macos)). It is measured on Windows and Linux too (2026-10-26), where .NET timers did not keep the 1 ms the deadlines need: Windows now ticks on a high-resolution waitable timer and Unix on the I/O loop's wait, and a flood takes 3.9–4.0 frames per 32 KiB on both, at Phase 6's throughput and echo ([Windows and Linux](#phase-7-result-windows-and-linux)), and a recheck on macOS with the loop's tick measured flood, echo, and tails as before. Phase 8 is decided on Windows and Linux and checked on macOS (2026-10-26): the bridges join a stream of short lines too, since a burst now also starts at four chunks within 1 ms and lasts while each deadline still finds output; 100-byte lines at 0.4–2 MB/s went from about 330 frames per 32 KiB to 9–52 on Windows, Linux, and macOS, for at most about 2 ms on output right after such a run ([Phase 8 result](#phase-8-result)). Phase 9 retires the reader threads. See [Implementation Phases](#implementation-phases).

## Background

Use case 4 ([terminal.md](../specs/terminal.md)) already serves browsers. `PtyWebSocketBridge` and `PtyWebSocketSessionManager` bridge a session to xterm.js with ACK flow control and reconnect. A web host differs from a desktop terminal in one way that matters here: it keeps many sessions open at once, and most of them are idle tabs.

Per session, MiniPty spends the following today:

| Resource | Today |
|---|---|
| Output reading | One dedicated reader thread per session that is still reading ([PtyReaderThread.cs](../../../src/MiniPty/Internal/PtyReaderThread.cs)). It blocks in `ReadFile` on Windows and in `poll`/`select` plus `read` on Unix. Threads park between sessions and exit after 30 s idle |
| Exit observation | Shared. Unix uses one watcher thread per process; Windows uses one thread per 63 sessions |
| Input | Windows: I/O completions on the overlapped input pipe, holding no thread. Unix: written by the session's reader thread |
| Post-exit drain | One process-wide 10 ms timer, running only while a session drains |
| Coalescing | Windows: `PIPE_NOWAIT` retries with `Thread.Sleep(0)` for a 500 µs window. Unix: a 1 ms wait on the master per continuation read. Both have a 2 ms budget per slot |
| Output buffer | 4 slots × 4 KiB from `ArrayPool<byte>.Shared`, held from spawn to end of output |

So 1,000 open tabs cost about 1,000 threads and 16 MB of buffers whether or not they print anything. Everything except output reading (and Unix input, which rides on it) already holds no per-session thread.

"Web" here means the server side. Browser WebAssembly cannot start child processes, so a browser can only hold the client side of the bridge protocol, never a PTY.

## Findings

### Reference libraries

Read from source in `.references/` (git-ignored): node-pty 1.1.0 (`d182c60`), WezTerm (`e2a98e1`), Porta.Pty 2.2.3 (`0186b60`), creack/pty (`9246436`).

| Library | Read model | Threads per session | Lesson for MiniPty |
|---|---|---|---|
| node-pty, Unix | Non-blocking master on libuv's epoll/kqueue | 1, a `waitpid` thread per child | Reads need no thread. Exit can join the same poller (MiniPty's watcher already does this) |
| node-pty, Windows | ConPTY output relayed by a `worker_threads` Worker into a second named pipe, then read on the main loop | 2 (Worker + exit waiter), 3 pipes, output decoded twice | The Worker exists because `ClosePseudoConsole` waits until output is drained, and the main thread is the one that calls it. The rule to keep: **output keeps draining while the pseudoconsole closes**. Its flow-control test is commented out ("Flow control doesn't work on Windows"), and it has no high-water mark |
| WezTerm (portable-pty, mux) | Blocking `read` on a reader thread; the source says non-blocking reads "are not portable" | 3 (reader, parser, exit waiter) + 1 MiB buffer | Shows the cost to avoid. It coalesces with a 3 ms window plus a size cap. Its remote mux sends screen diffs, so a slow client can skip intermediate states; a raw byte stream to xterm.js cannot |
| Porta.Pty, default | `FileStream(isAsync: false)`: `ReadAsync` blocks a thread-pool worker per session | 1 pool worker held | On a 4-vCPU Windows runner, its own test delivered output from 0 of 24 concurrent short-lived sessions read this way, because the pool starved. Blocking pool workers is a correctness hazard, not only a cost |
| Porta.Pty, `UseAsyncIo` | Windows: overlapped named pipe + `FileStream(isAsync: true)`. Unix: one `poll(2)` thread for the process | 0 | Same Windows approach as this plan, already used with ConPTY. Its Unix poller allocates several objects per wait and rebuilds its descriptor array almost every cycle |
| creack/pty (Go) | `*os.File` on Go's netpoller | 0 on Linux, since a March 2026 fix; blocking on macOS by design | Thread-free reads over a pty work on Linux. macOS avoids kqueue for ptys (golang/go#22099), which is the open risk for MiniPty's macOS path |

The common ground:

- On Windows, the output pipe must be a named pipe opened overlapped on MiniPty's end; an anonymous pipe cannot be.
- On Unix, a non-blocking master on a shared readiness loop works.
- Push events with unbounded queues (node-pty, the WezTerm mux) do not give a web host real backpressure; pull with a bounded buffer does.

### Probes (2026-10-23)

| ID | Probe | Observed |
|---|---|---|
| F1 | Linux (WSL2, kernel 6.6, .NET 10.0.9, both JIT and NativeAOT): a non-blocking pty master wrapped as `new Socket(new SafeSocketHandle(fd, ownsHandle: false))` | Works. 300 idle pending `ReceiveAsync` calls added no thread. A read allocates 0 B on both the synchronous and the pending path. A canceled read loses no data. A 256 KiB `SendAsync` stays pending while the slave is unread and completes as it is drained, also holding no thread. `Socket.Dispose` fails a pending read with `OperationAborted` and leaves the descriptor open |
| F2 | Same, slave closed (Linux reports `EIO` on the master) | The `ValueTask` API throws `SocketException` with the generic `SocketError.SocketError`. `SocketAsyncEventArgs` reports the same value without throwing |
| F3 | Same, zero-byte `ReceiveAsync` | Completes at once with 0, which cannot be told apart from end of output. A pty cannot be waited on this way: `read(fd, buf, 0)` returns immediately |
| F4 | Windows 11 26200: zero-byte overlapped `ReadFile` on a byte-mode named pipe whose server end is overlapped, bound with `ThreadPoolBoundHandle` | Pending (`ERROR_IO_PENDING`) until data arrives, then completes with 0 bytes and leaves the data in the pipe. A closed writer completes it with `ERROR_BROKEN_PIPE`. 300 idle pending reads added no thread. A pipe can be waited on without committing a buffer |

Not probed here: a real ConPTY on an overlapped output pipe inside MiniPty (the input pipe and Porta.Pty already run ConPTY on overlapped named pipes), and any effect on latency or chunk counts. macOS and FreeBSD readiness on a pty master is the [Phase 4 result](#phase-4-result).

## Constraints

These come from existing specifications and lessons, and the new engine must keep them.

- **Close keeps draining.** Closing a pseudoconsole can wait for its final frame to fit in the pipe, so a read stays posted (discarding after disposal) until the transport ends ([pty_crossplatform.md](../references/pty_crossplatform.md#disposal)).
- **End of output is not exit.** The post-exit drain closes the transport after 100 ms of quiet, a full ring after the close is not end of output, and parked time is not silence ([core_session.md](../specs/core_session.md#lessons-learned)).
- **No consumer code on MiniPty's threads.** Consumer waits complete asynchronously and swallow a throwing `SynchronizationContext.Post`.
- **Pooled arrays are rented and returned on one thread**, or they strand in a per-thread cache.
- **A session's fields are paid by every session.** State a session may never use lives behind a lazily created object.
- **Each `AssemblyLoadContext` owns its own loop.** A loop kept in the shared native library once handed one load's exits to the other.
- **NativeAOT, no third-party dependencies, `LibraryImport` only.**

## Public API Decisions

The public API does not change shape. The web-facing layer (`PtyTerminal`, `PtyWebSocketBridge`, `PtyWebSocketSessionManager`, `PtyStdioBridge`) already has what a web host needs: pull-based zero-copy chunks, backpressure from the socket to the child, cancellation tied to the request, and offset-based reconnect. What it lacks is cost proportional to active sessions, and that is an engine property.

| Member | Decision | Why |
|---|---|---|
| `ReadOutputAsync` | Shape unchanged. A chunk becomes **all contiguous output available when the consumer asks**, up to the buffer's capacity, instead of one transport read plus its micro-window | Coalescing moves to the consumer side (see Coalescing). A WebSocket consumer still busy with the previous send finds everything that arrived meanwhile, so a flood goes out in large frames and a keystroke echo goes out at once. The chunk lifetime contract is unchanged |
| `Output` (Stream) | Unchanged; `ReadAsync` now waits on a completion or readiness notification instead of a reader thread | Becomes as asynchronous as `NetworkStream`, so `CopyToAsync` into a WebSocket stream holds no thread |
| `PtyOutputChunk` | Unchanged | — |
| `PtyTerminal`, bridges | Shape unchanged. From Phase 7 the bridges join output that arrives in a burst into larger chunks, through an internal `PtyTerminal` option | Larger chunks under load reduce frame count. Where a read returns little, as on macOS, joining needs a wait, which the bridges make because they pay per frame |
| Engine selection | Internal only, no public option | A per-platform engine is not a user decision. An `AppContext` switch back to reader threads may ship for one release as a field escape hatch, documented as temporary |
| Metrics | Not in this plan | `System.Diagnostics.Metrics` counters (active sessions, bytes, full-buffer stalls, loop delay) are AOT-safe and useful to web operators, but are a separate decision |

Specification text that changes: the buffer bound and chunk description in [core_session.md](../specs/core_session.md#backpressure) and the `Pause` row of [terminal.md](../specs/terminal.md) ("4 × 4 KiB"); the "Many tabs" row of [GUI hosts](../specs/core_session.md#gui-hosts); the output pump, post-exit drain, disposal, and child exit sections of [pty_crossplatform.md](../references/pty_crossplatform.md); and Capture's per-read timestamps in [capture.md](../specs/capture.md) if read boundaries change.

## Architecture Decisions

```
ConPTY output pipe ── overlapped named pipe, completions on the runtime's IOCP ──┐
Unix PTY master ───── one readiness loop per load (today's exit watcher thread) ──┤
                                                                                  ▼
              per-session byte ring (borrowed while output is unread, returned when drained)
                                                                                  ▼
          ReadOutputAsync / Output / PtyTerminal / bridges (continuations run asynchronously)
```

### Windows

1. **Output pipe.** MiniPty's end of the output pipe becomes the overlapped server end of a named pipe, created exactly like the input pipe ([CreateInputPipe](../../../src/MiniPty/Internal/WindowsPtyBackend.cs): random name, single instance, first instance, remote clients rejected). ConPTY's end stays synchronous. The pipe's buffer size is decided by measurement, because it bounds the bytes in flight beyond the ring and therefore how quickly Ctrl+C takes effect during a flood.
2. **Completions on the runtime's completion port**, through `ThreadPoolBoundHandle` and a `PreAllocatedOverlapped` created once per session, with completion-port notification skipped for reads that finish at once. This is the mechanism the input pipe already uses, and the one Kestrel's own I/O depends on.
3. **Wait without a buffer.** An idle session posts a zero-byte read (F4). When it completes, MiniPty reads what the pipe holds into the ring with reads that finish at once, publishes it, and posts the next read. While the ring still holds unread output (a burst the consumer has not caught up with), the next read targets the ring directly and saves the extra round trip. Once the ring is empty and released, the next read is zero-byte again.
4. **Stopping.** Forced end of output after the close, and disposal, cancel the posted read with `CancelIoEx`. The reader thread handle, `CancelSynchronousIo`, and the `PIPE_NOWAIT` window go away.
5. **Close stays off completion callbacks.** `ClosePseudoConsole` and process termination never run on a completion; a posted read survives them, so the drain continues while they run.
6. **Exit observation unchanged.** The `WaitForMultipleObjects` watchers (one thread per 63 sessions) stay. Making them O(1) is a follow-up, not part of this plan.

### Unix

1. **One loop per load.** The exit watcher thread (pidfd + `epoll` on Linux, `kqueue` on macOS and FreeBSD) becomes the I/O loop: every session's master is registered on it as well. Unix ends up with one thread in total instead of a watcher plus one reader per session. The watcher already registers a disposed session's master to discard its output, so the registration and wake machinery exist.
2. **Readiness, then non-blocking reads on the loop thread.** Reads are short, never block, and need no thread-pool hop, so reading progresses even while the host's pool is starved; only delivery to the consumer waits for the pool. Each ready master gets a bounded amount of reading per loop iteration, so one flooding session cannot starve the others.
3. **Interest follows state.** Read interest is dropped while the ring is full and restored when the consumer frees space. Write interest is added only while input is queued, so the loop also writes queued input (today the reader thread does). At end of output (`EIO`/hang-up) the master is deregistered before anything else, so a hung-up descriptor cannot report readiness forever.
4. **Deadlines in the loop's timeout.** The coalescing window (kept on Unix at first, see below) joins the hang-up kill deadline that the watcher already keeps there. The window is a fixed interval, so its deadlines form a FIFO queue rather than a heap. The post-exit drain keeps its process-wide timer, which Windows shares.
5. **macOS and FreeBSD use `kqueue`.** Phase 4 found level-triggered read, write, and end-of-file events reliable on a macOS pty master, and the wait cost flat from 1 to 436 masters, where `select` grows with every master. FreeBSD follows from source. tmux, Go, and creack/pty avoid kqueue for ttys on macOS for reasons that do not apply to a master opened this way ([Phase 4 result](#phase-4-result)).
6. **The per-reader wake descriptors go away**; only the loop's own wake remains. The master stays reference-counted, so a close never races a read, a resize, or a write that is in progress.

### Buffer

1. **A single-producer, single-consumer byte ring** (head and tail only) replaces the four fixed slots. The consumer borrows the contiguous span from head to tail (to the end of the array at the wrap), so successive small reads join into one chunk without any time window.
2. **Rented on demand.** A session takes a ring when output arrives and gives it back when it is empty and the consumer has released its chunk. An idle session holds none. (On Windows a read posted into the ring holds it only during a burst.) This takes effect with the platform engines (Phases 5 and 6), because today's blocking Windows read needs its buffer while it waits.
3. **Not `ArrayPool<byte>.Shared`.** With completions and loop iterations on different threads than releases, a thread-caching pool would strand arrays. Rings come from a process-wide free list of pinned buffers (pinned so a posted overlapped read needs no extra pin), retained up to the peak number of sessions with unread output. Whether and when to trim the free list is decided in Phase 1.

### Coalescing

Today the reader thread holds a slot for up to 2 ms of continuation reads. In the new engine a consumer still busy with its previous chunk naturally takes everything that arrived meanwhile, so batching comes from the consumer's pace instead of a timer.

- **Windows drops the time window.** Sub-millisecond timers are not available on the completion port, and the window costs 10–40% of a core for trickling output today. Reading what the pipe holds plus consumer-side batching replaces it.
- **Unix keeps the 1 ms window and 2 ms budget at first**, as loop deadlines, so its chunking does not change in the same phase as its engine. Phase 6 compares it with consumer-side batching and keeps one policy for both platforms if the numbers allow. **Decided 2026-10-25:** the Linux loop dropped the window as well, so both engines publish each read as it completes ([Phase 6 result](#phase-6-result-linux)), and the kqueue loop does the same on macOS. The reader threads keep their windows until they retire.
- **A consumer that is faster than the child sees more, smaller chunks than today** (ConPTY delivers bulk output in slices 20–45 µs apart). This is measured, with the WebSocket bridge as the reference consumer. If frame counts matter, the remedy is a policy the consumer asks for, not a window the core applies to every consumer. On macOS they do: a read there returns at most 1 KiB, so a flood took four times the frames and ran about 12% slower through the bridge. In Phase 7 the bridges ask for one. In a burst their reads wait until 8 KiB is unread or a deadline passes, and a process-wide tick in the core library serves the deadlines.

### One-shot sinks

`CompleteAsync` and Capture write into a sink on the reader thread today. Two things break once output is delivered elsewhere:

- **On a shared loop**, growing a large result buffer (a copy proportional to the output so far) would stall every session.
- **On Windows**, completions run on pool workers, so the sinks' `ArrayPool<T>.Shared` growth and decode scratch would strand arrays in those workers' caches.

Sinks therefore stop depending on the thread that delivers output. They grow by appending segments without copying, from pools without per-thread caches. This lands in Phase 3, before either engine, with the one-shot allocation benchmarks as the gate.

## Isolation Rules

- **Nothing that can block runs on the loop or on a completion.** Spawning, `ClosePseudoConsole`, consumer continuations, and growing a sink all stay off it.
- **Faults stay with their session.** An unexpected error in one session's processing ends that session's output with the error recorded; the loop keeps running.
- **No lock is shared across sessions while a slow call runs.** node-pty holds a global mutex across `ClosePseudoConsole`.
- **Bounded memory.** Every session is bounded by its ring, the pipe or tty buffer, and (for persistent sessions) its replay buffer. A slow client throttles its own child, never the loop.
- **Thread-pool dependence is stated, not hidden.** On Unix, reading continues during pool starvation until rings fill. On Windows, completions need a pool worker, as Kestrel's sockets do. If that proves to matter, a dedicated completion port with one MiniPty thread is the fallback.

Security isolation of the child (which user it runs as, what environment it inherits, which container it runs in) is unchanged by this plan; see Follow-ups.

## Rejected Alternatives

| Alternative | Why rejected |
|---|---|
| Unix: wrap the master in `System.Net.Sockets.Socket` and use the runtime's socket engine | Works on Linux with no thread and no allocation (F1), and is the smallest change. Rejected as the primary design because a socket over a non-socket descriptor is runtime behavior, not a documented contract. A pty cannot be waited on with a zero-byte read through it (F3), so every idle session would hold a buffer. `EIO` arrives as a generic error (F2). macOS would go through the runtime's kqueue with the same open risk. Kept as a prototype and fallback option |
| Unix: `FileStream` or `RandomAccess` asynchronous reads | Synchronous reads on a pool worker on Unix: the Porta.Pty default failure |
| Read on a pool worker that blocks (`Task.Run` loop) | Pool starvation stops output, as Porta.Pty measured |
| A relay thread into a socket (node-pty's Worker, WezTerm's socketpair) | A thread per session, which is what this plan removes |
| Windows: a dedicated completion port and thread now | Isolates reads from pool starvation, but adds a thread and a second completion mechanism next to the input pipe's. Kept as the fallback in Isolation Rules |
| Windows: high-resolution waitable timers to keep a 500 µs window | Extra handles and wakeups per session to preserve a policy that consumer-side batching replaces |
| Linux `io_uring` | Blocked by the default seccomp profile of common container runtimes, needs recent kernels, and adds a third Unix mechanism |
| Expose `PipeReader` or push events with an unbounded queue | Already rejected for the core API ([core_session.md](../specs/core_session.md#caller-context)); unbounded queues are what make node-pty and the WezTerm mux unsafe for slow clients |
| A public option to choose the engine | Not a decision a user can make better than the library; adds surface for a transition |

## Implementation Phases

Each phase merges on its own with CI passing on Windows, Linux, and macOS. A test that a later phase fixes is added skipped, and its skip reason names that phase. After each change, run the narrowest relevant tests, so that a hang can be attributed to that change. When a phase lands, it appends a "Phase N result" section to this plan with its measurements and lessons.

| Phase | Delivers | Depends on | Platforms | Size |
|---|---|---|---|---|
| 0 | Baseline numbers, census tests, WebSocket benchmark | — | All | Small |
| 1 | Byte ring, buffer free list, contiguous chunks | 0 | All | Medium |
| 2 | Event-driven pump; the reader thread becomes one driver of it | 1 | All | Medium |
| 3 | One-shot sinks independent of the delivering thread | 2 | All | Small–medium |
| 4 | Readiness probe for pty masters | — | macOS, FreeBSD | Small |
| 5 | Windows engine | 3 | Windows | Medium–large |
| 6 | Unix engine | 3, and 4 for macOS and FreeBSD | Linux, then macOS and FreeBSD | Large |
| 7 | Bridges coalesce bursts of output into larger frames | 6 | All | Small–medium |
| 8 | Decision on steady output below the burst rate | 7 | All | Small |
| 9 | Reader thread retired, specifications updated, plan retired | 5, 6, 7, 8 | All | Small |

Phases 5 and 6 can run in parallel on separate branches. They touch different backends and share only the contract from Phase 2. Phase 4 can run at any time before Phase 6 reaches macOS.

Phases 1–3 keep today's reader thread and change one thing each, so each change is measured on its own, as G1 did. From Phase 5 on, when a second driver exists, an `AppContext` switch selects the reader-thread driver. That gives Phases 5 and 6 a rollback in the field, and Phase 9 decides whether it ships.

### Phase 0: Baseline and harness

**Goal:** the numbers every later phase is judged against, and failing tests that define "done".

**Changes**
- `MiniPty.Benchmarks` references `MiniPty.Terminal`, a first-party project and not a new dependency, and gains `PtyWebSocketBenchmarks.Bridge_32KiB_Bytes`:
  - A session bridged by `PtyWebSocketBridge` to a client over loopback TCP, through `WebSocket.CreateFromStream`.
  - The client acknowledges output the way the xterm.js guide describes.
  - It measures time and allocations per bridged session.
- [WebTerminalScale.cs](../../../samples/SingleFiles/WebTerminalScale.cs), an ASP.NET Core host that serves many sessions, takes the census and the latency measurements. BenchmarkDotNet suits neither 1,000 sessions nor percentiles.
  - Its dashboard shows the host's sessions, threads, memory, and handles live, so the effect of each phase can be seen in a browser.
  - Its `bench` mode starts the host as a child process, so the client's sockets are not counted.
  - It then opens idle shell sessions over WebSockets in steps, and records per step:
    - the host's threads, with thread-pool workers counted apart, since a burst of connections injects them;
    - working set and handles;
    - keystroke echo latency p50/p99.
  - Finally it floods one session, and records throughput and WebSocket frames per 32 KiB.
  - CI publishes it with NativeAOT and runs it with `--smoke`.

**Tests (added skipped)**
- `IdleSessionsHoldNoThread`: going from 16 to 64 idle sessions adds no threads beyond the runtime's own. On Windows the exit watcher's thread per 63 sessions is excluded. Skipped until Phase 5 on Windows and Phase 6 on Unix.

**Measure:** record the existing benchmarks (`PtyIntegrationBenchmarks`, `PtyOutputBenchmarks`, `PtyInputBenchmarks`, `PtyConPtyBenchmarks`) and the new ones on Windows, on Linux, and on macOS. Record the CPU a trickling child costs on Windows, which the specification puts at 10–40% of a core today.

**Done when:** the baseline is recorded, and the census test, with its skip removed locally, fails for the expected reason.

### Phase 0 result

Implemented 2026-10-23, as planned. With its skip removed on Windows, `PtyScaleTests.IdleSessionsHoldNoThread` failed: 64 idle sessions had 48 more threads than 16.

**Baseline on Windows.** Windows 11 26200 with 32 logical CPUs, the in-box ConPTY, and `WebTerminalScale bench` on a Release JIT build:

| Sessions | Threads | Pool threads | Other threads per session | Working set (MB) | Handles | Echo p50 / p99 (ms) |
|---:|---:|---:|---:|---:|---:|---:|
| 0 | 52 | 4 | — | 52.9 | 512 | — |
| 16 | 82 | 19 | 0.94 | 59.8 | 772 | 0.67 / 0.92 |
| 64 | 153 | 41 | 1.00 | 77.2 | 1,462 | 0.72 / 14.50 |
| 128 | 218 | 41 | 1.01 | 83.6 | 2,237 | 0.77 / 2.22 |

A flood through the bridge took 8.0 WebSocket frames per 32 KiB (4.0 KiB each) at 17–22 MB/s; ConPTY's rendering sets that rate.

**Baseline on Linux.** WSL2, Ubuntu 24.04, 8 logical CPUs, same build:

| Sessions | Threads | Pool threads | Other threads per session | Working set (MB) | Private (MB) | Descriptors | Echo p50 / p99 (ms) |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 0 | 28 | 5 | — | 73.5 | 175 | 189 | — |
| 16 | 47 | 9 | 0.94 | 78.2 | 333 | 252 | 1.97 / 11.40 |
| 64 | 95 | 9 | 0.98 | 84.2 | 723 | 444 | 1.58 / 2.51 |
| 256 | 287 | 9 | 1.00 | 99.5 | 2,275 | 1,212 | 1.97 / 3.66 |
| 1,000 | 1,050 | 28 | 1.00 | 159.4 | 8,447 | 4,188 | 1.50 / 1.62 |

- Every open session holds exactly one more thread: its output reader. The thread-pool workers come from the burst of connections and retire later.
- On Linux each reader thread reserves an 8 MB stack, so private (virtual) memory grows by 8 MB per session.
- A flood took 8.0 frames per 32 KiB at about 100–135 MB/s.

**BenchmarkDotNet baseline on Windows** (the rows Phase 1 can move):

| Benchmark | Mean | Allocated |
|---|---:|---:|
| Integration `Session_Exit0_WaitForExit` | 32.3 ms | 1.25 KB |
| Integration `Session_32KiB_Bytes` | 75.3 ms | 38.37 KB |
| Integration `Session_32KiB_StreamBytes` | 73.9 ms | 1.55 KB |
| Integration `Session_32KiB_OutputStreamBytes` | 72.6 ms | 1.66 KB |
| Integration `Capture_32KiB_Bytes` | 76.3 ms | 39.78 KB |
| ConPty `Session_32KiB_StreamBytes`, in-box / out-of-band | 74.7 / 76.8 ms | 1.55 / 1.56 KB |
| WebSocket `Bridge_32KiB_Bytes` | 107.2 ms (±32.8) | 26.43 KB |

**Not measured here:**
- macOS, which was measured with Phase 1: its numbers, and an A/B against this baseline, are under the Phase 1 result.
- BenchmarkDotNet on Linux and macOS; there the sample's A/B runs stand in for it.
- The CPU of a trickling child on Windows, which Phase 5 measures against its change.

**Lessons**
- **A path near `MAX_PATH` breaks benchmarks.** A checkout under a long temporary directory put BenchmarkDotNet's copy of the benchmark child at about 260 characters. `CreateProcess` failed with error 206, and every 32 KiB benchmark reported NA. Run benchmarks from a short path.
  - The path BenchmarkDotNet reported was 258 characters, but recreated outside it, that path started. The limit measured exactly 260 there, in both kinds of host.
  - Long program paths need both the machine's `LongPathsEnabled` and a `longPathAware` manifest in the host.
  - Even with both, `CreateProcess`'s own lookup of the command line stops at `MAX_PATH`. MiniPty now passes rooted and dot-relative program paths of 260 characters or more as the application name ([Program lookup](../specs/core_session.md#program-lookup-windows)).
- **The WebSocket harness itself had to be right before its numbers meant anything.** The lessons on watching the bridge, closing the server's connection, and counting pool workers apart are in [terminal.md](../specs/terminal.md#lessons-learned).
- **Repeated 32 MiB floods found a data-loss race that predates this plan:** one baseline run lost 60% of its output. See Phase 1 result.

### Phase 1: Byte ring and buffer free list

**Goal:** a chunk spans every contiguous read, and buffers stop depending on thread-local pool caches. The reader thread is unchanged.

**Changes**
- `PtyOutputPump`'s four slots become a single-producer, single-consumer byte ring of the same 16 KiB.
  - The producer appends within today's coalescing windows and publishes by advancing the tail.
  - `TryPeek` hands out the contiguous published span.
  - `Release` releases exactly what was handed out.
  - `CopyTo`, behind the `Output` stream, keeps partial consumption.
- A process-wide free list of pinned 16 KiB buffers, with no per-thread caching, replaces `ArrayPool<byte>.Shared` for rings. This phase decides whether and when the list is trimmed.
- Rings are still taken at spawn, because a blocking Windows read needs its buffer while it waits. Taking them on demand arrives with the engines.
- Specifications:
  - the chunk description and buffer bound in [core_session.md](../specs/core_session.md#backpressure);
  - the `Pause` row in [terminal.md](../specs/terminal.md);
  - the Buffer row of the output pump table in [pty_crossplatform.md](../references/pty_crossplatform.md).

**Tests**
- `PtyOutputPumpTests` cases:
  - wrap-around;
  - a chunk that spans several reads;
  - partial copies through `Output`;
  - a release racing disposal;
  - a full ring after the close is still not end of output.
- `IdleSessionsHoldNoOutputBuffer`, through an internal count of buffers taken from the free list. Added skipped until Phases 5 and 6.

**Measure:** allocations, and chunks per 32 KiB (`Session_32KiB_StreamBytes`, ConPTY streaming), against Phase 0.

**Done when:** no allocation regression, and chunks per 32 KiB are the same or fewer.

### Phase 1 result

Implemented 2026-10-23 on Windows and Linux, and measured on macOS the same day (see [On macOS](#on-macos)).

**Delivered as planned:**
- **The byte ring.** `PtyOutputPump.PublishSize` (4 KiB) is the most one read and its continuation reads publish at once. `Capacity` (16 KiB) is the ring. The producer waits while less than `PublishSize` is free, so a consumer that frees a few bytes at a time does not cause a read per release.
- **`PtyBufferPool`:** pinned buffers on one lock-guarded stack, with no thread affinity.
- **`IPtyBackend.OutputChunkLimit`:** the platform backends return the ring, and the scripted backends one read, so that tests can still hand reads out one at a time.
- **Tests:** the planned ones, and `IdleSessionsHoldNoOutputBuffer` (skipped).

**Trimming:** the free list keeps every returned buffer, up to the peak number of rings held at once, and allocates nothing after that. Phases 5 and 6 revisit this, once rings are taken per burst rather than per session.

**Behavior change:** the ring holds bytes, so a few small frames no longer fill it (`SmallFramesDoNotFillTheRing`). Tests that filled the ring with four small reads now use full-size reads.

**Unplanned: data loss in the pump, which predates this plan.**
- The Phase 0 harness lost the rest of a 32 MiB flood through the bridge in about one of ten runs. It happened on the baseline as well (13 of 33 MiB arrived), and the bridge then failed with "PTY output reader was signaled without a state change".
- **Cause:** a data signal claimed a wait armed after its output had already been consumed. The consumer had taken the output without waiting, then armed a new wait.
- **Fix:** each wait carries a generation, and a data signal claims a wait only while output is unread, checked after reading the wait. A generation alone did not fix it, because the claimed wait was new, not stale.
- **Test:** `ASignalClaimedForOneWaitNeverCompletesALaterOne` failed within two seconds in 15 of 15 runs before the fix, and passed 15 of 15 after it. The lesson is in [core_session.md](../specs/core_session.md#lessons-learned).

**Unplanned: one test changed.** In `BridgeFlowControlPausesWithoutAcksAndResumesOnAck`, a Linux child's 64 lines (8 KiB) arrived in one chunk, the child exited, and the bridge closed before the test acknowledged anything. It now prints 256 lines, more than the watermark plus one chunk.

**Measured** (A/B runs alternate the two builds):

| | Baseline | Phase 1 |
|---|---:|---:|
| Frames per 32 KiB of flood through the bridge, Windows (3 rounds) | 8.0 | 5.2–5.7 |
| Flood throughput, Windows (MB/s; ConPTY-bound) | 22.0 / 22.1 / 20.9 | 22.2 / 22.3 / 20.8 |
| Frames per 32 KiB, Linux (6 rounds) | 8.0 | 7.7–7.8 |
| Flood throughput, Linux (mean of 6 rounds, MB/s) | 110.9 | 113.9 |
| Echo p50 / p99 at 64 sessions, Linux (ms) | 1.52–1.54 / 1.70–1.90 | 1.52 / 1.72–1.78 |
| Other threads per session | 1.00 | 1.00 (not this phase's goal) |

BenchmarkDotNet on Windows, with the fix:

| Benchmark | Baseline | Phase 1 |
|---|---:|---:|
| Integration `Session_Exit0_WaitForExit` | 32.3 ms, 1.25 KB | 35.6 ms, 1.24 KB |
| Integration `Session_32KiB_Bytes` | 75.3 ms, 38.37 KB | 80.5 ms (±14.0), 38.36 KB |
| Integration `Session_32KiB_StreamBytes` | 73.9 ms, 1.55 KB | 76.3 ms, 1.55 KB |
| Integration `Session_32KiB_OutputStreamBytes` | 72.6 ms, 1.66 KB | 76.6 ms, 1.65 KB |
| Integration `Capture_32KiB_Bytes` | 76.3 ms, 39.78 KB | 78.0 ms, 39.77 KB |
| ConPty `Session_32KiB_StreamBytes`, in-box / out-of-band | 74.7 / 76.8 ms, 1.55 / 1.56 KB | 75.7 / 77.7 ms, 1.55 / 1.55 KB |
| WebSocket `Bridge_32KiB_Bytes` | 107.2 ms (±32.8), 26.43 KB | 77.1 ms, 26.32 KB |

- **Allocations** are equal or lower on every row: the pump lost its four slot lengths.
- **Times** moved within the spread between runs of the same build, which is about ±4 ms for these spawn-bound benchmarks.
  - An alternating A/B of the exit benchmarks measured Phase 1 at 35.0 / 34.2 ms, against 37.0 / 35.3 ms for the baseline.
  - A second round in which both builds were the baseline measured 35.8 / 33.7 and 36.2 / 34.6 ms.
- **Tests:** the full suite passed on Windows, on Linux, and on macOS: 592 tests, 590 passed, the 2 planned skips.
- **NativeAOT:** the sample publishes and passes `--smoke` on Windows and on Linux.

#### On macOS

Apple M2 with 8 logical CPUs, macOS 26.6.2, .NET 10.0.13, `WebTerminalScale bench` on a Release JIT build. The A/B baseline is the commit before Phases 0 and 1 with this sample added; Phase 0 changed no engine code.

Phase 1 leaves threads and memory per session unchanged, so this table (`bench --sessions 16,64,256`) is also the macOS baseline for Phase 6:

| Sessions | Threads | Pool threads | Other threads per session | Working set (MB) | Echo p50 / p99 (ms) |
|---:|---:|---:|---:|---:|---:|
| 0 | 31 | 5 | — | 78.5 | — |
| 16 | 50 | 9 | 0.94 | 81.2 | 1.32 / 1.48 |
| 64 | 116 | 28 | 0.97 | 92.2 | 1.31 / 1.47 |
| 256 | 308 | 28 | 0.99 | 85.0 | 1.38 / 1.56 |

- As on Linux, every open session holds exactly one more thread: its output reader.
- The bench reports 0 for private memory and for handles on macOS, so the reader threads' stack reservations, 8 MB per session on Linux, cannot be read from it.
- Working set at 256 sessions ranged from 85 to 110 MB over three runs (two of Phase 1, one of the baseline), so on macOS it is only a rough figure.

| | Baseline | Phase 1 |
|---|---:|---:|
| Frames for a 32 MiB flood through the bridge (6 alternating rounds) | 8,295 in every round | 8,270–8,291 |
| Frames per 32 KiB | 8.0 | 8.0 |
| Flood throughput (mean of 6 rounds, MB/s) | 64.2 | 64.7 |
| Echo p50 / p99 at 64 sessions (ms) | 1.46 / 2.43 | 1.31–1.49 / 1.47–1.82 |
| Threads at 256 sessions | 308 | 308 |

- **Every flood arrived in full.** The 32 MiB flood ran 16 times on Phase 1: 10 times on its own, and 6 times in the A/B. Each run received all 33,178 KiB (the 32 MiB plus the CR the terminal adds before each newline) and the exit message, and the host printed no "session failed". Throughput was 57–70 MB/s.
- **The baseline lost no output in its 6 rounds.** That is too few to say macOS is free of the race that Phase 1 fixed, which lost output in about one of ten runs on Windows and Linux.
- **Frames do not drop on macOS as they do on Windows.** In the baseline every frame but the last was exactly 4 KiB: the reader always filled a slot. With Phase 1 most chunks are still a single 4 KiB publish, so the bridge has usually sent one before the next is published. Frames are the same, which meets this phase's bar.

### Phase 2: Event-driven pump

**Goal:** separate what the pump does with output from what waits for it, without changing behavior.

**Changes**
- The pump becomes a state machine.
  - It reacts to: output available or a read completed with N bytes, end of output, a transport failure, the consumer freeing space, a close request, and disposal.
  - It asks its driver to arm, disarm, or cancel a read.
- That driver contract replaces `IPtyBackend`'s blocking output members (`ReadOutput`, `ReadMoreOutput`, `WaitWhileOutputBlocked`, each taking a `PtyReaderThread`).
- Today's reader thread becomes the first driver, keeping its coalescing windows and, on Unix, its writing of queued input. `CancelRead` becomes the driver's cancel.
- The post-exit drain scheduler talks only to the pump.
- The scripted backends in `PtyOutputPumpTests`, and the tests that construct backends or reader threads, move to the contract.
  - Those tests are `PtyConcurrentDisposeTests`, `PtyOutputYieldTests`, `PtyPendingInputTests`, and `PtyMacDescriptorTests`.
- The `AppContext` switch for the reader-thread driver: moved to Phase 5, because with one driver there is nothing to select (see the Phase 2 result).

**Tests:** the whole suite, unchanged in what it asserts.

**Done when:** behavior and benchmark numbers match Phase 1 within noise.

### Phase 2 result

Implemented 2026-10-23 and measured on Windows and Linux. The full suite passed on macOS later that day, with the follow-up under [Phase 4 result](#phase-4-result).

**Delivered:**
- **`IPtyOutputDriver`** (`Wake`, `CancelRead`, `OnPumpFinished`) is all the pump asks of a driver: re-check my state, release a blocked read, I am done. `PtyReaderThread` implements it, so a session still costs no driver object.
- **`IPtyBlockingOutput`** takes the blocking members out of `IPtyBackend`: `TryBeginOutputRead`, `EndOutputRead`, `ReadOutput`, `ReadMoreOutput`, `WaitWhileOutputBlocked`, and `CoalesceBudgetTicks`. Both backends implement it, and only the reader thread calls it.
- **`IPtyBackend.StartOutput(pump)`** lets each backend start its own driver. Today both call `PtyReaderThread.Run(pump, this)`, so Phases 5 and 6 change that one line per platform.
- **The pump's producer members**, which any driver calls, one call at a time per pump:
  - `IsFull` and `IsEndOfOutputForced`;
  - `DeliverBacklogToSink`;
  - the free-space handshake, `BeginWaitForFreeSpace` and `EndWaitForFreeSpace`;
  - `NextReadSpan`, `OnOutputRead`, `Publish`, and `Finish(error)`.
- **The read loop moved unchanged** from the pump into the reader thread, with its coalescing windows and its full-ring wait. The pump keeps the ring, its consumers, the sink, the drain decisions, and the ring's lifetime.
- **Tests:** `FakeBackend` implements both interfaces and `StartOutput`, and its subclasses are unchanged. The two coalescing-budget tests read the budget through `IPtyBlockingOutput`. No assertion changed.

**Deviation:** the `AppContext` switch moved to Phase 5. With one driver there is nothing for it to select, and a switch nothing exercises would only rot.

**Measured** (A/B, alternating the builds):

BenchmarkDotNet on Windows, mean of two rounds each (allocation in parentheses):

| Benchmark | Phase 1 | Phase 2 |
|---|---:|---:|
| Integration `Session_Exit0_WaitForExit` | 32.7 / 37.5 ms (1.24 KB) | 31.9 / 33.5 ms (1.24 KB) |
| Integration `Session_32KiB_Bytes` | 78.0 / 74.4 ms (38.36 KB) | 74.0 / 75.6 ms (38.36 KB) |
| Integration `Session_32KiB_StreamBytes` | 73.7 / 77.7 ms (1.55 KB) | 74.9 / 75.8 ms (1.55 KB) |
| Integration `Session_32KiB_OutputStreamBytes` | 74.8 / 80.2 ms (1.65 KB) | 75.8 / 75.3 ms (1.65 KB) |
| Integration `Capture_32KiB_Text` | 77.8 / 74.3 ms (113.95 KB) | 76.4 / 76.2 ms (113.95 KB) |
| ConPty `Session_32KiB_StreamBytes`, in-box | 75.1 / 78.1 ms (1.55 KB) | 80.2 / 73.8 ms (1.55 KB) |
| WebSocket `Bridge_32KiB_Bytes` | 77.2 / 75.5 ms (26.25 / 26.11 KB) | 75.0 / 75.6 ms (26.25 / 26.11 KB) |

- Every allocation matched, except once: in Phase 2's first round, `Session_Exit0_Bytes` and `Capture_Echo_Bytes` measured 70 B more, and the second round measured the Phase 1 values again.
- Times moved within the spread between runs of the same build.

`WebTerminalScale bench` on Linux, 3 rounds each:

| | Phase 1 | Phase 2 |
|---|---:|---:|
| Threads at 64 sessions (other threads per session) | 95 (0.98) | 95 (0.98) |
| Echo p50 / p99 at 64 sessions (ms) | 1.55–1.57 / 1.70–1.90 | 1.55–1.56 / 1.67–1.76 |
| 32 MiB flood: received, frames per 32 KiB | 33,178 KiB every round, 7.8 | 33,178 KiB every round, 7.7–7.8 |
| Flood throughput (mean, MB/s) | 108.6 | 106.1 |

**Tests:** the full suite passed on Windows and on Linux: 604 tests, 602 passed, the 2 planned skips.

### Phase 3: Sinks independent of the delivering thread

**Goal:** one-shot completion and Capture can be fed from a thread-pool worker or from the loop. Neither may strand pooled arrays or stall other sessions.

**Changes**
- `PtyGrowingBuffer`, `PtyCompletionSink`, and `PtyCaptureSink` stop renting and returning `ArrayPool<T>.Shared` arrays on the delivering thread.
- Result growth appends segments instead of copying everything written so far.
- Byte segments and decode scratch come from pools without per-thread caches, or are owned by the sink.

**Tests**
- The completion and Capture suites, including multi-megabyte output and UTF-8 sequences split across segments.
- A stress test that feeds a sink from varying pool workers, and shows that allocations do not grow from run to run.

**Measure:** the one-shot benchmarks (`Session_*_Bytes`, `Capture_*`), which gate this phase.

**Done when:** no allocation regression with the reader-thread driver.

### Phase 3 result

Implemented 2026-10-23 and tested on Windows and Linux. On macOS, `OneShotResultsKeepMultiMegabyteOutput` then failed: its exact comparison did not allow for the macOS terminal writing CR CR LF when its output queue fills between the CR and the LF of a newline ([platform constraints](../specs/platform_support.md#platform-specific-test-constraints)). With that folded, the full suite passed on macOS (see [Phase 4 result](#phase-4-result)).

**Delivered:**
- **Segments instead of growing arrays.** `PtyCompletionSink` and `PtyCaptureSink` collect bytes and decoded text in `PtySegmentedBuffer<T>`, 16 KiB segments appended as output arrives. A write costs what it carries, and nothing written is copied until the result is built. `PtyGrowingBuffer` is gone.
- **A pool without thread affinity.** Segments come from `PtySegmentPool<T>`, one lock-guarded stack per element type that keeps at most 64 segments (1 MiB), so one large capture cannot hold its memory for the rest of the process. Output beyond what the pool holds allocates its extra segments, and the collector reclaims them.
- **No bookkeeping objects.** The buffer is a struct of a segment table and a length, held inline in the sink. The tables (16 entries, 256 KiB of output) are pooled with the segments, and a buffer with more segments moves to a larger table that the pool does not keep.
- **No decode scratch.** Text is decoded straight into the segments with `Decoder.Convert`. Only when a segment has less room left than one byte can decode to (two characters for UTF-8, a surrogate pair) do the next characters cross the boundary through a 64-character stack buffer.
- **Results built on the caller's thread, once.** `OnOutputEnded` only flushes the decoder. `ToPayload` and `ToResult` merge the segments into one array each and return the segments, and a second call throws. `Abort` returns the segments of a failed completion, whose result nobody takes, also after output ended; before, an ended sink kept its buffers for a result that never came.
- The on-demand text chunks of a Capture without `DecodeOutput` decode the same way.

**Found and fixed: a Phase 1 regression.** Since Phase 1 a sink receives the backlog of several reads as one write of up to 16 KiB, but the decode scratch was still sized for one 4 KiB read. `CompleteAsync` and Capture with `DecodeOutput` threw when the sink attached after several reads had piled up, and on-demand text chunks threw for a chunk larger than one read. Every sink test had fed one read at a time.

**Tests** (new):
- `SinksDecodeAWriteLargerThanOneRead` and `CaptureTextChunksOnDemandDecodeAChunkLargerThanOneRead` for the regression, and `CompleteAsyncDecodesABacklogOfSeveralReads` for it end to end through the pump.
- `SinksKeepMultiMegabyteOutputWithCharactersAcrossSegments`: 3 MiB in writes of 1 byte to 16 KiB, with three-byte sequences across byte segments and a surrogate pair at the last element of a character segment.
- `OneShotResultsKeepMultiMegabyteOutput`: 4 MiB from a child through `CompleteAsync` and Capture. On Unix every line holds a three-byte character.
- `SinksFedAndFinishedOnOtherThreadsAllocateNothingBeyondTheResult`: the writes, the finish, and the result each on a thread that then ends, over 20 runs. Before: 6.4 KB per run beyond the result for bytes, and 81 KB with decoding. After: 1.57 KB and 1.67 KB, the three threads and the sink's own objects. The test allows 4 KiB.
- `AbortIsIdempotentAndNeverTouchesATakenResult` replaces the test that kept a finished result through `Abort`.
- The full suite passed on Windows and on Linux: 614 tests, 612 passed, the 2 planned skips.
- **NativeAOT:** the `Capture` and `Session` samples, which run Capture and `CompleteAsync`, publish without warnings and run on Windows. Linux was not published this phase; nothing in it is platform-specific.

**Measured** (A/B, alternating the builds): BenchmarkDotNet on Windows, `PtyIntegrationBenchmarks`, two rounds each (allocation in parentheses). The first segmented version allocated 30–210 B more than Phase 2 on every one-shot benchmark: its buffer object was twice the old growing array, Capture had kept its bytes in an inline struct, and text past 32 K characters needed an overflow array. The numbers below are for the struct buffer with pooled tables.

| Benchmark | Phase 2 | Phase 3 |
|---|---:|---:|
| `Session_Exit0_Bytes` | 37.6 / 35.6 ms (1.83 KB) | 35.0 / 34.6 ms (1.77 KB) |
| `Session_Echo_Bytes` | 37.8 / 38.6 ms (1.88 KB) | 36.5 / 37.5 ms (1.81 KB) |
| `Capture_Echo_Bytes` | 41.9 / 38.0 ms (3.08–3.09 KB) | 38.0 / 39.3 ms (3.02 KB) |
| `Session_32KiB_Bytes` | 77.2 / 80.8 ms (38.36 KB) | 77.3 / 77.0 ms (38.30 KB) |
| `Capture_32KiB_Bytes` | 77.1 / 76.9 ms (39.77 KB) | 79.8 / 81.4 ms (39.71–39.77 KB) |
| `Session_Echo_Text` | 38.3 / 42.5 ms (2.20 KB) | 37.1 / 37.7 ms (2.10 KB) |
| `Capture_Echo_Text` | 38.0 / 39.9 ms (4.55 KB) | 38.5 / 37.8 ms (4.45 KB) |
| `Capture_32KiB_Text` | 78.2 / 80.6 ms (113.95 KB) | 77.4 / 75.9 ms (113.93 KB) |
| `Capture_32KiB_DisplayPlain` | 77.5 / 78.9 ms (179.48 KB) | 79.1 / 77.7 ms (179.46–179.52 KB) |

- Every one-shot benchmark allocates the same or 20–100 B less, except `Capture_32KiB_DisplayPlain`, which moved within ±40 B. The persistent benchmarks (`Session_Exit0_WaitForExit`, `Session_32KiB_StreamBytes`, `Session_32KiB_OutputStreamBytes`) did not change: 1.24, 1.55, and 1.65 KB.
- Times moved within the spread between runs of the same build. Three benchmarks were rerun because a machine stall had slowed some iterations in one round of each build (StdDev 30–70 ms); the table uses the reruns for those three.

**Left for Phase 6:** Unix input that the terminal cannot take at once is still rented from `ArrayPool<byte>.Shared` by the writing thread and returned by the reader thread once written. When the loop writes queued input, those buffers need the same treatment.

### Phase 4: Readiness probe for pty masters

**Goal:** choose the Unix loop's wait mechanism per operating system before writing it.

**Work:** a standalone probe, like the F1–F4 probes, on macOS and, where a machine is available, FreeBSD. It checks:
- `kqueue` level-triggered `EVFILT_READ` and `EVFILT_WRITE` on a pty master;
- end of file after the slave closes, and after the session leader exits (macOS revokes the tty);
- the 1 KiB macOS master buffer under a flood;
- 1,000 masters in one `kqueue`;
- the same with `select`.

**Done when:** the choice per operating system is recorded in "Phase 4 result". Without a FreeBSD machine, FreeBSD follows from source and stays flagged as unverified, as it is today.

### Phase 4 result

Done 2026-10-23. The probe is [pty-readiness-probe.c](../../../scripts/pty-readiness-probe.c): standalone C with no MiniPty code, written for macOS and FreeBSD, which takes about three minutes. On macOS 26.6.2 (Apple M2, 8 logical CPUs) all 71 of its checks passed. No FreeBSD machine was available, so neither its build nor its run there is tested.

| OS | Wait | Basis |
|---|---|---|
| macOS | `kqueue`, level-triggered | Probed. `select` and `poll` also work on a master, but their wait cost grows with every master |
| FreeBSD | `kqueue`, level-triggered | Source only (`ptsdev_kqfilter`, FreeBSD 8.0 and later). Unverified until the probe runs there |

**kqueue on a macOS master** (each behavior below is in the XNU source, `ptmx_kqfilter`, too):
- `EVFILT_READ` and `EVFILT_WRITE` register and are level-triggered: reported on every wait while output is unread or input has room, and no longer once it is not. Output that `^S` stopped (MiniPty sets `IXON`) is not reported readable until `^Q`, so a stopped terminal does not spin the loop.
- `EVFILT_READ`'s `data` is exactly the unread byte count, so the loop can read that much and skip the read that would return `EAGAIN`. `EVFILT_WRITE`'s `data` is always 0: it says there is room, not how much.
- **End of output:** after the slave's last close, `EVFILT_READ` reports `EV_EOF` with `data` 0, a read returns 0 (Linux fails it with `EIO`), and a write fails with `EIO`. The report repeats on every wait until the master is deregistered, as Architecture already plans.
- **`EV_EOF` sticks.** A slave opened again after its last close left the old registration reporting `EV_EOF` while reads failed with `EAGAIN`: a spinning loop. Only deleting and adding the registration cleared it. So `EV_EOF` is a hint that a read returning 0 confirms. MiniPty never reopens a slave, since the host holds it until the spawn returns, but the loop must not spin if one does.
- **Before the slave first opens,** kqueue reports a master writable while writes fail with `EAGAIN` (`select` reports nothing). MiniPty opens the slave before the spawn, so the loop registers a master only after it exists.
- **Why others avoid it:**
  - In the XNU source up to macOS 14, `O_NONBLOCK` on a master whose slave was never opened failed with `ENOTTY` but set the flag anyway; macOS 15 added an exception, and on macOS 26 it succeeded. That is behind Go issue 22099 and creack/pty's blocking master. Go itself still puts character devices in kqueue.
  - tmux's "kqueue only works on sockets" comment concerns the client's `/dev/null` and `/dev/tty`, which share its one event loop with the masters.
  - XNU builds `poll` on kqueue's filters, so the poll(2) page's "does not support devices" is outdated for masters.

**The buffer and floods:**
- A macOS master holds exactly 1,024 bytes of output (the tty's fixed output ring) and 1,022 of input at every line speed from 9,600 to 230,400 baud. A read returns at most 1,024, so each readiness report moves at most 1 KiB, whatever the mechanism.
- Floods, alternating the three readers over 3 rounds: one flood of 32 MiB, then 8 at once of 8 MiB each. Every byte arrived in every run.

| Reader | 1 flood (MB/s) | CPU per MiB, 1 flood (ms) | 8 floods (MB/s, total) | CPU per MiB, 8 floods (ms) | Reads per wait, 8 floods | Others' share when the first of 8 ended |
|---|---:|---:|---:|---:|---:|---|
| One thread per session (today's model) | 204–285 | 2.3–3.0 | 225–228 | 13.8–14.2 | 1.0 | 80–100% |
| One `select` loop | 154–210 | 3.2–4.1 | 270–284 | 3.4–3.5 | 5.3 | 72–98% |
| One `kqueue` loop | 155–233 | 3.0–3.9 | 297–298 | 3.3–3.4 | 6.5 | 98–100% |

- A single flood moves faster through its own thread, because each KiB costs a wait plus a read. Once several sessions are active, one kqueue loop moves more than a thread per session for a quarter of the CPU, and shares best: `select` reports in descriptor order, which favors low descriptors.

**Many masters:**
- macOS allows 511 PTYs in the whole system (`kern.tty.ptmx_max`; 999 at most, and only root can raise it). The probe opened 436 before the next `posix_openpt` failed with `ENXIO`, because other programs held the rest. 1,000 sessions are impossible on macOS by default, which Acceptance already reflects by asking only 64 there.
- Time from a write on one slave to the wait reporting its master (p50 / p99, µs, 300 rounds):

| Masters | `kqueue` | `select` | `poll` |
|---:|---:|---:|---:|
| 1 | 4 / 14 | 9 / 21 | 10 / 22 |
| 64 | 5 / 28 | 28 / 69 | 31 / 63 |
| 256 | 9 / 21 | 48 / 243 | 68 / 386 |
| 436 | 7 / 29 | 62 / 332 | 121 / 564 |

- Idle for 500 ms, no mechanism reported anything. kqueue used 0.01–0.03 ms of CPU, and `select` and `poll` up to 0.4 and 0.7 ms at 436 masters.
- With all 436 active, each master was reported exactly once.
- Interest that follows state works. A registration disabled and enabled again, or deleted and added again, reports at once the output that arrived meanwhile. Write interest reports every master with room.
- Unlimited `select` worked with masters at descriptors above 4,096.

**End of output at the child's exit:**
- A loop that reads at once loses nothing, also when the child writes more than the tty holds: a child writing 64 KiB blocks until it is read, and output ends at its exit. Output a grandchild writes after the session leader's exit is lost: its write fails with `EIO`, and the master reported end of file at the leader's exit.
- **Output left unread is kept for 0.6 s.** When the exit is the slave's last close, XNU's slave close waits at most 60 clock ticks (0.6 s) for the master to read what the tty holds, then discards it, and the exit completes only then. 900 bytes left unread were all read at 551 ms and none at 601 ms. A child that sets its stdout non-blocking loses it at once.
- **No bound when another process holds the slave.** The session leader's exit then waits for the master however long it takes, and nothing is lost.
- **The same loss existed in MiniPty, and is fixed:**
  - Reproduced with the reader thread: a zsh child printed 13–16 KiB, which filled the ring, then 500 bytes, and exited. A consumer that began reading 2 s later received everything but those 500 bytes, and the exit was reported at 0.94 s. A consumer that read at once received everything.
  - With `/bin/sh` (bash on macOS) the same script's exit waited until the consumer read, and nothing was lost: bash's exit was not the slave's last close. What else held the slave was not identified.
  - **Fix:** on macOS the session now holds a descriptor of the slave from spawn until the master closes, so the child's exit is never the last close. The exit waits for the consumer instead of discarding: a child whose output nobody reads already blocks once the ring and the tty fill, and this moves that point by at most 1 KiB. Disposal still discards the output, so a child held in its exit ends with the session ([Post-exit drain](../references/pty_crossplatform.md#post-exit-drain)).
  - **Tests** (`PtyMacExitDrainTests`): the zsh scenario above receives every byte, and its exit is not reported before the read; disposal ends a child held in its exit, and the slave descriptor closes with the session; a child the host starts does not inherit the descriptor. The first two failed before the fix, with 0 of 500 bytes and no descriptor held, and the third failed with the descriptor opened without close-on-exec.
  - The full suite then passed on macOS 26.6.2: 617 tests, 615 passed, the 2 planned skips.
  - Only macOS holds the descriptor. Linux keeps the bytes readable after the slave closes, and a descriptor of the host would keep output from ending at the exit there. On FreeBSD (source), a last close drains for as long as the master keeps reading and discards after 1 s without progress. When another process still holds the slave, the leader's exit revokes the tty and discards at once, the opposite of macOS.

**FreeBSD from source:**
- `EVFILT_READ` reports the bytes queued and `EVFILT_WRITE` the room left, level-triggered.
- After the slave's last close, `EV_EOF` is set, queued bytes are read first, and then a read returns 0 and a write fails with `EIO`. Nothing is reported before the slave first opens.
- The output queue is the line speed divided by 5 (at most 64 KiB), so about 7.5 KiB at MiniPty's 38,400 baud, against macOS's fixed 1 KiB.
- tmux disables kqueue on FreeBSD for the tty layer before 8.0, which had no working master filter.

**Lessons**
- **Whether an exit is the slave's last close decides between 0.6 s and no bound.** The first exit probe found a forked child discarding its output after 0.6 s, while `sh -c "exec head …"` never exited until read. The variable was not `exec`, since an exec of the probe itself also discarded at 0.6 s. It was another holder of the slave, which bash's exit had and zsh's, `head`'s, and `dd`'s did not. A probe of exit behavior controls who else holds the slave.
- **A forked session leader must ignore `SIGHUP` before it forks the grandchild it means to keep.** The leader's exit sends `SIGHUP` to the foreground group at once, and a grandchild that set its own handler after the fork died first.
- **A size above the tty's buffer cannot test the discard.** The child blocks in its write until the master reads, so it exits only after the read; the first version of the test waited for that exit and hung.

### Phase 5: Windows engine

**Goal:** no thread per Windows session, and no buffer while a session is idle.

**Changes**
- The output pipe becomes an overlapped named pipe. The input pipe's creation is generalized to serve both directions.
- The pipe is bound through `ThreadPoolBoundHandle`, with a `PreAllocatedOverlapped` per session and completion-port notification skipped for reads that finish at once.
- The Windows driver:
  - posts a zero-byte read while the ring is empty;
  - after it completes, reads what the pipe holds;
  - posts reads into the ring during a burst;
  - takes the ring on demand and gives it back when it is empty and released.
- The forced end of output and disposal use `CancelIoEx`. `CancelSynchronousIo`, the reader's thread handle, and the `PIPE_NOWAIT` window and its constants are removed.
- This covers both the in-box and the out-of-band ConPTY.
- The `AppContext` switch that selects the reader-thread driver instead, as a rollback in the field: added here, where a second driver first exists, and used by Phase 6 too.

**Tests**
- Remove the skips of both census tests on Windows.
- Existing suites:
  - end of output with a released console host (2–3 ms after exit);
  - the in-box ConPTY before 24H2, through the drain's close;
  - the forced end when an unrelated process inherited the output pipe (G11);
  - `InheritCursor`, `KillProcessTree`, caller context, and concurrent disposal.
- New tests:
  - disposal during a flood while a read is posted;
  - a completion that arrives after disposal never touches a buffer already returned (stress);
  - a full ring posts no read and resumes when the consumer releases;
  - canceling a zero-byte read and a buffered read.

**Measure:** `PtyConPtyBenchmarks`, the integration benchmarks, the WebSocket benchmark (frames per 32 KiB, echo latency), and the CPU of a trickling child, all against Phase 0.

**Done when:** Acceptance holds on Windows.

### Phase 5 result

Implemented 2026-10-23 and tested on Windows and Linux; Phase 5 changes nothing that runs on macOS but the ring hand-off in the pump, which the full macOS suite covers (`dotnet test -c Release`).

**Delivered:**
- **`WindowsOutputDriver`**, the driver that holds no thread. It waits with a zero-byte read while nothing is unread and holds no ring then. When output arrives, it takes a ring, reads what the pipe holds with reads that complete at once, and publishes each. While output is unread it posts the next read into the ring; when the consumer has released everything, that read is canceled and the ring goes back. On a full ring it posts nothing and parks until the consumer's release wakes it.
- **The output pipe** is the overlapped server end of a named pipe, created by `CreateOverlappedPipe`, which now serves both directions with a 4 KiB buffer each. It is bound with `ThreadPoolBoundHandle`, with completion-port notification skipped for reads that complete at once.
- **Rings taken on demand.** The pump no longer rents its ring at construction. `HoldRing` takes one when the driver reads, and `LetGoOfRing` gives it back, at once when nothing is unread, otherwise when the consumer releases the last byte or the session is disposed. `WakeDriverWhenEmpty` is how the Windows driver learns that the read it left posted is all that keeps the ring. The reader thread holds its ring from its first read to end of output, as before.
- **Stopping.** The forced end of output after the close cancels the posted read with `CancelIoEx` and takes what `PeekNamedPipe` reports without waiting. Disposal closes the pipe, which ends the posted read.
- **Pooled driver objects.** Each driver owns a `PreAllocatedOverlapped`, 208 B to create, so drivers are pooled across sessions like reader threads; a session pays only its pipe's binding, 40 B.
- **The rollback switch**, `MiniPty.UseOutputReaderThreads`, read when each session starts (`PtyOutputDrivers`). It selects the reader-thread driver over an anonymous pipe, exactly as before this phase. Unix reads with reader threads either way until Phase 6.
- **Tests:** the census tests run on Windows and pass (they skip on Unix until Phase 6). `PtyWindowsOutputDriverTests` drives the driver over a plain named pipe whose writer the test controls:
  - output in order, and its end when the writer closes;
  - a full ring posts no read, and resumes on the consumer's release;
  - an idle session gives its ring back and still reads the next output;
  - the forced end cancels a posted zero-byte read, and a posted ring read whose output is still unread;
  - the drain ends output after the driver parked;
  - disposal during a flood, and disposal while the pipe never runs empty;
  - a completion after disposal never touches a returned ring;
  - the rollback switch selects reader threads.

**Deviation: the reader thread's Windows code stays.** The plan removed `CancelSynchronousIo`, the reader's thread handle, and the `PIPE_NOWAIT` window here. But a rollback has to read the way the code did before, and a blocking read is not allowed on a handle opened for overlapped I/O. So the switch also brings back the anonymous pipe, and that code goes with the reader thread in Phase 9.

**Found and fixed during the phase** (the lessons are in [core_session.md](../specs/core_session.md#lessons-learned)):
- **A step must give its thread back.** A step that kept finding output held its pool thread, and its pin on the pipe, for as long as the child wrote. After disposal this was a livelock: about one of a thousand disposals during a flood never completed. Steps now continue as a new work item after 16 reads.
- **A resumed driver ends the free-space handshake.** Otherwise the drain never saw the transport go quiet, and a session whose pipe stayed open never reached end of output.
- **Early completions.** A completion can arrive before its step has recorded the posted read; it is handed to that step. The posting step does all its work before recording the posted state.

**Unplanned: Capture joins close reads itself.** A sink is written once per read, so with no coalescing window Capture recorded one chunk per ConPTY slice again: a 32 KiB capture allocated 4.7 KB more (9.3 KB with text). The Capture sink now joins a read into the previous chunk within the reader thread's own windows (500 µs from the previous read, 2 ms from the chunk's first, 4 KiB at most), decided from the reads' timestamps as each write arrives. That is the consumer-side batching the plan calls for, applied where the consumer is a sink. `CaptureJoinsReadsThatFollowClosely` covers it.

**Unplanned: one test measured the process.** `SinksFedAndFinishedOnOtherThreadsAllocateNothingBeyondTheResult` counted all allocation in the process, and failed now and then at 4.2 KB against its 4 KiB limit, from work elsewhere in the test host. It now counts per thread, on the threads that run the sink, with a 1 KiB limit. The Phase 2 sink still fails it, at 4.9 KB and 80 KB per run.

**Measured** (A/B against Phase 4, alternating the builds; the trickle numbers compare the two drivers of one build through the rollback switch):

`WebTerminalScale bench --sessions 16,64,128 --echo 200` on Windows, two rounds each:

| | Phase 4 | Phase 5 |
|---|---:|---:|
| Other threads per session (16 / 64 / 128 sessions) | 0.94 / 1.00 / 1.01 | 0.00 / 0.02 / 0.02 |
| Threads at 128 sessions | 209–213 | 100–101 |
| Handles at 128 sessions | 2,187–2,205 | 1,739–1,759 |
| Working set per session at 64 sessions (KB) | 312–326 | 276–293 |
| Echo p50 / p99 at 64 sessions (ms) | 0.70–0.71 / 0.90–0.97 | 0.19–0.22 / 0.42–0.45 |
| Flood: frames per 32 KiB (Phase 0: 8.0) | 6.2 | 5.5–5.6 |
| Flood throughput (MB/s; ConPTY-bound) | 19.1 / 17.8 | 17.2 / 19.4 |

- The pool threads at 64 and 128 sessions (35–52) come from the burst of connections and retire later, as in Phase 0; the census test, which waits for that, passes.
- Echo is about 0.5 ms faster at p50, the 500 µs coalescing window the reader thread no longer waits out.

Host CPU while children trickle 20 bytes per write, one run each (% of one core):

| Sessions, interval | Reader threads | Completions |
|---|---:|---:|
| 1, 1 ms | 9.7 | 1.9 |
| 1, 5 ms | 7.5 | 0.5 |
| 1, 10 ms | 2.0 | 0.8 |
| 8, 1 ms | 49.2 | 18.3 |
| 8, 5 ms | 52.7 | 7.0 |
| 8, 10 ms | 22.3 | 6.1 |

With completions at 8 sessions and 1 ms, ConPTY delivered 107 KB/s against 66 KB/s, so that row also carries more output.

BenchmarkDotNet on Windows, two rounds each (allocation; times moved within the spread between runs of the same build):

| Benchmark | Phase 0 | Phase 4 | Phase 5 |
|---|---:|---:|---:|
| Integration `Session_Exit0_WaitForExit` | 1.25 KB | 1.24 KB | 1.28 KB |
| Integration `Session_Exit0_Bytes` | — | 1.77 KB | 1.80 KB |
| Integration `Session_32KiB_Bytes` | 38.37 KB | 38.30–38.31 KB | 38.34 KB |
| Integration `Session_32KiB_StreamBytes` | 1.55 KB | 1.55–1.70 KB | 1.59 KB |
| Integration `Capture_32KiB_Bytes` | 39.78 KB | 39.71–39.77 KB | 39.77 KB |
| Integration `Capture_32KiB_Text` | — | 113.93 KB | 113.98 KB |
| ConPty `Capture_32KiB_Bytes`, in-box / out-of-band | — | 39.71–39.77 / 35.35 KB | 39.77 / 35.41 KB |
| WebSocket `Bridge_32KiB_Bytes` | 26.43 KB | 26.18–26.25 KB | 26.19–26.44 KB |

- **Every session start costs 40 B more**: the pipe's binding to the completion port (`ThreadPoolBoundHandle`), the only allocation above Phase 0. `ThreadPool.BindHandle` with a reused `Overlapped` costs nothing, but it throws `PlatformNotSupportedException` when the host runs on the Windows thread pool (`System.Threading.ThreadPool.UseWindowsThreadPool`), where `ThreadPoolBoundHandle` works (72 B there). Reads, chunks, and exits allocate what they did. Accepted on 2026-10-24 as the price of having no thread per session (see Acceptance below).
- The Capture rows are with the joining above; without it they were 44.4 KB and 123.3 KB.

**Tests:** the full suite passed on Windows (628 tests) and on Linux (628 tests, 626 passed and the 2 census tests skipped, as they are until Phase 6). The driver, census, and ConPTY suites also pass with the host on the Windows thread pool.

**NativeAOT:** `WebTerminalScale` (with `--smoke`), `Capture`, and `Session` publish without warnings and run on Windows.

**Acceptance on Windows:**
- **Held:** no thread grows with idle sessions; an idle session holds no output buffer; reads allocate nothing; echo latency is better and throughput the same; frames per 32 KiB are fewer than in Phase 0; NativeAOT works.
- **Accepted exception (2026-10-24):** session start is 40 B above Phase 0, the completion-port binding (above). It is the price of having no thread per session, and the binding-free alternative fails on the Windows thread pool. The "not above the Phase 0 baseline" criterion therefore allows this 40 B for a Windows session start; reads, chunks, and exits stay at their baseline.
- **Not measured on Windows:** 1,000 idle sessions (the bench went to 128), and the Linux NativeAOT publish, which this phase does not change.

### Phase 6: Unix engine

**Goal:** one thread per load for all Unix sessions.

**Changes**
- **Native watcher:** masters are registered, modified, and removed with read and write interest and a session token. The token is an index plus a generation, so a stale event cannot reach a reused slot. The wait returns master events alongside exits. On macOS and FreeBSD it is the existing kqueue, as Phase 4 chose, used as its result requires: registered after the slave is open, `EV_EOF` confirmed by a read that returns 0, and the master deregistered at end of output.
- **The watcher loop becomes the I/O loop:**
  - non-blocking reads into rings taken on demand, bounded per iteration;
  - interest that follows ring and input state;
  - deregistration at end of output;
  - coalescing deadlines;
  - queued input written when the master is writable.
- Disposal's discard path merges with normal reading.
- **Delivery order:** Linux first, then macOS and FreeBSD, in separate pull requests. The reader-thread driver and its wake descriptors stay until Phase 9.

**Tests**
- Remove the skips of both census tests on Linux and macOS.
- Existing Unix suites: disposal and hang-up, line discipline, EOF staging, input against a full tty queue, `AssemblyLoadContext` isolation, macOS descriptor inheritance, and pid reuse.
- New tests:
  - **Fairness:** a flooding session does not delay another session's echo beyond a bound.
  - **Fault containment:** a read failure injected into one session ends only that session.
  - **No spinning:** a master that stays ready after end of output does not spin the loop.
  - **Churn:** 1,000 sessions opened and closed.

**Measure**
- The integration and input benchmarks, and the WebSocket benchmark, on Linux and macOS.
- Then compare the 1 ms window with consumer-side batching, and record the single policy chosen for both platforms.

**Done when:** Acceptance holds on Linux, then on macOS.

### Phase 6 result (Linux)

Implemented 2026-10-25 for Linux, as the delivery order above asks. macOS and FreeBSD keep reader threads until their kqueue loop lands; the native calls that register a master fail with `ENOTSUP` there, so nothing changes on those platforms yet.

**Delivered:**
- **Native watcher** (`minipty_unix_exit.c`): `minipty_watch_add`, `minipty_watch_modify`, and `minipty_watch_remove` register a master with read and write interest, level-triggered, under a token whose top bit sets it apart from children's reports. `minipty_watch_wait` replaces the child-only wait and reports masters' readiness (readable, writable, hang-up, error) alongside children's exits. Interest 0 arms the registration for one report (`EPOLLONESHOT`).
- **The watcher loop is the I/O loop** (`ChildWatcher`). A master's token is its slot plus the slot's generation, so a report for a freed slot is dropped. Each pass:
  - reaps reported children;
  - serves each reporting master up to four reads (16 KiB) into its ring, taken on demand, and writes its queued input;
  - looks at each session that asked: the consumer freed space, input was queued, a sink attached, the close or disposal began;
  - publishes coalescing windows whose deadline passed.
- **The session drives itself** (`UnixPtyBackendInstance` is the driver; `ServiceOnLoop`):
  - interest follows state: output while the ring has room, input while some is queued;
  - the free-space handshake parks it on a full ring;
  - end of output removes the master before anything else, and the loop's own reference on the master keeps the descriptor open until then;
  - a failure ends that session's output alone.
- **No coalescing window:** each read is published as it completes, the policy the Windows driver has, decided below. The first version kept the 1 ms window and 2 ms budget as loop deadlines, which the comparison below measured.
- **Disposal merges with reading:** disposal finishes the pump at once, as before, and the loop keeps the hung-up child's master registered and discards its output until the master closes. That replaced the watcher's separate discard path for these sessions.
- The rollback switch selects reader threads on Linux too. Reader threads and their wake descriptors stay until Phase 9.

**Tests:**
- The census tests run on Linux and pass; they still skip on macOS and FreeBSD.
- `PtyUnixLoopTests`:
  - a flood does not delay another session's echo;
  - an injected read failure ends only its own session;
  - a hung-up master on a full ring does not spin the loop, and its output still arrives in full (with interest 0 left level-triggered, the loop made about 750,000 passes in 500 ms);
  - 1,000 sessions opened and closed leave no thread, descriptor, or ring behind;
  - the rollback switch.
- Two disposal tests, each failing every time with the first Phase 6 code: `DisposeDiscardsWhatAHungUpChildWritesWhenTheLoopSeesTheDisposalFirst` and `DisposeFailsAQueuedWriteWhenTheLoopSeesTheDisposalFirst` (see Found and fixed).
- The fairness test passed with the per-pass bound removed too, also with the flood going to a one-shot result. A terminal delivers output more slowly than the loop reads it, so the bound is insurance rather than something a test can show (see [core_session.md](../specs/core_session.md#lessons-learned)).
- The full suite passed on Linux: 635 tests, all passed (the census tests run there now), in four consecutive runs after the disposal fixes.

**Found and fixed: two races in disposal.** Session disposal marks the pump disposed first and only then disposes the backend, which fails queued writes and hangs the child up. A loop that served the session in between, which a loaded full suite made it do now and then, ended the session as if output had ended:
- it removed the master, so nothing discarded what the hung-up child wrote next, and the child blocked until the grace kill (one of seven full Linux runs);
- it closed the input as the end of output, so a queued write failed with `IOException` rather than `ObjectDisposedException` (one of two runs).

The loop now treats a disposed pump as the disposal it is: it keeps discarding while the master stays open, and fails queued input as disposed. The hang-up also asks whether the loop still has the master registered. Both tests mark the pump first and let the loop see it, so each race happens every time; both failed every time with the first code.

**Measured** on Linux (WSL2, 8 logical CPUs), A/B against Phase 5 with alternating builds.

`WebTerminalScale bench --sessions 16,64,256,1000 --echo 200 --flood-bytes 33554432`, two rounds each of the final build (no window):

| | Phase 5 (reader threads) | Phase 6 (loop) |
|---|---:|---:|
| Other threads per session (16 / 64 / 256 / 1,000 sessions) | 0.94 / 0.98 / 1.00 / 1.00 | 0.00 / 0.00 / 0.00 / 0.00 |
| Threads at 1,000 sessions | 1,051 | 31–50 |
| Private (reserved) memory at 1,000 sessions (MB) | 8,448–8,454 | 240–396 |
| Working set at 1,000 sessions (MB) | 150–151 | 116–118 |
| Descriptors at 1,000 sessions | 4,188 | 3,188 |
| Echo p50 / p99 at 64 sessions (ms) | 1.47 / 1.59 | 0.38–0.39 / 0.50–0.55 |
| Flood: frames per 32 KiB (Phase 0: 8.0) | 7.7–7.8 | 15.9–18.4 |
| Flood throughput (MB/s) | 115.1–126.0 | 129.1–131.8 |

- The threads and private memory at 1,000 sessions vary with the pool workers the burst of connections injected (9 to 28 of them in both builds).
- Each session costs one descriptor less: its reader thread's wake descriptor.

BenchmarkDotNet (allocation; times moved within the spread between runs, the loop's slightly faster): a session start allocates 10–20 B more than in Phase 5, for the loop's per-session state on the backend. For example, `Session_Exit0_WaitForExit` went from 1.22 to 1.23 KB, `Session_Exit0_Bytes` from 1.63 to 1.65 KB, and `Session_32KiB_StreamBytes` from 1.52 to 1.53 KB. Reads, chunks, and exits allocate what they did. Input writes stayed at 2 B, and `Bridge_32KiB_Bytes` at 26.4 KB.
- A first layout cost 48 B. Storing the slot's generation instead of the whole token, and the interest in a byte, brought it to 40.
- Dropping the coalescing window then removed 20 B more: the open publish, its first read's time, and its deadline.

**The coalescing policy, compared** (the loop without its window publishes each read as it completes, as Windows does):

| | Loop, 1 ms window | Loop, no window |
|---|---:|---:|
| Echo p50 / p99 at 64 sessions (ms) | 1.47–1.48 / 1.58–1.68 | 0.41–0.44 / 0.55–0.61 |
| Flood: frames per 32 KiB (Phase 0: 8.0) | 7.7–7.8 | 15.2–17.1 |
| Flood throughput (MB/s) | 111.3–129.7 | 111.9–121.0 |

Without the window, echo is about 1 ms faster, and a flood takes about twice as many frames, smaller ones (1.9–2.1 KiB on average). That is around the "more than twice the Phase 0 baseline" that Acceptance says needs a decision. Windows already publishes without a window, at 5.5–5.6 frames per 32 KiB, because ConPTY's slices are smaller and its flood is ConPTY-bound.

**NativeAOT:** `WebTerminalScale` publishes on Linux without warnings, links the native code statically, and passes `--smoke`.

**Decision (2026-10-25): one policy, no window.** The Linux loop publishes each read as it completes, as the Windows driver does, and the doubled frame count of a flood is accepted. A bridge that wants fewer frames can batch on its side, the remedy this plan names. The reader threads of macOS and FreeBSD, and of the rollback switch, keep their windows until Phase 9, and the kqueue loop follows the new policy. Removing the window also removed 20 B of per-session state (see Measured), and the final build measured echo at p50 0.38–0.39 ms and 15.9–18.4 frames per 32 KiB.

**Next:** macOS and FreeBSD on kqueue, as Phase 4 chose and with its rules: the master registered after the slave opens, `EV_EOF` confirmed by a read that returns 0, and the master deregistered at end of output. Done for macOS the same day; see [Phase 6 result (macOS)](#phase-6-result-macos).

### Phase 6 result (macOS)

Implemented 2026-10-25 on macOS 27.0.1 (Darwin 27.0, Apple M2, 8 logical CPUs, .NET 10.0.13). FreeBSD runs the same kqueue code, decided from source and not run; see [Phase 4 result](#phase-4-result).

**Delivered:**
- **Masters on the watcher's kqueue** (`minipty_unix_exit.c`). A master's read and write interest are its `EVFILT_READ` and `EVFILT_WRITE` registrations, each enabled or disabled with the interest. Add and modify are one `kevent` call with `EV_RECEIPT`, so every change is applied and reported instead of the call stopping at the first failure. The token rides in the event's user data, and a 32-bit build, whose user data cannot carry it, reports `ENOTSUP` and reads on reader threads.
- **One report per master per wait.** The wait joins a master's read and write events into one report, as epoll gives, so the loop serves a session once per wait. `EV_EOF` is reported as hang-up and `EV_ERROR` as an error.
- **Interest 0 is silent.** It disables both filters, which then report nothing, end of file included. kqueue needs no counterpart of the `EPOLLONESHOT` arming Linux uses for a parked master.
- **The rules of Phase 4:**
  - the master is registered after the slave opens, which the spawn already does before output starts;
  - `EV_EOF` ends output only on a read that returns 0;
  - the master is removed at end of output, as on Linux;
  - new: when a hang-up report's first read finds nothing, the loop renews the registration (`minipty_watch_renew`, which deletes and adds both filters in one call). That clears the `EV_EOF` a slave opened again leaves behind, which would otherwise spin the loop. MiniPty never opens a slave again, but another process can.
- **The loop on every Unix.** `StartOutput` no longer checks for Linux; a master the loop cannot register reads on a reader thread, as it already did when the watcher had no epoll. The rollback switch covers macOS too.
- **No coalescing window,** the policy decided for Linux.

**Tests:**
- The census tests run on macOS and pass. No platform skips them any longer.
- `PtyUnixLoopTests` run on every Unix:
  - the hang-up test sizes its output by platform. On macOS a child's exit waits until the master has read what the terminal holds, because the session holds a descriptor of the slave, so the master hangs up while parked only when the terminal is empty: the child writes exactly what fills the ring. FreeBSD discards what a last close leaves unread after 1 s, so it takes the same size;
  - new, `AParkedMasterWithUnreadOutputDoesNotSpinTheLoop`: a child blocked in its write while its session is parked, so the master stays readable. With read interest left on while parked, this test and the hang-up test both failed on macOS, at 50 passes or more in 500 ms.
- `PtyKqueueWatchTests` (new) drive the native calls on a PTY pair the test owns: the two filters join into one report; interest 0 reports neither unread output nor a hang-up, and enabling it again reports both at once; a slave closed and opened again leaves `EV_EOF` repeating while reads fail with `EAGAIN`, and renewing clears it. With the renewal reduced to a modify, or the joining removed, the matching test failed.
- The two disposal races found on Linux (`...WhenTheLoopSeesTheDisposalFirst`) run on macOS too, and pass.
- The full suite passed on macOS: 639 tests, all passed, in 4 consecutive runs after the last test fix below, and every test class passed run alone.

**Found and fixed in the tests:**
- **The flood test reached the macOS line limit.** `AFloodDoesNotDelayAnotherSessionsEcho` writes one character per echo and no newline. The loop echoed fast enough that 500 ms of echoes put more than 1,024 bytes on one line, after which a macOS terminal drops input and echoes BEL ([macOS line limit](../references/pty_crossplatform.md#unix-stdin-eof)), and the test waited 30 s for an echo that never came. The test now erases each character after its echo. A C probe on the same kqueue setup, one flood and one echo master, reported every echo within 3 µs of the write.
- **A chunk test asserted the reader thread's window.** `PtyReadOutputAsyncPreservesLargeChunksOnUnix` expected 32 KiB in at most 16 chunks, one of them 4 KiB or more. With the loop a macOS consumer that keeps up gets each 1 KiB read as a chunk of its own, and the test failed 6 of 6 times when run alone; in full runs it passed whenever load slowed its consumer. What the pump guarantees is that output piling up while the consumer is busy goes out as one chunk, so the test's consumer now waits 10 ms before each chunk, and it passed 10 of 10 runs alone.
- **A test's own PTY must be close-on-exec.** `PtyKqueueWatchTests` first opened its PTY without it, and in 1 of 9 full runs a child that another test started meanwhile inherited the slave, so closing it did not end output. Both ends now open close-on-exec, and a hang-up is waited for rather than expected on the first wait, because a child forked in the instant before its exec still holds a copy.

**Measured** on macOS, A/B against the Linux commit of this phase, in which macOS still read on reader threads, alternating the builds.

`WebTerminalScale bench --sessions 16,64,256 --echo 200 --flood-bytes 33554432`, two rounds each (macOS allows 511 PTYs in the whole system, so 1,000 sessions cannot run):

| | Reader threads | Loop |
|---|---:|---:|
| Other threads per session (16 / 64 / 256 sessions) | 0.94 / 0.97 / 0.99 | 0.00 / 0.00 / 0.00 |
| Threads at 256 sessions | 308 | 33 |
| Working set at 256 sessions (MB) | 109.5–109.7 | 97.4–97.8 |
| Echo p50 / p99 at 64 sessions (ms) | 1.48–1.49 / 1.89–2.07 | 0.10–0.12 / 0.30–0.37 |
| Echo p50 / p99 at 256 sessions (ms) | 1.49–1.54 / 2.49–3.19 | 0.10–0.13 / 0.27–0.36 |
| Flood: frames per 32 KiB (Phase 0: 8.0) | 7.9–8.0 | 33.0–33.3 |
| Flood throughput (MB/s) | 63.4–65.5 | 56.2–57.2 |

- The loop's thread counts were 0.06 below the baseline at most, which the table rounds to 0.00.
- Echo is about 1.4 ms faster: the reader thread's 1 ms window, and its wait on a thread of its own, are gone.
- **A flood takes four times the frames and is about 12% slower.** A macOS master holds 1 KiB, so each read returns at most 1 KiB, and the loop publishes each read as it completes. The bridge, which keeps up, sends each as a frame of its own (1.0 KiB on average). The reader thread's window joined four reads into one 4 KiB publish. On Linux, whose reads are larger, the same policy doubled the frames; here it quadruples them.

The evidence points to the consumer, not to the loop. A flood read directly through `ReadOutputAsync`, with the two drivers of one build through the rollback switch, three rounds of one flood and two of eight:

| | Reader threads | Loop |
|---|---:|---:|
| 1 flood of 32 MiB (MiB/s; `tr` in the child sets the rate) | 21.7–21.9 | 21.7–21.8 |
| Host CPU per MiB, 1 flood (ms) | 20.1–21.9 | 33.1–33.8 |
| 8 floods of 8 MiB at once (MiB/s, total) | 94.5–95.3 | 97.7–97.8 |
| Host CPU per MiB, 8 floods (ms) | 13.7–15.0 | 13.5–13.7 |
| Others' share when the first of 8 ended | 88–99% | 98% |

- Read without the bridge, one flood arrives as fast either way, and the loop shares eight floods better at no more CPU.
- One flood costs the host half again as much CPU with the loop. A sample of the process put the loop's thread in `kevent` for most of the time, and showed thread-pool workers spinning between wake-ups, which a consumer woken once per KiB instead of once per 4 KiB pays four times as often.
- **Not the cause:** `EVFILT_READ` reports how many bytes the master holds, so the loop could skip the read that returns `EAGAIN` after each KiB. With that skip the bridge flood measured 56.2–59.4 MB/s, against 57.4–58.0 MB/s without it, three rounds each, so the loop reads until `EAGAIN`, as on Linux.

BenchmarkDotNet, one round each (time, allocation):

| Benchmark | Reader threads | Loop |
|---|---:|---:|
| Input `WriteInputBytes` / `WriteInputText` / `InputStreamWriteAsync` | 162.7 / 164.2 / 162.0 µs, 2 B | 103.9 / 111.2 / 110.0 µs, 2 B |
| Integration `Session_Exit0_WaitForExit` | 6.16 ms, 1.24 KB | 6.13 ms, 1.24 KB |
| Integration `Session_Exit0_Bytes` | 6.17 ms, 1.66 KB | 5.95 ms, 1.66 KB |
| Integration `Session_32KiB_Bytes` | 32.0 ms, 34.14 KB | 31.9 ms, 34.00 KB |
| Integration `Session_32KiB_StreamBytes` | 32.0 ms, 1.74 KB | 31.4 ms, 1.60 KB |
| Integration `Session_32KiB_OutputStreamBytes` | 30.6 ms, 1.84 KB | 31.6 ms, 1.70 KB |
| Integration `Capture_32KiB_Bytes` | 32.7 ms, 35.50 KB | 31.6 ms, 35.36 KB |
| Integration `Capture_32KiB_Text` | 31.6 ms, 100.88 KB | 31.3 ms, 100.81 KB |
| WebSocket `Bridge_32KiB_Bytes` (three runs) | 32.0–32.2 ms, 26.67–26.84 KB | 31.9–32.5 ms, 26.56–28.29 KB |

- Every allocation is the same or lower, by up to 140 B, except the bridge's, whose spread grew with its frame count; its time is the same.
- A session start costs nothing more than with reader threads: the loop's 10–20 B of per-session state, which the Linux result records, are on every Unix session since that commit.
- **Input is about a third faster.** These writes come faster than the terminal takes input. The loop writes queued input as soon as the master reports room, while a reader thread inside its 1 ms window waits for output only.

**NativeAOT:** `WebTerminalScale` publishes for `osx-arm64`, links the native code statically, and passes `--smoke`. The linker warns that the archive was built for a newer macOS than the target, the open item on the minimum macOS version in [next_todo.md](../next_todo.md). With the Homebrew build of the .NET SDK, the link also needs `LIBRARY_PATH` to name Homebrew's `lib`, for the OpenSSL and Brotli libraries the SDK's own native code links; that is the machine, not MiniPty.

**Acceptance on macOS:**
- **Held:** no thread grows with 64 or 256 idle sessions; an idle session holds no output buffer (the census tests); reads allocate nothing, and session start, chunks, and exit allocate no more than with reader threads; echo latency is better at p50 and p99; the full suite passes; NativeAOT works.
- **Not held:** a flood through the bridge takes 33 frames per 32 KiB, four times Phase 0, and its throughput is about 12% lower.

**Decision (2026-10-25): the core stays as it is, and the bridges coalesce bursts (Phase 7).**
- **No window in the core.** Every engine publishes each read as it completes, on every platform, and a macOS window would split that policy by platform. On Linux the whole echo gain came from dropping the window (p50 1.47 ms on the loop with it, 0.41–0.44 ms without), and macOS's 1.5 ms to 0.1 ms is of the same kind. A window would also bring deadlines back into the loop, which the Linux loop has just shed.
- **Nothing to change in kqueue or the loop.** The 1 KiB is the macOS tty's fixed output ring, which reader threads read 1 KiB at a time too ([Phase 4 result](#phase-4-result)). Read without the bridge, the loop delivers one flood as fast as reader threads and eight floods faster, and skipping the read that returns `EAGAIN` changed nothing.
- **Coalescing on macOS needs a wait, and the wait belongs to the consumer.** Before the child writes its next KiB, a reader cannot tell a flood that goes on from one that has ended, so joining reads there means waiting, which trades latency for frames. The party that pays per frame is the bridge (one WebSocket message per chunk, and one event per message in the browser), so the trade is the bridge's to make, as this plan's Coalescing section already says. It is not a macOS special case: Linux floods take twice Phase 0's frames, and the session manager sends frames the same way.
- **Severity:** the cost shows where the network is faster than the terminal (loopback, a LAN), as CPU at both ends; no output is lost, and interactive output is not delayed. It does not hold back Phase 6, but Phase 7 gates the release, as Acceptance asks for a decision before it.

**Next:** Phase 7; FreeBSD stays from source until a machine runs the probe and the suite there; then Phases 8 and 9.

### Phase 7: Coalesce bursts in the bridges

**Goal:** a flood through a bridge goes out in large frames on every platform, while an echo still goes out at once. The core keeps publishing each read as it completes.

**Changes**
- `PtyWebSocketBridge` and `PtyWebSocketSessionManager` coalesce while output arrives in a burst. A chunk after idle goes out at once. During a burst the bridge takes its next chunk only once enough output is waiting or a short bound has passed, so the ring holds what arrived meanwhile and the next chunk carries it without a copy. Whether `PtyStdioBridge` does the same is decided here too.
- The decision comes from the traffic, never from an operating-system check.
- **Waiting must not wake the bridge once per read.** That wake-up, and the thread-pool workers spinning after it, is what made a single macOS flood cost the host half again as much CPU. If this needs the pump to signal a consumer at a byte threshold, that signal is internal and requested by the consumer: the pump still never holds output back on its own.
- **Bounds:**
  - Size: the pump stops reading with less than 4 KiB free, so about 12 KiB is the most one chunk can count on.
  - Time: about 1 ms on Unix. Windows timers tick at about 15.6 ms, so the time bound there is part of the design; Windows floods already take 5.5 frames per 32 KiB, bound by ConPTY.
- [terminal.md](../specs/terminal.md) records the policy and its bounds.

**Tests**
- An echo after idle is sent at once, and one during a flood waits no longer than the bound.
- A flood's frames grow, and the end of a burst goes out within the bound.
- Flow control, the session manager's replay offsets, and disposal behave as before.

**Measure:** on Windows, Linux, and macOS, against Phase 6: frames per 32 KiB and throughput of a flood through the bridge, echo p50 and p99, the host's CPU per MiB of a flood, and `Bridge_32KiB_Bytes`.

**Done when:** a macOS flood through the bridge is no slower than with reader threads (63–66 MB/s on the machine above) and takes at most twice Phase 0's frames; echo stays at Phase 6's p50 and p99; and no platform regresses.

### Phase 7 result (macOS)

Implemented 2026-10-25 and measured on macOS, on the machine of the Phase 6 macOS result. Windows and Linux are not measured yet.

**Delivered:**
- **Coalescing reads** (`PtyOutputCoalescing.cs`). `PtySession.ReadOutputCoalescedAsync` is internal, and `PtyTerminal` uses it when the internal `PtyTerminalOptions.CoalesceBursts` is set. All three bridges set it: `PtyWebSocketBridge`, `PtyWebSocketSessionManager`, and `PtyStdioBridge`, which sends a frame per chunk as the WebSocket bridge does.
  - A reader enters a burst once it has been handed 4 KiB within 1 ms. An echo never comes near that rate, but a single chunk of 4 KiB counts.
  - In a burst it takes its next chunk once 8 KiB is unread, or once the wait's deadline, 1 ms after it asked, has been served.
  - A wait that output completes before its deadline keeps the burst. One completed later keeps it while the output unread when the consumer resumes, including any left unread when it asked, amounts to 2 KiB or more per millisecond since it asked, half the rate that starts a burst. Otherwise the burst ends, and what the late wait left unread does not count toward the next burst, however many chunks it comes out as.
- **A wait with a minimum in the pump.** `ArmWait` takes the least unread output that completes the wait, and a publish below it does not wake the consumer. A flood therefore wakes the bridge at most once per chunk it takes, 8 KiB on average in the flood below, instead of once per 1 KiB read. `RelaxWaitMinimum` lowers the minimum to 1 byte at the deadline. A plain read asks for 1 byte, which is the previous check exactly, so the pump still never holds output back on its own.
- **One deadline tick per process** (`PtyOutputCoalesceScheduler`): 1 ms, running while it holds a reader, until a tick finds none of them with a deadline, as the post-exit drain's 10 ms tick runs only while some session drains after its child exited. A reader the scheduler still holds arms its deadline without the scheduler's lock, a served deadline keeps the reader for one more tick so that the next wait of its burst finds it held, and joining or leaving costs O(1).
- **Nothing more per plain session.** The coalescing state lives in a `PtyCoalescingOutputReader`, which replaces the session's reader on its first coalescing enumeration, and the pump's minimum is a `short` where a `bool` was. Measured with a probe on the final code, a pump is 184 B and a plain reader 128 B, as in Phase 6. A bridge's coalescing reader and its enumerable take 208 B together, once per session.
- `WebTerminalScale bench` reports the host's CPU per MiB of its flood.

**Found and fixed during the phase:**
- **A wider field grew every session.** The first version kept the minimum in an `int` in place of the `bool`, and every session's pump grew from 184 to 192 B, also for sessions that never read through a bridge (`Session_Exit0_WaitForExit` 1.24 to 1.25 KB). Its state inside the reader grew every reader from 128 to 136 B. Both are back, as the lesson in [core_session.md](../specs/core_session.md#lessons-learned) says: a session's fields are paid by every session.
- **A frame-size test runs alone.** In one of four full parallel runs, the bridge's flood test took 263 frames for 1 MiB (3.89 KiB on average) against about 130 alone. A loaded machine slowed the flood below a burst's rate now and then, and each such slowdown hands out what has arrived, as it should. The frame tests are `[NotInParallel]`, like the loop's flood test.
- **A test of joined chunks needs the platform's chunk limit.** The pump tests' scripted backend caps a chunk at 4 KiB (`OutputChunkLimit`), so the first test of an 8 KiB chunk got 4 KiB. The new test backend allows the ring, as the platform backends do.

**Found and fixed in review.** An adversarial review of the macOS implementation ran in rounds. It kept each claim only after a probe, a failing test, arithmetic, or the cited source showed it, and two of three independent verifiers failed to refute it. The first round:
- **A stale deadline could strand output.** A deadline that outlived its wait could lower the next wait's minimum to 1 byte while that wait was being armed. The arming re-check compared against the minimum it was given, and the deadline's signal found no wait yet, so the output stayed unread until the next publish, with no time bound. The re-check now reads the minimum the pump holds. `RelaxWaitMinimum` also signals when the minimum is 1 already, a safeguard that completes any wait with output unread at its deadline.
- **The chunk a deadline took started the next burst.** It piled up over the whole wait, so a chunk of 4 KiB or more counted as 4 KiB within 1 ms, and the next read waited again. That output no longer counts.
- **The end of a burst depended on the tick.** A burst ended only when a tick served the deadline. With a late tick, as on a coarse timer, output that came after the deadline but before the tick completed the wait at its minimum and kept the burst. A tick that ran after output had completed a wait in time ended the burst. The consumer now compares when the wait was completed with its deadline.
- **`ReadOutputAsync` with a token grew by 8 B.** The enumerable that carries the token gained a coalescing flag, 40 B instead of 32. Coalescing reads now have an enumerable of their own.
- **Every coalescing wait took the scheduler's lock**, and leaving the scheduler scanned its array. A held reader now arms without the lock, and leaving moves the last reader into the freed place.
- **The coalescing state was a separate object**, reached under three names. It is now the reader's subclass.
- **Tests did not cover** a deadline with nothing unread, a minimum already at 1 byte, the time bound with the real tick, or the stdio bridge's frames, and several relied on the real tick's timing.
- **README** did not say that only the built-in bridges join bursts, which a custom transport needs to know.

The second round:
- **Staying in a burst took twice the rate that starts one.** A wait had to fill the 8 KiB minimum within 1 ms to keep the burst, while 4 KiB within 1 ms starts one. A steady stream between those rates left and entered a burst on every wait. Under load the verifiers measured 1.4–3.4 times the reader threads' frames at 4.5–7 MB/s. A late wait now keeps the burst while what is unread then amounts to 2 KiB or more per millisecond of the wait, half the starting rate.
- **Output a late wait left unread still counted when it wrapped the ring.** It came out as two chunks, and only the first was skipped, so the next output could wait for a deadline again. The skip now counts bytes.
- **A served deadline let the reader go,** so the next wait of the same burst took the scheduler's lock and restarted the timer. The scheduler now keeps it for one more tick.
- **Tests did not catch** a read in a burst that already finds the minimum unread, which a broken line would make spin, a burst started by several small chunks, the arming re-check of a stale deadline, or the scheduler keeping a reader that armed while a tick dropped it. Each now has a test that fails when its line is broken. The burst window is writable for the second, and `ArmWait` has a hook for the third, a static field read once per wait.
- **Docs and comments** were corrected in about twenty places, among them the persistent-sessions bridge's frames, the measured builds behind each figure, and the test premise on Linux.

The third round:
- **The keep rule counts what the bridge left unread.** Output already unread when the read asked counts as if it came during the wait. Output that piles up while the bridge sends therefore keeps a stream below the keep rate in a burst when the sends take long enough, and each chunk then waits until its deadline is served. In the review's probes of a 1 MB/s stream sent at 2.5 ms per chunk, 83–100% of the chunks waited so. Their waits averaged 1.15–1.53 ms. A bridge 8 KiB behind does not wait at all. The rule keeps a burst whenever counting only what came after the ask would, since it counts at least as much. Measured against that rule, a 2.5 MB/s stream took 10.3 frames per 32 KiB where the other rule took 33 in one of two runs. One `ls -lR` run of the shipped rule took 40.9 against 20.3, and it also streamed slower, at 3.0 against 4.5 MB/s. The code stays, and the docs now say what it counts.
- **The keep test could overflow.** After a wait of 2^52 ticks, about 52 days at a 1 GHz timestamp, the time multiplied by the keep rate no longer fits in 64 bits, and the wrapped product could keep the burst. It now divides instead.
- **Two skip lines had no test**: clearing the skip for a new enumeration, and counting the new output in a chunk that also holds the last of the skipped bytes. Each now has one.
- **Docs and comments** were corrected, among them what the keep rule counts, how long a wait may last, when the scheduler lets a reader go, and the figures behind the test list.

The later rounds:
- **A test could lose its premise unnoticed.** An early return on cancellation in `OnWaitEnded` changes no behavior, and `ANewEnumerationCountsWhatACanceledOneLeftUnread` kept passing without the skip it is about. It now asserts that skip.
- **Docs and comments** were made exact where the earlier rounds had rewritten them.

**Lesson: judge the burst rules on the traffic they serve, not on one flood.** The first round's two exit fixes were right by the spec's words, and a single flood, which fills the minimum before nearly every deadline, almost never reached them (1–8 late waits of about 4,096 in the review's count). Measured afterwards on other traffic, a build with them took about twice the committed build's frames for 32 floods at once and three times for `ls -lR`, whichever of the second round's other changes it had. A late wait then ended the burst whenever output had slowed at all, as it does on a busy host. Keeping the burst at half the starting rate brought both back to the committed build's figures or below. A change to these rules is measured on concurrent floods, a bursty real command, and steady streams between 1 and 8 MB/s, as below.

**Tests:**
- `PtyOutputCoalescingTests`, 24 tests with a backend the test publishes into. Twelve hold the tick (`HoldTicksForTests`), ten of them serving deadlines by hand, eleven set a deadline no test reaches, and one uses the real tick. Only a delay of 50 ms or more could decide one:
  - output outside a burst is handed out as it is published, and small chunks within the window start a burst
  - in a burst, reads below the minimum do not complete the wait, the minimum comes as one chunk, and a read that finds it unread takes it at once
  - the end of a burst comes at the deadline and ends the burst, and what the deadline took does not start the next one, also when it wraps the ring, while output after it does count, and a new enumeration counts what a canceled one left unread
  - a wait that outlasts its deadline, far slower than a burst, ends the burst when the tick is late or the wait lasted days, one at three quarters of the starting rate keeps it, and one completed before its deadline keeps it when the consumer resumes late
  - the deadline completes a wait whose minimum is already 1 byte, a stale deadline during the arming is seen by the re-check, and a deadline with nothing unread lets the next output through
  - the scheduler keeps a reader whose deadline it served, and one that armed a wait while a tick dropped it
  - with the real tick, the tails of six sessions over three rounds go out within the bound, a median under 50 ms that allows a coarse timer and fails a deadline 100 times too long
  - end of output, cancellation, and disposal end a coalescing wait
  - a plain read is never held and keeps the plain reader, and a coalescing read after a plain one coalesces
  - In the first version, without the minimum in the pump two tests failed, and without the deadline the tail test timed out. From the second round on, each line that a round's new test guards was broken in turn, and each break failed that test and no other test of the class.
- `AFloodThroughTheBridgeGoesOutInLargeFrames` and `PersistentSessionSendsAFloodInLargeFrames` (1 MiB), and `StdioBridgeSendsAFloodInLargeFrames` (256 KiB, below the high watermark): at least three quarters of the flood's bytes go out in frames of 4 KiB or more. They run on Unix only, and alone. On macOS with coalescing off, all three failed in each of three runs, with 1–13% of the flood's bytes in such frames.
- The full suite passed on macOS: 666 tests, in three consecutive runs.

**Measured** on macOS, A/B with alternating builds.

`WebTerminalScale bench --sessions 16,64,256 --echo 200 --flood-bytes 33554432`. Phase 6 and the first Phase 7 version ran two rounds each, alternating. That version still had the `int` minimum, superseded before the commit. The committed build ran two more rounds alone, then two rounds each against the round-one build and against the final build, alternating. The final build includes the review's first two rounds. The third changed only the keep test's arithmetic, which gives the same result for any wait under 2^52 ticks, so these figures stand for the final code:

| | Phase 6 | Phase 7, first version | Phase 7, committed | Phase 7, final |
|---|---:|---:|---:|---:|
| Flood: frames per 32 KiB (Phase 0: 8.0) | 29.9–33.2 | 4.0 | 4.0 | 4.0 |
| Flood throughput (MB/s; reader threads: 63–66) | 54.5–55.0 | 77.0–79.1 | 73.0–76.6 | 74.8–75.0 |
| Host CPU per MiB of the flood (ms) | 57.4–67.8 | 36.6–39.8 | 37.4–42.7 | 41.1–41.2 |
| Threads at 256 sessions | 33–46 | 33 | 33–52 | 33 |
| Echo p50 at 64 / 256 sessions (ms) | 0.11 / 0.11 | 0.11–0.12 / 0.11 | 0.08–0.12 / 0.10–0.14 | 0.11–0.13 / 0.10 |
| Echo p99 at 16, 64, and 256 sessions (ms) | 0.18–0.45 | 0.23–0.46 | 0.15–0.54 | 0.17–0.36 |

- **The flood's CPU is within the spread between rounds.** Four more alternating rounds of the flood alone (`--sessions 16 --echo 50`) measured the committed build at 37.1–44.1 ms per MiB and the final build at 37.8–40.8 ms, at 71.0–75.6 against 73.8–75.6 MB/s. Over the six rounds each, the medians are 38.8 and 40.8 ms, about 5% apart, within a 10% regression threshold.
- **Echo is unchanged.** p99 over 200 samples is the third slowest sample, and it moves across each build's whole range from round to round. Three rounds each of `--sessions 64 --echo 1000`, Phase 6 against the final build, measured p50 0.09–0.10 ms against 0.10 ms, and p99 0.18–0.24 ms against 0.24–0.27 ms. Three earlier rounds measured Phase 6's p99 at 0.23–0.31 ms.
- **The threads at 256 sessions** grew to 52 in two rounds of the committed build, all of it thread-pool workers (28 against 9), as Phase 6 showed in one round (46 threads, 22 of them pool workers).
- The flood is now faster than with reader threads, whose 1 ms window joined only four reads.

Other traffic, through the WebSocket bridge over an in-memory socket, two rounds each, the committed build against the final one, alternating. A paced child writes lines of the given size at a steady rate. The rate per session is measured in the client from the first frame to the last:

| Frames per 32 KiB | Committed | Final |
|---|---:|---:|
| 32 floods of 4 MiB at once (3.0–3.3 MB/s each) | 9.7–10.1 | 8.5–8.9 |
| 8 floods of 8 MiB at once (13 MB/s each) | 4.03 | 4.06 |
| `ls -lR /usr/share` (4–5 MB/s) | 17.5–30.6 | 16.9 |
| Steady 7 MB/s, 800 B lines | 4.25 | 4.25–4.26 |
| Steady 4.5 MB/s, 500 B lines | 7.1 | 5.7 |
| Steady 2.5 MB/s, 500 B lines | 17.7–18.1 | 10.3 |
| Steady 1.4 MB/s, 100 B lines | 324 | 324 |

- **A burst's tail and an echo right after it** take the same time in both builds. A 1 KiB tail with the real tick, 60 times 50 ms apart, went out at p50 1.33 against 1.31 ms, and at most 1.50 against 1.49 ms. A 10-byte echo right after a 5 KiB tail went out at p50 1.31 against 1.33 ms.
- **A steady stream below about 2 MB/s is not joined while each send is quick.** One that never reaches the starting rate, 4 KiB within 1 ms, goes out one frame per read, as with the Phase 6 loop. One that enters a burst leaves it at its first late wait, unless the output that piles up during the bridge's sends keeps it there. The 1.4 MB/s stream took 324 frames per 32 KiB. The 2.5 MB/s stream, above the keep rate, stays in a burst once it enters: in the review's probe 2 of about 2,600 late waits ended it. It took 10.3. With reader threads, whose 1 ms window joined every read, the review measured 8–12 for the 1.4 MB/s stream.

BenchmarkDotNet, one round each, the committed Phase 7 build against Phase 6: every allocation is the same or lower, and no integration or bridge time is higher by more than 0.1%. The session and capture 32 KiB rows are 160 B lower, for example `Session_32KiB_StreamBytes` 1.60 against 1.76 KB, which is the spread between runs of Phase 6 itself (its own result above measured 1.60 KB). `Bridge_32KiB_Bytes` is 26.63 against 26.81 KB, inside that row's spread between runs (26.56–28.29 KB). `Session_Exit0_WaitForExit` stays at 1.24 KB. Input writes take 105–109 µs and 2 B, against 104–111 µs in Phase 6's own result. The final build against the committed one: every allocation is the same except `Bridge_32KiB_Bytes`, 26.80 against 26.63 KB, inside that spread. No integration time is more than 4.5% higher, most are lower, and input writes take 104–107 µs.

**NativeAOT:** the final code's `WebTerminalScale` publishes for `osx-arm64` without trimming or AOT warnings and passes `--smoke`, whose 256 KiB flood took 4.6 frames per 32 KiB.

**Done on macOS:** throughput 74–76 MB/s against 63–66 with reader threads, 4 frames per 32 KiB against Phase 0's 8, echo at Phase 6's p50 and p99.

**Open** (the first three are answered in the [Windows and Linux result](#phase-7-result-windows-and-linux)):
- **Windows and Linux** are not measured: frames, throughput, echo, host CPU, and BenchmarkDotNet against Phase 6, on those machines, and the traffic table above.
- **The frame tests on Linux.** A Linux read can return 4 KiB, so the three frame tests may pass there without the joining. Check them with coalescing off.
- **The deadline on Windows.** The tick runs on .NET timers, which on Windows fire at the system timer's resolution, about 15.6 ms. A wait in a burst there may last that long, where on macOS it lasts up to about 2 ms. That covers the end of a burst, the tail of a repaint or paste of 4 KiB or more, and an echo typed while output streams. Echo outside a burst is not affected. Measure it on Windows. If it shows, the tick can move to a high-resolution waitable timer, one per process, or a burst can start at a larger size. (It showed, on Linux too.)
- **Steady output below about 2 MB/s goes out one frame per read** while each send is quick, as with the Phase 6 loop. Reader threads joined it within 1 ms. This covers log-rate streams of many small writes. Joining it means waiting on output no rate distinguishes from interactive output. It stays as it is for now, and [Phase 8](#phase-8-steady-streams-below-the-burst-rate) decides from measurements on every platform.
- **An echo during a flood** is not tested against the bound. The tests time the end of a burst only.
- **Package versions.** `MiniPty.Terminal` now uses MiniPty's internals, as `MiniPty.Capture` already does, while each package declares MiniPty with a lower bound only, through its project reference. A newer MiniPty with changed internals would break an older `MiniPty.Terminal` at run time. Whether to pin the exact version is a release decision.
- FreeBSD stays from source, as before.

### Phase 7 result (Windows and Linux)

Measured 2026-10-26 on Windows 11 26200 (32 logical CPUs) and Linux (WSL2, kernel 6.6 with `CONFIG_HZ=250`, 8 logical CPUs), .NET 10.0.9. Other sessions shared the Windows machine, so every figure there comes from rounds alternating the builds, and CPU from medians of 8–10 rounds.

**Found: .NET timers did not keep the deadlines' millisecond on either platform.**
- **Windows:** a 1 ms `Timer` fired every 15.6 ms, the system timer's resolution, as the macOS result expected. A burst's tail waited for it: the real-tick test's tails took a median of 14.9–15.4 ms, against 1.3 ms on macOS, and through ConPTY and the WebSocket bridge the second half of a 6 KiB repaint arrived 14.8 ms after its first.
- **Linux:** a 1 ms `Timer` fired every 4 ms, which the plan had not expected. .NET timers take their due times from `CLOCK_MONOTONIC_COARSE`, which advances at the kernel's tick rate, 250 Hz here. `Thread.Sleep(1)` and a 1 ms epoll wait each took 1.05 ms. The real-tick test's tails took a median of 2.6–4.2 ms, at most 5.3 ms, and a repaint's tail up to 6.1 ms.

**Delivered:**
- **Windows ticks on a high-resolution waitable timer** (`WindowsPreciseTick`, `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, Windows 10 1803 and later, below every ConPTY). A thread of its own waits for it and runs the tick. Waiting on the thread pool instead cost 35–44 µs of CPU per tick with the runtime's own pool, whose wait thread hands each signal to a worker, against 4–9 µs on the thread: about 3% of a core while a flood keeps the tick running. `timeBeginPeriod` would have raised the timer resolution of the whole process. The thread stays 30 s after the last burst, so bursts start no thread each, and then exits.
- **Unix ticks on the I/O loop.** While the scheduler holds a reader, the loop's wait times out at the next tick, as it does for a hang-up's deadline, and the loop serves the tick after the pass's other work. It runs on the loop thread, which no longer waits for a pool worker to serve a deadline. This covers macOS, checked below, and FreeBSD, which has not run it.
- A `Timer` ticks only where neither can start. The scheduler records which tick runs (`PtyCoalesceTickSource`), so it stops the same one.

**Tests:**
- `TheTickFiresAboutEveryMillisecond` counts the ticks while a reader waits, and checks which tick runs. With `Timer` ticks it counted 14 in 204 ms on Windows and 48–49 in 200 ms on WSL2, and failed. With the ticks above, the real-tick test's tails took 1.42–1.44 ms on Windows and 1.96–2.00 ms on Linux (medians; slowest 2.07 and 2.15 ms).
- **The frame tests do decide on Linux.** With coalescing off in all three bridges, 32–53% of a flood's bytes went out in frames of 4 KiB or more, and each test failed its three quarters, in three runs each. With it on, 97–99.9%. On Windows they still skip, since ConPTY's slices make frames of about 6 KiB either way.
- The coalescing tests pass on both Windows thread pools. The full suite passed on Linux, 667 tests, in five runs of the final code. On Windows it passed in six of eight full runs of the final code. Each of the other two failed one test, `PtyResizeAfterExitWithOpenTransportDoesNotThrow`, which reads no output through a bridge. That test's premise was wrong on Windows 11 24H2 and later, not this phase's code (see Found and fixed in a test).
- `TheTickStopsOnceNoReaderWaits`: once the last wait ends, the tick stops at the next tick and fires at most once more in 200 ms, and on Linux the loop makes fewer than 100 passes in them. With the stop left out, Windows ticked 145 times in those 200 ms and the Linux loop made 194 passes, and the test failed on both. A tick that runs while no bridge waits would be a wake-up every millisecond for nothing.

**Found and fixed in a test.** `PtyResizeAfterExitWithOpenTransportDoesNotThrow` expected a resize after the child's exit to apply while no close had been requested, because "conhost stays alive until the pseudo-console is closed". MiniPty releases the console host at spawn wherever ConPTY can (the out-of-band ConPTY, and the in-box one from Windows 11 24H2), and the host then ends with the child. Of 20 resizes each, all applied 0–2 ms after the exit, and none 5, 10, 20, or 50 ms after it: `ResizePseudoConsole` fails once the host is gone, and `Resize` skips it as exit tolerance allows. A full run that kept the test's thread off a core for 5 ms failed it. With a 20 ms pause before the resize, the test failed in 5 of 5 runs. A grandchild (`start /b ping`) now keeps the host for it, and with the same pause it passed in 5 of 5. `PtySession.Resize` and [core_session.md](../specs/core_session.md) say that the host can end before the transport closes.

**Measured**, against Phase 6 and the committed Phase 7 build (which ticked on `Timer`), alternating the builds:

`WebTerminalScale bench`, two rounds each (Windows `--sessions 16,64,128`, Linux `--sessions 16,64,256,1000`; `--echo 200 --flood-bytes 33554432`):

| | Windows Phase 6 | Windows Phase 7 | Linux Phase 6 | Linux Phase 7 |
|---|---:|---:|---:|---:|
| Flood: frames per 32 KiB (Phase 0: 8.0) | 5.5–5.7 | 3.9 | 13.9–16.5 | 4.0 |
| Flood throughput (MB/s) | 22.5–24.5 | 24.7–25.5 | 108–141 | 124–133 |
| Echo p50 / p99 at 64 sessions (ms) | 0.23–0.24 / 0.37–0.38 | 0.23–0.24 / 0.40–0.42 | 0.38–0.43 / 0.53–0.56 | 0.38–0.41 / 0.52–0.60 |
| Threads at the largest step | 81–101 | 81 | 48–50 | 37–50 |

The thread counts move with the pool workers a burst of connections injects, in every build.

The flood alone, 64 MiB (`--sessions 4 --echo 20 --flood-bytes 67108864`), 10 rounds each on Windows and 8 on Linux:

| | Windows Phase 6 | Windows Phase 7 | Linux Phase 6 | Linux Phase 7, committed | Linux Phase 7 |
|---|---:|---:|---:|---:|---:|
| Frames per 32 KiB | 5.8–5.9 | 3.9 | 13.7–18.3 | 4.0 | 4.0 |
| Throughput (MB/s) | 24.6–25.5 | 25.8–26.7 | 116–141 | 107–137 | 118–137 |
| Host CPU per MiB (ms), median (range) | 13.9 (10.6–18.9) | 13.2 (10.2–17.4) | 24.2 (23.2–26.4) | 20.4 (17.7–23.4) | 19.1 (18.1–21.1) |

- **Against the committed build, Windows pays for the deadlines it now keeps.** Two runs of 10 and 8 alternating rounds measured the committed build at medians of 13.0 and 15.2 ms per MiB, the final one at 16.3 and 16.5, within the spread of single rounds (9–24 ms). In a 64 MiB flood the final build served 1,044 deadlines, and 303 bursts ended, where the coarse timer served almost none: ConPTY pauses for more than 1 ms now and then, and a deadline kept to the millisecond ends the wait there. The frame count is the same. A build that kept ticking for 32 ticks after the last reader left, so that the scheduler did not start and stop between waits, measured 16.1 ms, so that is not the cost. Against Phase 6 the final build costs no more.
- On Linux the loop's tick costs nothing measurable against the committed build's `Timer`. One committed round printed no result and did not reproduce in three reruns.

The traffic of the macOS result, through the WebSocket bridge over loopback, two rounds each. The real commands are `dir /s` of `C:\Windows\System32\DriverStore\FileRepository` (0.4 MiB) and `ls -lR /usr/share` (2.4 MiB). A repaint is one write of 6 KiB every 50 ms, and its tail is the time from its first frame to its last:

| Frames per 32 KiB | Windows Phase 6 | Windows committed | Windows final | Linux Phase 6 | Linux committed | Linux final |
|---|---:|---:|---:|---:|---:|---:|
| 32 floods of 4 MiB at once | 4.18–4.21 | 3.88–3.93 | 3.65–3.69 | 8.17–8.28 | 4.18–4.20 | 4.74–4.80 |
| 8 floods of 8 MiB at once | 4.58 | 4.00 | 3.87–4.17 | 8.97–9.01 | 4.01 | 4.01 |
| The real command | 638 | 638 | 638–639 | 97–122 | 5.7–6.0 | 7.8–8.2 |
| Steady 7 MB/s, 800 B lines | 39.5–39.7 | 4.03–4.04 | 4.10–4.83 | 41.3–42.6 | 4.08–4.11 | 4.08–4.12 |
| Steady 4.5 MB/s, 500 B lines | 61.9 | 4.20 | 4.98–5.03 | 64.4–66.9 | 4.68–4.76 | 5.63–5.66 |
| Steady 2.5 MB/s, 500 B lines | 62.2 | 4.62–4.63 | 7.25–7.28 | 66.0–67.0 | 5.19–5.32 | 7.92–8.05 |
| Steady 1.4 MB/s, 100 B lines | 326 | 310–324 | 325 | 318 | 306–320 | 319–321 |
| A repaint's tail, p50 / max (ms) | 0.04 / 0.09 | 14.8 / 16.2 | 1.05–1.41 / 1.61 | 0.00 / 0.06 | 0.00 / 6.12 | 0.00 / 1.11 |

- **Bounding the wait costs some joining of steady streams.** The committed build's coarse timers let a wait last 15.6 or 4 ms, and streams between 2 and 5 MB/s, and `ls -lR`, went out in fewer frames, while every burst's tail waited as long. With the wait bounded at about 2 ms, they take one to three more frames per 32 KiB, still eight to nine times fewer than in Phase 6, and a repaint's tail waits 1–1.4 ms on Windows instead of 15. The second Windows round of the final build ran under load from other sessions: the 7 MB/s stream came out at 4.6 MB/s, in 4.83 frames.
- `dir /s` writes about 0.5 MB/s, one line at a time, so ConPTY delivers it below the rate that starts a burst, and it goes out one frame per line in every build, as the 1.4 MB/s stream does. Both are [Phase 8](#phase-8-steady-streams-below-the-burst-rate)'s.
- On Linux a 6 KiB repaint took 1.4 frames in every build, and most repaints went out as one frame, so only some tails waited at all.

BenchmarkDotNet, one round each, Phase 6 against the final build: every allocation is the same, within 20 B. On Windows `Session_Exit0_WaitForExit` stays at 1.28 KB, `Session_32KiB_Bytes` at 38.63–38.64 KB, `Capture_32KiB_Text` at 114.47–114.49 KB, and `Bridge_32KiB_Bytes` is 26.55 against 26.61 KB, inside that row's spread between runs; input writes allocate nothing. On Linux they are 1.23, 33.93, and 100.8 KB in both builds, `Bridge_32KiB_Bytes` 26.29 against 26.32 KB, and input writes 2 B. No time moved beyond the spread between runs. The tick's thread on Windows is created once and kept through the iterations, so it costs a benchmark nothing per operation.

**NativeAOT:** `WebTerminalScale` publishes without warnings for `win-x64` and `linux-x64`, links the native code statically on Linux, and passes `--smoke` on both, whose 256 KiB flood took 3.0 and 4.1 frames per 32 KiB.

**Done on Windows and Linux:** a flood takes 3.9–4.0 frames per 32 KiB, half Phase 0's, at Phase 6's throughput or more and no more host CPU; echo p50 is Phase 6's, and p99 stays within the spread between rounds (0.37–0.67 ms across all builds). A burst's tail now waits about 1–2 ms on every platform, where Phase 6 sent it at once.

**Checked on macOS** (2026-10-26, on the machine of the macOS result): the final code against the build before it (`244141f`, whose tick ran on `Timer`), alternating, two rounds each.
- The full suite passed, 668 tests, in three runs, `TheTickFiresAboutEveryMillisecond` and `TheTickStopsOnceNoReaderWaits` among them.
- The tick runs on the loop (`PtyCoalesceTickSource.UnixLoop`). Over five rounds it fired 157–161 times in 200–202 ms, every 1.26–1.29 ms on average, where the review had measured the `Timer` at 1.27–1.30 ms apart.

| | Before | After |
|---|---:|---:|
| `WebTerminalScale bench` flood: frames per 32 KiB, MB/s, host CPU per MiB (ms) | 4.0, 77.6–86.1, 31.5–40.0 | 4.0, 76.5–79.5, 33.8–38.0 |
| 32 floods of 4 MiB at once, frames per 32 KiB | 9.37–9.50 | 9.19–9.37 |
| 8 floods of 8 MiB at once | 4.05–4.06 | 4.06–4.08 |
| `ls -lR /usr/share` | 17.2–54.8 | 17.9–19.1 |
| Steady 7 MB/s, 800 B lines | 4.25–4.28 | 4.53–4.56 |
| Steady 4.5 MB/s, 500 B lines | 5.71–5.72 | 5.72 |
| Steady 2.5 MB/s, 500 B lines | 10.3–17.0 | 8.14 |
| Steady 1.4 MB/s, 100 B lines | 319–323 | 319–322 |
| A burst's 1 KiB tail with the real tick, p50 / max (ms) | 1.28 / 1.86 | 1.28 / 2.78 |
| An echo right after a 5 KiB tail, p50 (ms) | 1.31 | 1.27 |

- The `ls -lR` round of 54.8 frames streamed at 2.4 MB/s, against 4.8 MB/s in the other three.
- The 7 MB/s stream took about 7% more frames in both rounds.
- **Echo is unchanged.** Three rounds each of `--sessions 64 --echo 1000` measured p50 0.09–0.10 ms in both builds, and p99 0.17–0.27 ms before and 0.19–0.25 ms after. In the scale bench, p50 at 64 and 256 sessions was 0.11–0.12 and 0.13–0.14 ms before, and 0.11–0.13 and 0.12–0.13 ms after.
- BenchmarkDotNet, one round, against the macOS result's final build: every allocation is the same except `Bridge_32KiB_Bytes`, 26.87 against 26.80 KB, inside that row's spread between runs. No time is more than 3.5% higher, the short rows are 1–3% lower, and input writes take 100–102 µs.
- **NativeAOT:** `WebTerminalScale` publishes for `osx-arm64` without trimming or AOT warnings and passes `--smoke`, whose 256 KiB flood took 4.3 frames per 32 KiB.

**Open:**
- FreeBSD runs the loop's tick from source, unverified, as the rest of its loop.
- **An echo during a flood** is still not tested against the bound.
- Steady output below the burst rate, decided in [Phase 8](#phase-8-result), and the package versions, as in the macOS result.

### Phase 8: Steady streams below the burst rate

**Goal:** decide, from measurements on Windows, Linux, and macOS, whether the bridges join steady output that never reaches the rate that starts a burst, and record the decision in [terminal.md](../specs/terminal.md). Until then such output goes out one frame per read while the bridge keeps up, as it did before Phase 7.

**Why it is open:** on macOS, 100-byte lines at 1.4 MB/s took 324 frames per 32 KiB, where reader threads took 8–12 ([Phase 7 result](#phase-7-result-macos)). The reader threads' window joined every read, but it also held each echo back: echo became about 1.4 ms faster on macOS once that window and the reader thread's own wait were gone ([Phase 6 result](#phase-6-result-macos)). Before the next write comes, nothing tells such a stream from interactive output that has ended, so joining it means letting each of its chunks wait. A wait lasts up to about 2 ms on every platform, now that Windows and Unix tick at about 1 ms ([Phase 7 result (Windows and Linux)](#phase-7-result-windows-and-linux)).

**Changes**
- Measure the cost on each platform, with the reader threads as the baseline through the rollback switch: frames per 32 KiB and the host's CPU per MiB for steady streams of small writes from about 0.4 to 4 MB/s, and for real commands that write many short lines.
- Try at least one rule that starts a burst below 4 KiB per millisecond, such as a number of reads within the window. The review tried starting a burst on four chunks within 1 ms, and ending it only at a deadline with nothing unread, on the code before its second round. The 1.4 MB/s stream then took 12.6 frames per 32 KiB, a flood stayed at 4.0, and each chunk of such a stream waited about 1.3 ms on macOS.
- Measure what each tried rule costs interactive output on each platform: echo p50 and p99, the tail of a repaint, and an echo right after other output.
- Choose: keep the current rule and record its cost, lower the start rule for every bridge, or let the host choose. A host choice is public API, so it is added to the Public API Decisions above.
- The Windows deadline that this decision waited for is measured and fixed: Windows waits as long as the other platforms.

**Tests** (if the rule changes)
- The new start rule has a test on each side of its threshold, using the window and deadline overrides of `PtyOutputCoalescingTests`.
- An echo after idle, and one right after a burst's tail, still goes out at once.

**Done when:** terminal.md states what the bridges do with steady output below the starting rate, and why, with figures from Windows, Linux, and macOS. If the rule changed, echo stays at Phase 7's p50 and p99, and no traffic in Phase 7's table takes more frames.

### Phase 8 result

Decided 2026-10-26 from Windows and Linux, on the machines of the [Phase 7 result](#phase-7-result-windows-and-linux), and checked on macOS the same day (below).

**Delivered:**
- **A burst also starts at four chunks within 1 ms** (`PtyOutputCoalescing.BurstChunks`), whatever their size. A stream of short lines that the terminal hands over one read at a time reaches that, and a keystroke echo does not. Writes the terminal hands over at once are one chunk.
- **A burst lasts while each deadline still finds output unread.** The tick records whether the deadline it serves found output, before the wait completes. Such a wait keeps the burst, so a stream of short lines stays in its burst, each chunk taken at a deadline. A wait that output completes only after a deadline that found nothing ends the burst, and that output, an echo typed after the stream for example, goes out as it arrives. The rate that keeps a burst (2 KiB per millisecond) still decides for a wait whose deadline a late tick did not serve.
- The start rule alone would not have been enough. A stream at 1 MB/s fills a deadline of 1–2 ms with 1–2 KiB, below that rate, so it would leave its burst at every deadline and start it again four chunks later.
- **`samples/SingleFiles/BridgeTraffic.cs`**, the traffic harness of Phases 7 and 8: floods, a real command, steady streams of 100- to 800-byte lines, a key's echo after idle and right after other output, and a repaint's tail, through `PtyWebSocketBridge` over loopback. `--reader-threads` selects the reader-thread drivers through the rollback switch.
- The coalescing reader's two new fields are a byte and a flag. They pack with its other state once its burst and wait flags share one byte, so a bridge's session allocates what it did (see Checked on macOS).

**Tests:**
- `PtyOutputCoalescingTests`, 29 tests, among them:
  - four chunks within the window start a burst and three do not;
  - a stream stays in its burst while each deadline finds output, at 200 bytes per deadline, far below the rate that keeps a burst;
  - the end of a burst goes out at its deadline, and the burst ends at the next deadline that finds nothing;
  - the rate that keeps a burst still decides for a deadline no tick served;
  - a coalescing read allocates no more than its reader and enumerable (see Checked on macOS).
- The tests of what a late wait leaves unread now take that wait from an empty deadline or an unserved one, because a deadline that finds output keeps the burst.
- Each new line broke its tests and no others: without the deadline's finding, two tests failed; without the chunk count, two; with the keep rate raised to the start rate, one.
- The full suite passed in two runs on Windows and two on Linux, 670 tests each, and on macOS in three runs of 670 and, with the allocation test, three of 671.

**Measured**, with `BridgeTraffic` alternating the builds: the Phase 7 rule in six rounds on Windows and four on Linux, the Phase 8 rule in four each, and the reader threads in two each. Frames per 32 KiB:

| | Windows, Phase 7 | Windows, reader threads | Windows, Phase 8 | Linux, Phase 7 | Linux, reader threads | Linux, Phase 8 |
|---|---:|---:|---:|---:|---:|---:|
| 100 B lines at 2 MB/s | 322.6–325.6 | 8.30–8.31 | 9.2–9.7 | 293–305 | 8.10 | 8.7–9.5 |
| 100 B lines at 1.4 MB/s | 326.2–327.0 | 12.07 | 13.2–13.6 | 318–321 | 11.99 | 14.2–14.3 |
| 100 B lines at 1 MB/s | 326.9–328.0 | 16.03–16.07 | 18.0–18.5 | 324.1–324.6 | 15.98 | 20.1–20.4 |
| 100 B lines at 0.7 MB/s | 328.0–329.1 | 22.02 | 26.6–27.3 | 323.6–324.1 | 21.94 | 28.7–29.0 |
| 100 B lines at 0.4 MB/s | 332.9–333.5 | 34.00–34.03 | 45.9–47.4 | 322.8–325.5 | 33.97–34.16 | 48.0–48.6 |
| `dir /s` (0.4 MiB) / `ls -lR /usr/share` (2.4 MiB) | 637–640 | 24.7–25.3 | 30.2–34.7 | 8.3–58 | 5.6–5.7 | 6.1–6.3 |

- **CPU.** This process's CPU per MiB covers both ends of the loopback socket. For the 100 B lines it fell from 1.6–2.5 s to 0.48–0.62 s at 1–2 MB/s on Windows, and from 1.5–3.9 s to 0.25–0.96 s on Linux, a third to a sixth.
- **Unchanged.** Floods and the streams of 500- and 800-byte lines at 2.5–7 MB/s, the rest of Phase 7's table, took the same frames within the spread between rounds, at the same rate and CPU.
- The reader threads took somewhat fewer frames, since their windows joined every read, at 0.76–0.80 ms of echo after idle on Windows against 0.24–0.29, and 1.69 ms on Linux against 0.59–0.65.

What it costs interactive output, p50 (max), in milliseconds. The key goes out 0.5 ms after the head of each burst of output, except after idle:

| Echo | Linux, Phase 7 | Linux, Phase 8 |
|---|---:|---:|
| After idle | 0.59–0.65 (0.73–0.96) | 0.64–0.65 (0.85–1.13) |
| After a 5 KiB tail | 0.48–0.53 (1.57–1.67) | 0.49–0.50 (1.56–1.71) |
| After four writes of 40 B back to back | 0.50–0.53 (0.62–0.92) | 0.50–0.53 (0.62–0.68) |
| After six writes of 40 B, 150 µs apart | 0.38–0.45 (0.56–1.85) | 1.52–1.54 (1.68–2.22) |

- **The cost is the last row.** Writes read one by one start a burst, and the echo that comes right after them waits for the deadline, about 1.1 ms more at p50 and bounded by the deadline. Writes the terminal joins on its own, the third row, do not start one. A 6 KiB repaint's tail is unchanged (Linux p90 0.04–0.12 ms, max 1.06–1.15 ms in both).
- **On Windows the rule costs echo nothing measurable.** Every row was the same in both builds. An echo right after other output took 7.6–15 ms in every build, the reader threads included, which is ConPTY's own and not the bridge's. After idle it took 0.24–0.29 ms.

BenchmarkDotNet, one round each, the Phase 7 build against the Phase 8 build. On Windows `Session_Exit0_WaitForExit` stays at 1.28 KB, `Session_32KiB_Bytes` is 38.64 against 38.34 KB, and `Bridge_32KiB_Bytes` 26.48 against 26.21 KB. On Linux they are 1.23 and 33.93 KB in both, and `Bridge_32KiB_Bytes` 26.30 against 26.44 KB. These are inside each row's spread between runs, and no time moved beyond it.

**NativeAOT:** `WebTerminalScale` publishes without warnings for `win-x64` and `linux-x64`, links statically on Linux, and passes `--smoke` on both (3.2 and 4.1 frames per 32 KiB for its 256 KiB flood).

**Checked on macOS** (2026-10-26, on the machine of the Phase 7 macOS result), with `BridgeTraffic` alternating the builds: the Phase 7 rule (`2c7ec93`, with the harness copied in) in three rounds, the Phase 8 rule in three, and the reader threads in two. These measurements, BenchmarkDotNet and NativeAOT included, ran before the flags below were joined, which changes no behavior. Frames per 32 KiB:

| | macOS, Phase 7 | macOS, reader threads | macOS, Phase 8 |
|---|---:|---:|---:|
| 100 B lines at 2 MB/s | 319.5–323.8 | 8.11 | 10.50–10.65 |
| 100 B lines at 1.4 MB/s | 321.4–323.4 | 11.99–12.00 | 14.80–15.04 |
| 100 B lines at 1 MB/s | 320.4–324.3 | 15.89–15.98 | 19.63–20.30 |
| 100 B lines at 0.7 MB/s | 324.8–325.1 | 21.94 | 27.26–28.46 |
| 100 B lines at 0.4 MB/s | 322.9–323.3 | 34.68–35.09 | 50.90–51.73 |
| `ls -lR /usr/share` (1.3 MiB) | 21.5–60.9 | 5.91–5.98 | 7.53–7.86 |

- **CPU.** For the 100 B lines at 1–2 MB/s it fell from 1.7–2.2 s per MiB to 0.14–0.24 s, against 0.12–0.21 s with the reader threads. At 0.4 MB/s it fell from 2.5–2.6 s to 0.51–0.52 s, against 0.37–0.39 s.
- **Unchanged.** Floods and the streams of 500- and 800-byte lines took the same frames within the spread between rounds: 32 floods 4.91–4.95 against 4.62–5.51, 8 floods 4.02–4.03 against 4.01–4.03, and the steady streams at 2.5–7 MB/s 4.55–7.82 against 4.54–7.80.

| Echo, p50 (max), ms | macOS, Phase 7 | macOS, reader threads | macOS, Phase 8 |
|---|---:|---:|---:|
| After idle | 0.37–0.51 (1.09–20.69) | 1.73–1.77 (3.44–3.99) | 0.43–0.48 (1.27–4.89) |
| After a 5 KiB tail | 1.39–1.50 (2.37–4.49) | 1.53–1.55 (2.29–2.67) | 0.41–1.38 (1.73–4.55) |
| After four writes of 40 B back to back | 0.21–0.23 (0.78–2.12) | 1.49–1.52 (4.42–10.66) | 0.21–0.23 (1.05–1.20) |
| After six writes of 40 B, 150 µs apart | 0.19–0.21 (1.58–6.41) | 1.52 (1.82–1.94) | 1.41–1.46 (1.95–2.97) |

- **The cost is the last row, as on Linux:** about 1.2 ms more at p50. Writes back to back cost echo nothing on macOS either.
- **A 6 KiB repaint** took 3.44–3.52 frames against 3.10–3.25, and its tail p50 stayed at 1.28–1.29 ms against 1.17–1.30 ms.
- **Found and fixed: the two new fields grew every bridge session by 8 bytes.** The reader's own fields went from 47 to 49 bytes, past the 48 bytes they fit in, and its allocation with its enumerable from 208 to 216 B. The burst and wait flags, which only the consumer writes, now share one byte, and the probe measures 208 B again. `ACoalescingReadAllocatesNoMoreThanItsReaderAndEnumerable` keeps it there in a 64-bit process, and failed with one more `bool`. `Bridge_32KiB_Bytes` does not resolve 8 bytes: it measured 26.58 against 26.87 KB with them.
- BenchmarkDotNet, one round, against the macOS run of `2c7ec93`: every allocation is the same, `Bridge_32KiB_Bytes` within its spread between runs, and no time is more than 1.6% higher except `WriteInputBytes`, 106.2 against 99.9 µs.
- **NativeAOT:** `WebTerminalScale` publishes for `osx-arm64` without trimming or AOT warnings and passes `--smoke`, whose 256 KiB flood took 4.3 frames per 32 KiB.

**Decision (2026-10-26): the bridges join streams of short lines, the same rule for every bridge, with no host option.**
- Sent one frame per read, such a stream cost 20–40 times the reader threads' frames, and a real command such as `dir /s` 25 times. Both cost host CPU at both ends, and the browser one event per frame.
- The price is a wait on output right after a run of small writes read one by one, bounded by the deadline at about 2 ms.
- A host option would be public API for a choice that bound makes small.
- Streams slower than four reads a millisecond (100-byte lines below about 0.4 MB/s) still go out one frame per read.

**Open:**
- **An echo during a flood** is still not tested against the bound, as in Phase 7.

### Phase 9: Retire the reader thread

**Goal:** one engine per platform, and specifications that describe it.

**Changes**
- Remove `PtyReaderThread`, the reader-thread driver, the per-reader wake descriptors (`minipty_wake_*`, `minipty_pty_wait`), and their tests. The alternative is to keep the `AppContext` switch for one release and remove it in the next, a release decision recorded here.
- Update the specifications:
  - [core_session.md](../specs/core_session.md): Backpressure, Caller Context, the GUI hosts "Many tabs" row, and lessons learned.
  - [pty_crossplatform.md](../references/pty_crossplatform.md): Output pump, Post-exit drain, Disposal, Child exit observation, and Concurrency.
  - [terminal.md](../specs/terminal.md).
  - [capture.md](../specs/capture.md), if read boundaries changed its timestamps.
- Update the README if it mentions reader threads, and [next_todo.md](../next_todo.md).
- Fold the lessons into the specifications and retire this plan, as the repository does with finished plans.

**Done when:** Acceptance holds on every platform, a sample publishes and runs under NativeAOT on Windows and Linux, and nothing outside history refers to reader threads.

## Acceptance

- The process thread count does not grow with 64 or 1,000 idle sessions on Windows and Linux, nor with 64 on macOS. Windows exit watchers still add one thread per 63 sessions.
- A session with no unread output holds no output buffer.
- Reads allocate nothing. Allocation per session start, chunk, and exit is not above the Phase 0 baseline, except a few bytes at a session start for having no thread per session: Windows' 40 B completion-port binding, accepted in the [Phase 5 result](#phase-5-result), and the Unix loop's 10–20 B of state, the same price, recorded under the same exception in the [Phase 6 result](#phase-6-result-linux) and paid on macOS too.
- Keystroke echo latency through the WebSocket bridge is not worse at p50 or p99, and throughput is not lower.
- Frames per 32 KiB through the bridge are recorded on every platform. More than twice the Phase 0 baseline needs a decision before release: Linux's twice was accepted, and the bridges' coalescing (Phase 7) brought a flood to half the baseline on every platform, from four times it on macOS and twice on Linux. Streams of short lines below the burst rate are joined as well ([Phase 8](#phase-8-result)), so they take near the reader threads' frames instead of 40 times as many.
- The full test suite passes on Windows, Linux, and macOS, and a sample publishes and runs under NativeAOT on Windows and Linux.

## Risks and Open Questions

- **macOS readiness on a pty master:** settled by Phase 4, `kqueue` holds up, and the Phase 6 loop on it passes the full suite on macOS 27. FreeBSD's choice, and the loop there, rest on source alone until the probe and the suite run there.
- **macOS flood frames and throughput** through the WebSocket bridge: four times the frames of Phase 0 and about 12% slower with no window, because a macOS read returns at most 1 KiB ([Phase 6 result (macOS)](#phase-6-result-macos)). Decided: the bridges coalesce bursts (Phase 7). On macOS a flood now takes 4 frames per 32 KiB and runs faster than with reader threads ([Phase 7 result](#phase-7-result-macos)), and on Windows and Linux 3.9–4.0, once the deadlines ticked at 1 ms there too ([Windows and Linux](#phase-7-result-windows-and-linux)).
- **macOS discards output left in the tty when the child's exit is the slave's last close**, 0.6 s after the exit: settled after Phase 4, since the session holds a slave descriptor on macOS ([Phase 4 result](#phase-4-result)). The Unix engine keeps closing that descriptor with the master. If FreeBSD runs need the same guarantee, they need another remedy, because there the descriptor would make the exit discard at once.
- **Windows chunk count** with a consumer faster than ConPTY's slices; see Coalescing.
- **Windows thread-pool dependence** of read completions; see Isolation Rules.
- **Capture timestamps** stop being one per transport read if read boundaries change; Capture's contract may need rewording.
- **One loop thread is a single point of failure for every Unix session in a load.** Fault containment per session and loop-level tests (a session that errors, a descriptor that stays ready) are part of Phase 6.
- **The free list's retained memory** after a peak, and whether to trim it.

## Follow-ups (Not In This Plan)

- **Windows exit observation in O(1) threads** (for example thread-pool waits), once 63 sessions per thread matters.
- **Environment baseline for multi-tenant hosts.** `Environment` is an overlay on the host's environment, so a web server's secrets (connection strings, cloud credentials) reach every shell unless each key is removed. A minimal-base option needs its own design, because Windows children fail to start without variables such as `SystemRoot`.
- **Out-of-process PTY host guidance.** A web host that serves untrusted users runs MiniPty in a separate, less privileged process or container and talks to it over `PtyStdioBridge`, as VS Code runs node-pty in its pty host. This gives crash and privilege isolation and lets the web tier restart without ending shells. It is documentation and a sample, not a library change.
- **Metrics** through `System.Diagnostics.Metrics`.

## Estimate

Large overall; per-phase sizes are in [Implementation Phases](#implementation-phases). Phase 6 is the largest. Phase 4 chose kqueue for macOS, so macOS and FreeBSD share one loop and differ from Linux only in the wait call.
