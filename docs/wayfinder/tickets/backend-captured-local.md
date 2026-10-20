---
title: Backend — a nested lambda reading another method's local
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: closed
closed_date: 2026-10-20
resolution: Closed 2026-10-20 in slice 1c-ii, by shape 1 — a generated closure class per capturing lambda, with **one** `object[]` field rather than a field per captured value (`shortcut:` note in `Emitter.EnsureLambda`). A lambda that captures nothing is still a static method, so the prelude value graph and its `tail.` prefixes are untouched (20M-deep self-tail probe: `42` in 1.1 s, `tail.` still in the image). A capturing lambda's body becomes an instance method of `Closure_N`; the delegate is built over the instance, whose `ldftn` needs the object on the stack but does not consume it — a `dup` there is an invalid program, which is how the first draft died. Captures keep their positions in the scope list, so every de Bruijn index, and every ambient index behind it, still means what the elaborator decided. `captured local` refusals **31 → 3**; the three left are not lambdas: a `rec` whose body reads an enclosing local, filed as [rec over an enclosing local](backend-rec-over-an-enclosing-local.md). The `_lambdas` cache is now keyed by (lambda by reference, shapes) — a lambda ensured at two shapes produced a delegate whose signature did not match its method.
assignee:
blocked_by: []
# Filed 2026-10-20 from 1c-i's parity histogram: 31 cases report
# `not ported yet: backend captured local`, the second-largest refusal after `implicit
# application`, and no slice's ticket claims it.
---

# Backend — a nested lambda reading another method's local

`Emitter.cs`'s `EmitVar` handles a local, a generated method, and (as of 1c-i) a nominal and a
constructor. The one case it refuses:

```csharp
// A local of another method would need a real closure; that is 1b's job.
case Binding.Local:
    throw new NotImplementedException("not ported yet: backend captured local");
```

`EnsureLambda` and `EnsureClosureMethod` generate **static** methods precisely so that no closure
object is needed: a prelude function's body only reads the ambient environment, and a `let`-bound
lambda becomes a method whose scope is its enclosing one. That works while the lambda's body reads
only binders that are themselves methods or ambient values. It stops the first time a lambda body
reads a **local of the enclosing method** — then the value has to travel with the delegate, which
means a closure object (or an environment threaded as an extra parameter).

## Entry points, measured 2026-10-20

`--parity` on the merged 1c-i tree: **31** cases, e.g. `macros/core-200.qll`,
`values/type-case-constructor-entries.qll`. Those two are also behind other walls, so the first job
is to find the ten or so where this is the *only* refusal — the histogram's per-kind example is now
printed by the runner, but a full per-case list is not: measure it before briefing a fork.

## What it touches

`src/Quill.Backend/Emitter.cs` (`Scope`/`Binding`, `EnsureLambda`, `EnsureFixMember`) and
`src/Quill.Backend/Emitter.Prelude.cs` (`EnsureClosureMethod`). Two candidate shapes, and the
decision is which one to take:

1. **A generated closure class per capturing lambda** — the honest one, and what F# does; a field
   per captured local, `Ldftn` over an instance `Invoke`.
2. **The environment as an extra parameter** — every generated method already takes its parameters
   as objects, so threading one more is cheap; but it changes every call site in the emitter and
   makes `Arity` lie.

## Acceptance

`--parity`'s `not ported yet: backend captured local` reaches 0 with `differ` still 0, suite
967/0, xUnit 217 — and the emitted image byte-scanned for `tail.` if the chosen shape moves a call
site (1a′'s tail-call probe is the regression test).
