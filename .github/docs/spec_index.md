# Specification Index

MiniPty behavior is documented under `.github/docs/`. Specs cover **what** and **why**; OS-level implementation notes live in [references/](references/).

## Specs at a Glance

| Document | Covers |
|---|---|
| [spec.md](spec.md) | Library scope, packages, public API contracts, cancellation, failure behavior, verification |
| [plans/plan_minipty_next.md](plans/plan_minipty_next.md) | Planning notes for persistent PTY sessions, package boundaries, node-pty comparison, and staged API direction |
| [references/pty_crossplatform.md](references/pty_crossplatform.md) | ConPTY / `openpty` design, EOF staging, interop constraints (implementers) |

Status: **Implemented** specs cover MiniPty 0.3.x / MiniPty.Capture 0.3.x. Planning documents describe proposed future work and are not API contracts.

## Where to Look

| I want to… | Read |
|---|---|
| Understand what MiniPty provides vs MiniPty.Capture | [spec.md](spec.md) → Scope |
| Use streams, wait, or one-shot completion | [spec.md](spec.md) → Core API |
| Observe PTY output with per-read timestamps | [spec.md](spec.md) → Capture API |
| Turn decoded PTY text into host-readable output | [spec.md](spec.md) → Display text (`PtyOutput`) |
| Know cancel vs kill semantics | [spec.md](spec.md) → Cancellation |
| Debug ConPTY hangs, EOF, or fork safety | [references/pty_crossplatform.md](references/pty_crossplatform.md) |
| See how scenetake uses these packages | [scenetake spec_pty](https://github.com/guitarrapc/scenetake/blob/main/.github/docs/spec_pty.md) |

## How Documents Relate

```
spec.md                    ← public API contract (start here)
    └── references/pty_crossplatform.md   OS backends, EOF, interop
```
