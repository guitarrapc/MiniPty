# MiniPty Design Brush-up Plan

This document captures a small, data-oriented refinement plan for MiniPty that learns from the separation of concerns seen in vs-pty.net without introducing a heavy object model or an extensible inheritance hierarchy.

This is a working plan, not an API contract. The goal is to improve internal structure while keeping MiniPty small, predictable, NativeAOT-friendly, and allocation-conscious.

## Goals

- Improve the separation between:
  - launch options (what to start),
  - launch execution policy (how to start it on this platform), and
  - runtime session behavior (how the live PTY session is managed).
- Keep the design data-oriented and avoid over-engineering with public interfaces, inheritance hierarchies, or plugin-style abstractions.
- Preserve the current public API shape as much as possible.
- Keep hot paths allocation-friendly and avoid introducing unnecessary indirection.
- Enforce no-allocation-regression as a hard gate for this plan.

## Non-goals

- No WinPTY support.
- No public backend extension point.
- No runtime plugin system.
- No large refactor for its own sake.

## Hard Constraints

- No allocation regression is allowed in hot paths.
- If a design cleanup introduces measurable allocation growth, that change is rejected or reworked.
- Prefer static dispatch, plain data structs/records, and localized helpers over abstraction layers that add runtime overhead.
- Do not add backend plugin points or polymorphic extension seams for future-proofing.
- Do not add new internal models just to make the design look cleaner; add one only when it removes duplication or clarifies a real boundary without allocation cost.
- Do not move existing normalization responsibilities away from well-scoped helpers unless the new location is clearly simpler and allocation-neutral.

## Current State

MiniPty already has a reasonable core shape:

- [src/MiniPty/Pty.cs](../../src/MiniPty/Pty.cs) is the public entry point.
- [src/MiniPty/PtyStartInfo.cs](../../src/MiniPty/PtyStartInfo.cs) carries launch options.
- [src/MiniPty/PtySession.cs](../../src/MiniPty/PtySession.cs) owns the live session lifecycle.
- [src/MiniPty/Internal/WindowsPtyBackend.cs](../../src/MiniPty/Internal/WindowsPtyBackend.cs) and [src/MiniPty/Internal/UnixPtyBackend.cs](../../src/MiniPty/Internal/UnixPtyBackend.cs) contain the platform-specific execution details.

The current structure is already directionally good, but the responsibilities are still somewhat blended inside the startup and backend layers.

## Proposed Direction

### 1. Keep public options as public data

Keep [src/MiniPty/PtyStartInfo.cs](../../src/MiniPty/PtyStartInfo.cs) as the user-facing launch contract.

This object should remain a simple, declarative description of the request:

- executable
- arguments
- working directory
- size
- environment
- terminal name

It should not contain launch orchestration logic.

### 2. Clarify launch normalization boundaries

Clarify where launch request normalization belongs before adding any new type.

Current responsibility boundaries should be preserved unless there is a concrete reason to change them:

- [src/MiniPty/PtyStartInfo.cs](../../src/MiniPty/PtyStartInfo.cs) remains the public request shape.
- `PtyStartInfo.ClampedSize` remains the size normalization point unless moving it demonstrably simplifies the startup path.
- `PtyEnvironment` remains the environment normalization point.
- Platform backends remain responsible for platform launch preparation.

Only introduce an internal launch model if the existing boundaries cannot express the responsibility split cleanly. If introduced, it must be a borrow-only, allocation-neutral internal shape, preferably a `readonly struct` that references existing data instead of copying it.

### 3. Separate backend selection from backend execution

Today the startup entry point chooses the platform backend directly. That is fine, but it can be made more explicit.

The plan is to split this into two small responsibilities:

- backend selection: choose the platform strategy for the current OS
- backend execution: perform the actual PTY launch and return a runtime backend instance

This can be done without introducing a large interface hierarchy.

Recommended approach:

- use a small internal selector function or static helper
- use platform-specific launcher methods that are simple, explicit, and local to the backend modules
- keep the implementation close to the data and avoid abstraction for abstraction’s sake

### 4. Keep the backend boundary thin

The existing backend concept is useful, but it should stay narrow.

Preferred approach:

- keep a thin internal runtime contract for the backend instance
- avoid making it a public abstraction or a general-purpose extension point
- avoid inheritance-based backend families
- prefer small, explicit functions and plain data records over a large framework

If a backend boundary is still valuable, it should be a very thin internal contract, not a general design system.

### 5. Let the session layer coordinate lifecycle only

The live session object should focus on the runtime lifecycle:

- input/output streaming
- resize
- exit waiting
- kill/dispose
- EOF semantics

It should not need to decide how the PTY is launched, and it should not host platform-specific start logic.

## Implementation Plan

### Phase 1: Clarify launch input normalization boundaries

Status: Completed.

Scope:

- Audit the current launch normalization responsibilities around [src/MiniPty/PtyStartInfo.cs](../../src/MiniPty/PtyStartInfo.cs), `PtyEnvironment`, and the platform backends
- Keep `PtyStartInfo.ClampedSize` and `PtyEnvironment` in place unless moving them is simpler and allocation-neutral
- Add an internal launch model only if the audit finds a real duplication or unclear boundary that cannot be fixed with smaller local changes
- If such a model is needed, make it borrow-only and allocation-neutral; do not copy arguments, environment, or strings for design cleanliness
- Start with test hardening for `PtyEnvironment` rather than introducing new launch models or splitting environment construction into separate types
- Keep `PtyEnvironment` as the single child-environment construction unit; do not split validation, sanitize, overlay, TERM defaulting, or materialization into separate files or types
- Keep behavior unchanged

Initial test-hardening scope:

- Add tests to the existing `PtyEnvironmentTests` file instead of creating new platform-specific test files
- Cover Unix sanitize and `TERM` default behavior by equivalence class, not by one broad smoke test or one test per sanitized key
- Include only overlay cases that affect Unix `TERM` priority in this first pass
- Include a minimal Windows contrast test showing that `TerminalName` does not create `TERM` on Windows
- Leave broader overlay semantics and Windows case-insensitive behavior for later focused tests unless an existing gap blocks this phase

Files likely affected:

- [src/MiniPty/PtyStartInfo.cs](../../src/MiniPty/PtyStartInfo.cs)
- [src/MiniPty/Pty.cs](../../src/MiniPty/Pty.cs)
- [src/MiniPty/Internal/PtyEnvironment.cs](../../src/MiniPty/Internal/PtyEnvironment.cs)

Acceptance criteria:

- public API remains unchanged
- behavior is identical
- environment and size preparation boundaries are easier to reason about
- no new internal launch model is added unless it has a concrete simplification benefit
- `PtyEnvironmentTests` covers the first-pass Unix sanitize / `TERM` equivalence classes and the minimal Windows `TerminalName` contrast
- no additional steady-state allocations in start and session hot paths

Implemented outcome:

- No production code or public API changes were required.
- `PtyEnvironment` remains the single child-environment construction unit.
- Existing tests were extended to pin Unix stale-terminal sanitize behavior, Unix `TERM` priority cases, and the Windows `TerminalName` contrast.
- Startup/completion allocation stayed flat in `Session_Exit0_Bytes` (`3.8 KB` before and after).

Lesson learned:

- The existing normalization boundaries were already sufficient for Phase 1. A new internal launch model would not reduce duplication or clarify enough behavior to justify the extra structure.

### Phase 2: Isolate backend selection

Status: Completed.

Scope:

- Extract platform/backend selection logic from [src/MiniPty/Pty.cs](../../src/MiniPty/Pty.cs)
- Introduce a small internal selector helper that returns the chosen execution strategy
- Keep the control flow simple and explicit
- Keep selection logic static and branch-based; do not introduce public or multi-layer runtime polymorphism
- Skip this phase if the extracted helper is more complex than the current direct `RuntimeInformation` branching

Files likely affected:

- [src/MiniPty/Pty.cs](../../src/MiniPty/Pty.cs)
- new internal helper file under [src/MiniPty/Internal](../../src/MiniPty/Internal)

Acceptance criteria:

- startup orchestration is easier to follow
- platform branching is localized
- no new public API is introduced
- the resulting code is simpler than or equal in complexity to the current direct branch
- no allocation increase versus baseline in startup-related benchmarks

Implemented outcome:

- No production code or public API changes were required.
- The existing [src/MiniPty/Pty.cs](../../src/MiniPty/Pty.cs) entry point remains the right boundary for null validation, supported-OS validation, backend family selection, and `PtySession` wrapping.
- `Pty.IsSupported` continues to define the supported OS set: Windows, Linux, macOS, and FreeBSD.
- `Pty.Start` continues to map Windows to `WindowsPtyBackend` and the remaining supported OSes to `UnixPtyBackend` after the support check.
- No selector helper was introduced because it would add indirection without reducing duplication or clarifying the startup path.

Lesson learned:

- In this case, isolating backend selection means preserving the small direct branch. The direct `RuntimeInformation` check is clearer and no less allocation-friendly than an internal helper.

### Phase 3: Thin backend execution boundary

Scope:

- Keep the runtime backend concept, but make it as small and focused as possible
- Ensure platform launch code is owned by the platform modules and not mixed with session orchestration
- Prefer plain data + focused functions over an extensible abstraction hierarchy
- Keep runtime backend boundary minimal; avoid additional interface layers unless proven necessary
- Do not add new interfaces; keep, shrink, or clarify the existing backend boundary only

Files likely affected:

- [src/MiniPty/Internal/WindowsPtyBackend.cs](../../src/MiniPty/Internal/WindowsPtyBackend.cs)
- [src/MiniPty/Internal/UnixPtyBackend.cs](../../src/MiniPty/Internal/UnixPtyBackend.cs)
- [src/MiniPty/PtySession.cs](../../src/MiniPty/PtySession.cs)

Acceptance criteria:

- the startup path is easy to inspect
- OS-specific code remains isolated
- the backend boundary stays minimal and understandable
- existing `IPtyBackend` usage is not expanded into a broader abstraction hierarchy
- no allocation increase in output/read, write, wait, and resize behavior benchmarks

### Phase 4: Lifecycle cleanup and docs

Scope:

- Document the responsibility split in the implementation comments and relevant docs
- Keep the internals understandable for future contributors
- Make sure the public API remains intuitive

Files likely affected:

- [src/MiniPty/PtySession.cs](../../src/MiniPty/PtySession.cs)
- [README.md](../../README.md)
- [/.github/docs/spec.md](../spec.md)

Acceptance criteria:

- the mental model is clear for maintainers
- no accidental API expansion
- no performance or allocation regression introduced by lifecycle cleanup changes

## Validation Plan

Each phase should be validated with:

- targeted tests for start/exit/lifecycle behavior
- benchmark checks for allocation-sensitive paths where relevant
- no regression in existing PTY session behavior

Allocation validation is a release gate for this plan:

- Run relevant benchmarks before and after each phase.
- Compare allocation metrics and fail the phase if allocations increase.
- If allocation regresses, investigate root cause and revise implementation before proceeding.

## Expected Outcome

After this brush-up, MiniPty should have a clearer internal structure:

- options stay declarative
- launch policy is explicit and localized
- platform execution stays isolated
- session runtime behavior remains focused on the live PTY lifecycle

That gives most of the architectural benefit of the backend-separation idea from vs-pty.net while staying faithful to MiniPty’s data-oriented and minimal style.
