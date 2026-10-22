# Thread-Free I/O Plan (Web Hosting Scale)

Goal: let a web host (ASP.NET Core, `HttpListener`, or any WebSocket server) keep hundreds to thousands of PTY sessions open while paying only for the ones producing output. No session may hold a thread, an idle session should hold no output buffer, and reading must stay allocation-free. Latency, throughput, backpressure, and every existing lifecycle guarantee must be at least as good as today. This replaces the deferred "Thread-free async I/O" item (G6 of the retired GUI host readiness plan) with a concrete design.

**Status:** Proposed (2026-10-23). No phase has started. See [Implementation Phases](#implementation-phases).

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

Not probed: a real ConPTY on an overlapped output pipe inside MiniPty (the input pipe and Porta.Pty already run ConPTY on overlapped named pipes), macOS and FreeBSD readiness on a pty master, and any effect on latency or chunk counts.

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
| `PtyTerminal`, bridges | Unchanged. The bridges keep sending one binary frame per chunk | Larger chunks under load reduce frame count without bridge changes |
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
5. **macOS.** Use `kqueue` if level-triggered read, write, and end-of-file events prove reliable on a pty master; tmux and creack/pty avoid kqueue for ptys on macOS. Otherwise the loop waits with `select`, which the reader already uses there (`_DARWIN_UNLIMITED_SELECT`). The probe comes first (Phase 4).
6. **The per-reader wake descriptors go away**; only the loop's own wake remains. The master stays reference-counted, so a close never races a read, a resize, or a write that is in progress.

### Buffer

1. **A single-producer, single-consumer byte ring** (head and tail only) replaces the four fixed slots. The consumer borrows the contiguous span from head to tail (to the end of the array at the wrap), so successive small reads join into one chunk without any time window.
2. **Rented on demand.** A session takes a ring when output arrives and gives it back when it is empty and the consumer has released its chunk. An idle session holds none. (On Windows a read posted into the ring holds it only during a burst.) This takes effect with the platform engines (Phases 5 and 6), because today's blocking Windows read needs its buffer while it waits.
3. **Not `ArrayPool<byte>.Shared`.** With completions and loop iterations on different threads than releases, a thread-caching pool would strand arrays. Rings come from a process-wide free list of pinned buffers (pinned so a posted overlapped read needs no extra pin), retained up to the peak number of sessions with unread output. Whether and when to trim the free list is decided in Phase 1.

### Coalescing

Today the reader thread holds a slot for up to 2 ms of continuation reads. In the new engine a consumer still busy with its previous chunk naturally takes everything that arrived meanwhile, so batching comes from the consumer's pace instead of a timer.

- **Windows drops the time window.** Sub-millisecond timers are not available on the completion port, and the window costs 10–40% of a core for trickling output today. Reading what the pipe holds plus consumer-side batching replaces it.
- **Unix keeps the 1 ms window and 2 ms budget at first**, as loop deadlines, so its chunking does not change in the same phase as its engine. Phase 6 compares it with consumer-side batching and keeps one policy for both platforms if the numbers allow.
- **A consumer that is faster than the child sees more, smaller chunks than today** (ConPTY delivers bulk output in slices 20–45 µs apart). This is measured, with the WebSocket bridge as the reference consumer. If frame counts matter, the remedy is a consumer-side policy in the bridge, not a timer in the core.

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
| 7 | Reader thread retired, specifications updated, plan retired | 5, 6 | All | Small |

Phases 5 and 6 can run in parallel on separate branches. They touch different backends and share only the contract from Phase 2. Phase 4 can run at any time before Phase 6 reaches macOS.

Phases 1–3 keep today's reader thread and change one thing each, so each change is measured on its own, as G1 did. From Phase 2 on, an `AppContext` switch selects the reader-thread driver. That gives Phases 5 and 6 a rollback in the field, and Phase 7 decides whether it ships.

### Phase 0: Baseline and harness

**Goal:** the numbers every later phase is judged against, and failing tests that define "done".

**Changes**
- `MiniPty.Benchmarks` references `MiniPty.Terminal`, a first-party project and not a new dependency, and gains a WebSocket benchmark:
  - Server and client run in one process over loopback TCP, through `WebSocket.CreateFromStream`.
  - The server side is `PtyWebSocketBridge`. The client acknowledges output the way the xterm.js guide describes.
  - It measures keystroke echo latency (p50/p99), throughput, and frames per 32 KiB, at 1, 64, and 1,000 sessions.
- A census helper that reports the process thread count and working set for N idle sessions, and the same at 16 sessions, so that thread growth can be compared across session counts rather than against a fixed number.

**Tests (added skipped)**
- `IdleSessionsHoldNoThread`: going from 16 to 64 idle sessions adds no threads beyond the runtime's own. On Windows the exit watcher's thread per 63 sessions is excluded. Skipped until Phase 5 on Windows and Phase 6 on Unix.

**Measure:** record the existing benchmarks (`PtyIntegrationBenchmarks`, `PtyOutputBenchmarks`, `PtyInputBenchmarks`, `PtyConPtyBenchmarks`) and the new ones on Windows, on Linux, and on macOS. Record the CPU a trickling child costs on Windows, which the specification puts at 10–40% of a core today.

**Done when:** the baseline is recorded, and the census test, with its skip removed locally, fails for the expected reason.

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
- The `AppContext` switch for the reader-thread driver.

**Tests:** the whole suite, unchanged in what it asserts.

**Done when:** behavior and benchmark numbers match Phase 1 within noise.

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

### Phase 4: Readiness probe for pty masters

**Goal:** choose the Unix loop's wait mechanism per operating system before writing it.

**Work:** a standalone probe, like the F1–F4 probes, on macOS and, where a machine is available, FreeBSD. It checks:
- `kqueue` level-triggered `EVFILT_READ` and `EVFILT_WRITE` on a pty master;
- end of file after the slave closes, and after the session leader exits (macOS revokes the tty);
- the 1 KiB macOS master buffer under a flood;
- 1,000 masters in one `kqueue`;
- the same with `select`.

**Done when:** the choice per operating system is recorded in "Phase 4 result". Without a FreeBSD machine, FreeBSD follows from source and stays flagged as unverified, as it is today.

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

### Phase 6: Unix engine

**Goal:** one thread per load for all Unix sessions.

**Changes**
- **Native watcher:** masters are registered, modified, and removed with read and write interest and a session token. The token is an index plus a generation, so a stale event cannot reach a reused slot. The wait returns master events alongside exits. On macOS and FreeBSD it uses what Phase 4 chose.
- **The watcher loop becomes the I/O loop:**
  - non-blocking reads into rings taken on demand, bounded per iteration;
  - interest that follows ring and input state;
  - deregistration at end of output;
  - coalescing deadlines;
  - queued input written when the master is writable.
- Disposal's discard path merges with normal reading.
- **Delivery order:** Linux first, then macOS and FreeBSD, in separate pull requests. The reader-thread driver and its wake descriptors stay until Phase 7.

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

### Phase 7: Retire the reader thread

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
- Reads allocate nothing. Allocation per session start, chunk, and exit is not above the Phase 0 baseline.
- Keystroke echo latency through the WebSocket bridge is not worse at p50 or p99, and throughput is not lower.
- Frames per 32 KiB through the bridge are recorded on every platform. More than twice the Phase 0 baseline needs a decision before release.
- The full test suite passes on Windows, Linux, and macOS, and a sample publishes and runs under NativeAOT on Windows and Linux.

## Risks and Open Questions

- **macOS readiness on a pty master** (Phase 4). If neither `kqueue` nor `select` holds up in one loop, macOS keeps reader threads; it is not a primary web hosting platform.
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

Large overall; per-phase sizes are in [Implementation Phases](#implementation-phases). Phase 6 is the largest, and the Phase 4 outcome for macOS decides how large.
