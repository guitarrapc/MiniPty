Milestones 1–5 and the editor terminal backend (use case 4) are implemented. See [specs/core_session.md](specs/core_session.md), [specs/console.md](specs/console.md), [specs/terminal.md](specs/terminal.md), [specs/lifecycle.md](specs/lifecycle.md).

GUI host readiness ([plans/plan_gui_host_readiness.md](plans/plan_gui_host_readiness.md)) Phases A, B, and C are implemented: caller-context safety, thread-pool independence, exit-tolerant resize, platform size limits, GUI host guidance with a WinUI sample, bounded end of output on Windows, explicit Unix termios with `IUTF8`, and hang-up before kill on Unix dispose.

Phase D is implemented as well: G7 (opt-in out-of-band ConPTY supplied by the application, `PtyStartInfo.ConPty` plus `ConPtyLibrary`, with `Clear()`) and G8 (opt-in process-tree cleanup, `PtyStartInfo.KillProcessTree`).

**Next:** No follow-up implementation plan is currently selected. Candidates recorded in the plan's open questions: in-box `ReleasePseudoConsole` on Windows builds that export it, for the same pipe-break end of output as the out-of-band path. G6 (thread-free async I/O) is deferred until an embedder needs dozens of concurrent sessions. Remaining deferred and won't-do node-pty parity items are recorded as non-goals in [specs/terminal.md](specs/terminal.md).
