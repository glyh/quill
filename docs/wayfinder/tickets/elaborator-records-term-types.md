---
title: The elaborator records a type for every term it builds
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: []
---

# The elaborator records a type for every term it builds

## Where it stands — 2026-10-19, verified by the integrator

**The branch merges clean and is green, and still nothing consumes the table.**
`fork/elaborator-records-term-types` (`eaf8628`) was merged into a scratch worktree over
`4eb7594`, its scratch test deleted, and run: **build 0 errors, conformance 967/0, xUnit 217,
parity 352 match / 0 differ / 548 not ported — unchanged** (352/548 are that base's numbers, from
before `59894d6` split the count; today's runner would print value-match). So the merge itself
costs nothing and risks nothing; what it does not do is make the table total.

**The hole table below reproduces**, by copying `CoverageProbe` from the sibling branch
`pi-agent-ca0ce18c-6795-469` (`9527ae1`) onto that merged tree — it is that branch's file, not
`eaf8628`'s. Result: `Fix` 0/1 and 0/2, `Lam` 0/2 in the trait program and 0/4 in the mutual-rec
one, `Struct` 0/1 and 1/3. Unchanged from the 2026-10-13 measurement.

**The three owed items, in order — this is the whole remaining work:**
1. **Delete `test/Quill.Tests/ProbeDumpTests.cs`** (44 lines; it writes `/tmp/probe-dump.txt` and
   asserts nothing). It is the only reason the branch is described as unmergeable, and deleting it
   was sufficient — the merge above was otherwise conflict-free.
2. **Bring in `CoverageProbe` and turn it into an assertion.** It lives on the sibling branch
   (`9527ae1`), not on `eaf8628`, and it currently prints per-category counts and asserts nothing,
   so it passes on an empty table. A per-category non-trivial recorded count, or a test that fails
   on an empty table, is the difference between a measurement and a diagnostic.
3. **Record the missing sites, `Fix` first** — `.Structs.cs`, `.Traits.cs`, `.Match.cs` and
   `.RecTypes.cs` have no recording call sites at all. `Fix` is what a signature-hungry emitter
   needs most and is recorded nowhere.

**Why it is still not the frontier's next step:** its own sentence below is the answer — *nothing
consumes the table yet*. Slice 1f is where a consumer arrives, and the emitter has proved it can
reach **178 value-match** with `Let.Type` and the object tier alone (a lambda with no written type
degrades to `Func<object,object>`, which is slower but correct). So this is **enabling work for
1f**, and its three items belong to whichever fork next touches the elaborator rather than to a
fork of their own.

Dated correction, 2026-10-20 (later the same day): 1c-i and 1c-ii landed, so the frontier is now
**310 value-match** and the numbers above (140 after 1b, 132 before it, 178 after 1c-i) are the
records of those trees. The argument is unchanged — the object tier carries the load, and
`Let.Type` is what keeps a lambda from degrading — but the *case* for 1f is now different in one
respect worth knowing before starting it: the one refusal 1c-ii left in this family is exactly a
value read back at a shape the object tier did not record
([core-061](../tickets/backend-object-tier-delegate-shape.md)), which the annotation table would
close. 1d and 1e are ahead of it, not 1c-ii.

## Where it stands — 2026-10-13, two forks died mid-work

The keying decision is **made and documented**, the mechanism works where it is wired, and the
recording is **incomplete in a way that is now measured rather than guessed**.

**Branches, neither merged.** `fork/elaborator-records-term-types` (`eaf8628`) and
`pi-agent-ca0ce18c-6795-469` (`9527ae1`, which merges the first and adds a measurement). Both
build, both pass xUnit (218) and the conformance suite (967 / 0 failed), and **neither is
mergeable**: the second is cut before the map and ticket updates above it, so merging it would
revert them, and both carry a scratch probe promoted to a test
(`test/Quill.Tests/ProbeDumpTests.cs` writes `/tmp/probe-dump.txt` and asserts nothing).

**The holes, measured** by running that branch's `CoverageProbe` (a diagnostic that prints
per-category counts; it is not an assertion-based test, which is itself part of what is missing):

| program | recorded / total |
| --- | --- |
| `trait Eq(a) … impl Eq(I64) = module { eq = fn(x, y) { x < y } }` | **Lam 0/2**, **Struct 0/1**, Let 2/2, Pi 2/2 |
| `struct C { v : I64; method get() { self.v } }` | **Lam 0/1**, **Struct 1/3**, Let 1/1, Pi 1/1 |
| `rec f : I64 -> I64 = fn(n) { if … }` | **Fix 0/1**, Lam 1/1, Match 1/1, Let 1/1, Pi 1/1 |
| `rec a = fn(x) { x } and b = fn(y) { y }` | **Fix 0/2**, **Lam 0/4**, Let 1/2, Pi 2/2 |
| `(fn(x) { x + 1 })(7)` | **Lam 1/1** — the ticket's key case passes |
| `match (1 + 2) { v => v + 1 }` | Match 1/1 |

Read off that: **`Fix` is never recorded at all** (the case a signature-hungry emitter needs most),
`Struct` fields and impl methods are mostly missed, and lambdas reached other than through a
recorded chain are missed. What works is `Let`, `Pi`, `Match`, and lambda chains under a recorded
head. Call sites exist in `Elaborator.cs`, `.Enum.cs`, `.Generalise.cs`, `.Methods.cs` only —
`.Structs.cs`, `.Traits.cs`, `.Match.cs` and `.RecTypes.cs` have none.

**What is still owed, in order:** delete the scratch test; make the coverage measurement an
assertion (per category, with a non-trivial recorded count, or a test that passes on an empty table
is worthless); then record the missing sites, `Fix` first. Nothing consumes the table yet, so this
is not on the backend's critical path.

## Question

`Core.term` is untyped: `Lam(Term Body)` (`Core.cs:19`) and
`Ap(Term Fn, Explicitness, Term Arg)` (`Core.cs:20`) carry no types, and no annotation table
exists anywhere in `src/`. A type-directed consumer therefore cannot ask "what is this
subterm's type" even though the elaborator knew it at every `Infer`/`Check` call.

Record it: an elaboration-time side table from term node to its type, exposed on
`Elaborated`.

## Why now

[A .NET backend](../topics/dotnet-backend.md) was ruled to emit **native** .NET types (`long`,
generated classes per nominal, real delegates) rather than the evaluator's `Value` model. A
typed emitter needs a signature for every lambda, and the term does not carry one. Measured
2026-10-13 by dumping the elaborated `fib`: the only types reachable today are the ones the
surface happened to write — `Let(Type, Def, Body)` keeps its annotation, so an annotated
`rec fib : I64 -> I64` arrives typed, while `(fn(x) { x + 1 })(7)` does not.

## What it touches

- **Every `Infer`/`Check` entry point** in `Elaborator.cs` and its partials: the type is in
  hand at each, so the recording is mechanical — but it must be *total* for the emitter's sake,
  and the binder-introducing cases (`Lam`, `Pi`, `Fix` members, `Struct` fields, `Impl` methods)
  are where a missed node is a hole rather than an error.
- **`Core.term` identity, not spans.** The table must key on node identity. Terms are records,
  so structural equality means two distinct occurrences of `Atom(I64 1)` are the *same* key;
  either key by reference or key by a path/index. This is the design question in this ticket,
  and it collides with the `EquatableArray` immutability rules.
- **The evaluator is unaffected** — this is compile-time information the backend reads; `Nbe`
  must keep working exactly as it does, and the suite must not move.
- **`Elaborated` gains a field** (`Driver.cs:23`), whose consumers are the runner and the CLI,
  plus the xUnit tests that construct or read it.

## Acceptance

- A test that walks a program's term and finds a recorded type for every node the emitter
  needs a signature for, with the unannotated-lambda case (`(fn(x) { x + 1 })(7)`) covered —
  that is the node today's term cannot answer.
- The full suite unchanged: 967 cases, 0 failed, xUnit green.
- A named answer to the identity question (reference versus path), written down, because a
  structural key over records silently collides.

## Sharpens when

A consumer needs a term's type that the surface did not write — today that consumer is the
backend's typed tier, and it is the only one.
