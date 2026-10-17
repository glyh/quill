---
title: A .NET backend
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by:
---

# A .NET backend

## Question

Add a compilation backend that targets .NET — lowering `Core.term` to something runnable on the
runtime the compiler itself already sits on (the implementation is C# on .NET 10) — so a `quill`
program can be built and run as a .NET artifact, not only interpreted through NbE.

## Where it stands

- **There is no backend.** The pipeline ends at NbE → value, and `src/Quill.Cli` is a stub that
  prints `quill: the .NET port has no entry point yet` and exits 1. The only way to run a program
  is the conformance runner's single-file mode.
- The only analysis is in `docs/ideas/compiler-architecture.md` §"Backend: C, LLVM, or your own —
  and what a second backend costs", whose verdict is **undecided and unscheduled**: *"there is no
  backend… Nothing in `docs/wayfinder` picks one — the closest is the fog item on the
  library-vs-compiler-machinery boundary (UFCS, FFI), which would have to be settled first."*
- This ticket is the first direction on the map that is **not** a language or macro question.

## The three options (from the ideas doc)

| option | what it means on a .NET host |
|---|---|
| (a) emit C from `Core.term` | a native dependency; the emitted code is the artifact |
| (b) a native dependency | call an existing backend (LLVM, libgccjit) instead of writing one |
| (c) a code generator over the host | write a generator **and** a collector — the ideas doc's option (c) |

## What it touches

- **A new pipeline stage after `Core.term`** — additive, not a restructure of the existing
  stages. The ideas doc: *"If a backend ever appears, this is the phase it will need."*
- **Effects/handlers.** .NET IL has no stack to save, so handlers would need evidence passing
  (route the request down as an argument) rather than stack capture —
  `docs/ideas/effects-and-handlers.md` records this as what makes handlers usable on .NET IL.
- **The FFI fog item** ("Library-level features vs compiler machinery") — a backend needs a story
  for calling out to .NET libraries, and the ideas doc says that boundary must be settled first.
- **Diagnostics.** Spans point at source today; a backend adds a second mapping
  (source → emitted code) to maintain.

## Why it is not scheduled

- **No consumer.** Every program in the tree runs through the interpreter; compile time is
  unmeasured, so the cost being optimized is unknown.
- **The second-backend cost is recorded:** Skew shipped four backends and still died of a small
  standard library.

## Open questions

1. **Which target** — (a) emit C, (b) a native dependency, or (c) a code generator plus
   collector over the host?
2. **Who owns the garbage collector** under option (c)?
3. **How do effects/handlers lower** — evidence passing (no stack on .NET IL) or a captured
   stack?
4. **Does a backend sharpen the content-addressed codebase fog item?** A persistent store pays
   off more when there is a build to cache — compile time is that item's sharpening condition.

## Ruling 2 — later on 2026-10-13: emit IL, and the effect tier

The ruling below was **superseded on its first point the same day**: the target is **IL**, not C#
source. Reason, measured rather than argued: F# covers mutual and higher-order tail calls with a
`tail.` prefix (inside the closure's `Invoke` method for the higher-order case), and with
`--tailcalls-` those shapes die at 300k frames with exit 134 — so `tail.` is what preserves
unbounded recursion, and C# has no syntax for it. `ILGenerator.Emit(OpCodes.Tailcall)` does, so the
backend is built with `System.Reflection.Emit` + `PersistedAssemblyBuilder` and the Roslyn
dependency is gone. Slice 1a is re-cut as **1a′**; its C#-text emitter is dropped, while its
`Runtime.cs`, CLI and parity harness are salvaged.

**Effects are ruled too: a state machine only where the effect row needs it.** Pure arrows emit
plain IL with `tail.`; arrows whose row is non-empty emit a machine — fields for what is live across
a suspension, an `int` state as the resume point, one switch at entry — with `perform` handing the
handler the machine itself as the continuation and `resume` re-entering at its state. One-shot
enforcement falls out of clearing the state, so `ref-nbe-continuation-used` stays an error with no
extra code. **Accepted limit:** a tail call inside a machine consumes CLR stack, because `tail.` is
illegal in a method carrying a state machine. F# made the same call, turning tail emission off in
its async path rather than solving it. Alternatives weighed, with emitted shapes:
[the two choices](../topics/dotnet-backend-choices.md).

Answers to the four original questions, updated:

1. **(c)**, a code generator over the host — emitting IL. Collector question (2) dissolves: the
   host has one.
2. **The CLR's GC.** No collector is written.
3. **Ruled** (above), and built in slice 1d.
4. **Yes, but not yet.** The interpreter baseline is measured (`fib(30)` = 13.6 s against 3.4 ms
   for the same program as C#), so compile time is no longer unmeasured; a persistent store still
   waits for a *build* to cache, which slice 2 produces.

## Ruling — 2026-10-13

> **Superseded on the target: see Ruling 2 above.** Kept as the record of what was decided first,
> and of the measurements that made the second ruling possible.

Option **(c) over the host**, in its cheapest form: **emit C#, compile in-process with Roslyn**,
value model **native .NET types** (`long`, generated classes per nominal, real delegates),
recursion **direct style on the CLR stack with a depth guard**, and the first slice accepted by
**running the 651 value-producing cases through `Backend.Run` beside `Driver.Run` and diffing the
`VALUE` lines.** Detail, evidence and staging: [a .NET backend](../topics/dotnet-backend.md).

Answers to the four questions:

1. **(c)** — a code generator over the host. Collector question (2) dissolves: the host has one.
2. **The CLR's GC.** No collector is written.
3. **Still open, and now scoped to slice 1d.** Effects are 55 of the 418 `values/` cases.
   `resume` re-pushes a captured `Kont` slice (`Nbe.Effects.cs:120`) and is one-shot by choice;
   the direct-style options are `async`/`await` state machines for effectful arrows only, or a
   trampoline kept for effectful code. The ideas doc's evidence-passing note is about routing,
   which the term already does (`Core.Tunnel`), not about `resume`.
4. **Yes, but not yet.** The interpreter baseline is now measured (`fib(30)` = 13.6 s against
   3.4 ms for the same program as C#), so compile time is no longer unmeasured; a persistent
   store still waits for a *build* to cache, which slice 2 produces.

**What the ruling does not do:** delete `Nbe`. The evaluator is the elaborator's normalizer
(`Elaborator.cs:93`), evaluates every import (`Loader.cs:130`) and every std unit
(`Prelude.cs:86`), and runs macros. The backend replaces the *program execution* path
(`Driver.Run`), which is what `src/Quill.Cli` will drive.

**Two findings that change the shape of the work, both probed rather than assumed:**

- **C# cannot emit tail calls** (`.NET 10.0.112`, x64, `-c Release`: self tail recursion overflows
  at ~250-280 k frames, mutual tail and non-tail at the same order; the JIT loop-converts nothing).
  Direct style therefore needs the depth guard, and TCO needs either explicit loop emission or a
  `tail.`-prefixed IL tier.
- **`Core.term` is untyped** — `Lam(Term Body)` has no domain and no annotation table exists. So
  the typed tier has a prerequisite: an elaboration-time node → type side table. Slice 1 emits
  typed signatures only where the term carries one (`Let.Type`, so `rec fib : I64 -> I64` is typed)
  and `object` elsewhere; the rest is [the annotation pass](elaborator-records-term-types.md).

## Sharpens when

Someone wants to run a `quill` program outside the interpreter — a binary, a library, or an FFI
consumer — and the interpreter is not enough. **Sharpened 2026-10-13**, and the consumer is the
user: hand optimization to the host and drop the interpreter as the execution path.
