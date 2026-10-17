Milestones 1–5 and the editor terminal backend (use case 4) are implemented. See [specs/core_session.md](specs/core_session.md), [specs/console.md](specs/console.md), [specs/terminal.md](specs/terminal.md), [specs/lifecycle.md](specs/lifecycle.md).

GUI host readiness ([plans/plan_gui_host_readiness.md](plans/plan_gui_host_readiness.md)) Phases A, B, and C are implemented: caller-context safety, thread-pool independence, exit-tolerant resize, platform size limits, GUI host guidance with a WinUI sample, bounded end of output on Windows, explicit Unix termios with `IUTF8`, and hang-up before kill on Unix dispose.

**Next:** Phase D of that plan (G7 opt-in out-of-band ConPTY, G8 opt-in process-tree cleanup). G6 (thread-free async I/O) is deferred until an embedder needs dozens of concurrent sessions. Remaining deferred and won't-do terminal parity items are recorded in [plans/plan_terminal_parity.md](plans/plan_terminal_parity.md).
