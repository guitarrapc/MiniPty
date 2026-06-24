# AGENTS.md

MiniPty is a NativeAOT-first, data-oriented, high-performance .NET PTY library.
Keep every change small, measurable, and friendly to zero-allocation implementation.

## Core Principles

- Treat NativeAOT compatibility as a hard requirement.
- Prefer data-oriented code, explicit ownership, and simple control flow.
- Aim for zero allocations in hot paths; when allocation is unavoidable, minimize it and make the cost obvious.
- Prefer `Span<T>`, `ReadOnlySpan<T>`, `Memory<T>`, pooling, reusable buffers, and value types where they reduce allocation without obscuring correctness.
- Avoid reflection patterns, dynamic code generation, runtime code discovery, and APIs that are fragile under trimming or AOT.
- Do not add third-party dependencies, all implementation must be self-contained.
- Keep public APIs minimal, predictable, and hard to misuse.
- Design APIs so that their usage is immediately intuitive. Simplicity is the ideal; complexity is the enemy.

## Implementation Guidance

- Preserve existing cross-platform behavior for Windows ConPTY and Unix PTY backends.
- Keep platform interop thin, explicit, and isolated under existing backend boundaries.
- Prefer async APIs that avoid hidden buffering and unnecessary task/allocation churn.
- Avoid LINQ, closures, iterator blocks, boxing, string formatting, and exception-driven control flow in hot paths.
- Use UTF-8/byte-oriented processing where possible; decode text only at API boundaries that require text.
- Use cancellation and disposal paths carefully; process/session cleanup must be deterministic.
- Do not broaden scope with unrelated refactors.

## Testing And Validation

- Add or update focused tests for behavior changes, especially process lifecycle, cancellation paths, output draining, terminal resize, and cross-platform edge cases.
- For performance-sensitive changes, add or update benchmarks under `MiniPty.Benchmarks` when useful.
- Run the narrowest relevant `dotnet test` or benchmark command you can justify before finishing.
- If a change may affect NativeAOT, verify publish/build behavior or explain why it was not run.

## Documentation

- Keep docs concise and implementation-neutral.
- Update README or samples when public API behavior changes.
- Document allocation or compatibility trade-offs only when they matter to users or future maintainers.
