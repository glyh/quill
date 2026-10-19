---
title: Backend slice 1b — compile the prelude value graph and the rest of Match
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: []
# Filed 2026-10-19 by the integrator, off the reconnaissance below. Slice 1a' landed
# 2026-10-17; this is the next slice in docs/wayfinder/topics/dotnet-backend.md's staging
# table, which had no ticket of its own until now.
---

# Backend slice 1b — compile the prelude value graph and the rest of Match

The staging table in [the .NET backend topic](../topics/dotnet-backend.md) describes slices 1a′–2
as prose. This ticket is 1b, filed so a fork has a name, frontmatter and a gate.

## Reconnaissance (integrator, 2026-10-19, HEAD `4eb7594`)

All measured, not read off a doc. Suite `967 cases, 0 failed`, xUnit `217/217`,
`--parity` **900 cases, 352 match, 0 differ, 548 not ported**.

**1. What 1b's first half actually is.** `{ 5 <= 6 }` fails `not ported yet: backend prelude
value`. `quill-dump-term --env=21` on it shows why: `Var 21` resolves to
`VNeutral head=HPrim { Name = + }`-shaped entries for primitives, but `<=` is a **prelude field**
— `std/lib.qll:29`, `pub (<=) = fn(x, y) { i64_to_bool(le_i64(x, y)) }` — so it arrives as a
`VLam(Closure(Environment, Term))` reachable only by closure conversion over the value graph.
`{ 1 + 1 }` works today only because `+` is a primitive the emitter already special-cases.
`Tools/Quill.DumpTerm` (salvaged 2026-10-17, 511 lines) is the instrument: `--env=N --values`.

**2. The second half, and the shape `if` really lowers to.** `fib(30)` fails `not ported yet:
backend decision tree`. The dumped term shows `if` is not a term: it is `Match` with
`tree = Destruct at=base cases[2] default=none` over `True`/`False`. The emitter compiles exactly
one tree shape today — `Switch { At: Occurrence.Base }` with literal `I64` keys and binding-free
leaves (`Emitter.cs:320-379`) — so `Destruct` is entirely unported.

**3. `Con` is a prerequisite of 1b, and the staging table gets this wrong.** The table lists
`Con`/nominal/struct/record under **1c**. But `if (n < 2)` does not compare `I64`: it routes
through `i64_to_bool` (`std/bootstrap.qll:6`, `match (n) { 0 => False, _ => True }`), which
**returns constructors**, and the `if` then destructures `Bool`. The same is true of every
prelude predicate and of `min`/`max`/`abs` (`std/lib.qll:63-77`). So 1b's own gate needs `Con`,
and so does the first prelude function anyone compiles. **1b therefore carries a minimal nominal
representation**, and 1c keeps the general one (`Struct`, records, tuples, `Proj`/`Dot`, `Open`).

**4. A `Con`-free gate already exists and passes**, which is how the two halves were separated
without guesswork: `{ rec fib : I64 -> I64 = fn(n) { match (n) { 0 => 0, 1 => 1, _ => fib(n - 1) +
fib(n - 2) } }; fib(20) }` → **`6765`** through the backend today. Pure-`I64` `Switch` works;
what does not is `Destruct`, and any reference to a prelude function.

**5. The parity harness's "352 match" is ~3× inflated**, and any fork's report inherits the
error. Instrumented run of `--parity` splitting the 352: **value-match 111, both-errored 6,
both-elaboration-failed 235**. Since there are 658 value-producing cases, real coverage is
**111/658 ≈ 17%**. A case whose elaboration fails is counted as a match without either path
running.

## What it touches

- `src/Quill.Backend/Emitter.cs` — `EmitVar`'s `Value` fallthrough (currently
  `not ported yet: backend prelude value`, `:171`) and `EmitMatch` (`:324`). One file.
- `src/Quill.Backend/Runtime.cs` — a nominal representation, if `Con` needs one beyond
  `Describe`'s existing fallback.
- `test/Quill.Conformance/Program.cs` — the `--parity` breakdown (finding 5).
- `docs/wayfinder/topics/dotnet-backend.md` + `dotnet-backend-term-shapes.md` — the two
  staging-table corrections (finding 3) and whichever shapes get dumped.

## Acceptance

- `fib(30)` → **`832040`** through `Backend.Run`, and faster than the 22.5 s the interpreter
  takes on this machine (measured today; the topic doc's 13.6 s baseline was a different host).
- `{ 5 <= 6 }` → `1` (in `Driver.Describe`'s terms; the interpreter prints the value — match it).
- `{ 1 == 1 }` either works or still refuses **only** on `implicit application`, which is 1e.
- `--parity`: **differ stays 0**. `not ported` must fall, and the fork reports the new
  value-match / both-errored / both-elab-failed split, not just the total.
- Suite unchanged at **967 cases, 0 failed**; xUnit at 217, or more with a new test.

## Not in this slice

`Struct`/records/tuples/`Proj`/`Dot`/`Open` (1c), refs and effects (1d), traits/dictionaries and
type-case (1e), the annotation pass (1f). `Implicit application` is 1e and is expected to stay
refused here.

## Sharpens when

The value-graph walk (finding 1) and the decision-tree walk (finding 2) are independent of each
other but share `Emitter.cs`'s `Scope`/`Ctx` plumbing, so they are one fork rather than two —
a second fork on the same file would only rebase.
