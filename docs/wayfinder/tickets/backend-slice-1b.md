---
title: Backend slice 1b — compile the prelude value graph and the rest of Match
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: closed
closed_date: 2026-10-20
resolution: Closed 2026-10-20, `cfc55e7`. The residue was **neither** a type position to skip **nor** the first demand for a nominal value — the dichotomy this ticket and its split-out ticket posed was a false one. `<=` never names `Option`: `EnsureClosureMethod` bound one parameter per `Lam` in the closure's chain and then emitted the body *with those Lams still on it*, binding each twice, so every ambient index in the body read one entry too far out — from `i64_to_bool` (context level 50) to `Option` (51), whose value is a `VLam` over a closure whose body is the `Term.Nominal`. The emitter was compiling the `Option` type former as a function. One line (`LamChain(...).Body`, the helper was already in the file, unused). That exposed a second and independent bug it had been hiding: `EmitSwitch` compared an object-tier boxed-`I64` scrutinee with `Ldc_I8 … Bne_Un`, which is not IL and never compares equal, so every such switch took its default arm (`i64_to_bool(0)` → `True`; only the first fix landing turned 3 suite cases into differs). Both fixed together. Full measurement and reasoning: [the residue ticket](backend-prelude-closure-reaches-nominal.md). Gates, integrator's own run on the merged tree: suite **967 cases, 0 failed**, xUnit **217/217**, `--parity` **900 cases, 381 match, 0 differ, 519 not ported**, `real coverage: 140 value-match` (was 132), `Nominal` refusals **52 → 44** (all 44 now program-level type declarations — 1c), `{ 5 <= 6 }` → `True`, `{ 1 < 2 }` → `True`, `{ 9 <= 2 }` → `False`, `{ min(3, 4) }` → `3`, `{ max(2, 9) }` → `9`, `{ abs(0 - 5) }` → `5`, and the `if` spelling of `fib(30)` → **832040 in 0.81 s**.
assignee:
blocked_by: []
# Filed 2026-10-19 by the integrator, off the reconnaissance below. Slice 1a' landed
# 2026-10-17. 1b-ii landed 2026-10-19 (926eb49); 1b-i landed the same day (b416d65) with
# one measured residue left, which stayed open until `cfc55e7` closed it on 2026-10-20.
#
# Residue (2026-10-19): `{ 5 <= 6 }`, `{ 1 < 2 }`, `{ min(3, 4) }` and the `if` spelling of
# fib all stop at `not ported yet: backend Nominal`. **Split into its own ticket the same day:**
# [compiling a prelude closure reaches a Nominal it cannot emit](backend-prelude-closure-reaches-nominal.md).
# The Nominal is inside a **prelude closure body**, not the program: a probe walking the elaborated
# program term counts **zero** Nominal terms, so it is the closure-conversion path's hole and NOT
# 1c's general nominal tier. That reading held; the fix was still smaller than either reading —
# the closure's captured environment was read at the wrong index, and no nominal value is ever
# demanded by these programs.
---

# Backend slice 1b — compile the prelude value graph and the rest of Match

The staging table in [the .NET backend topic](../topics/dotnet-backend.md) describes slices 1a′–2
as prose. This ticket is 1b, filed so a fork has a name, frontmatter and a gate.

## Where it stands — 2026-10-19

*Every `currently …` in this ticket is that date's measurement, kept as the acceptance record.
**All of its gates pass as of `cfc55e7` (2026-10-20)** — see the frontmatter's `resolution`.*

**1b-ii (the `Match` walk) is done**, merged `926eb49`; **1b-i (the prelude value graph) is
mostly done**, merged `b416d65`, with **one measured residue** (the frontmatter above).
Integrator's own run on the merged tree, not a fork's report:

| | before | after |
|---|---|---|
| conformance | 967 / 0 | **967 / 0** |
| xUnit | 217 | **217** |
| parity value-match | 111 | **132** |
| parity differ | 0 | **0** |
| `decision tree` refusals | 106 | **17** (Sequential 14, TypeSwitch 3 — deliberate) |

Now working through `Backend.Run`: `{ True }` → `True`, `{ i64_to_bool(1) }` → `True`,
`{ not(False) }` → `True`, `{ match (True) { True => 1, False => 2 } }` → `1`,
`{ if (True) { 3 } else { 4 } }` → `3`, `{ rec fib … match … }` → `832040`, and the `if`
spelling of `fib(30)` now gets past the tree (to the residue above) instead of dying at it.

**The two halves were built in parallel and the seam cost a merge — see the topic doc's
"The seam this cost a merge".** Neither branch alone could fail on it: 1b-ii's `Destruct` body
was unreachable until 1b-i could materialise a constructor value. Two bugs, both compile-clean
and xUnit-clean, and both only visible through `--parity`: `MatchOps` was `internal` (emitted
code is a separately loaded assembly and cannot call internals → `MethodAccessException`), and
it read `Value.VCon` where emitted code produces `Runtime.Con` (→ `InvalidOperationException`
on the 17 cases that reach a `Destruct`). **Lesson for the next parallel split: run the parity
harness before reporting, and treat a fork's unmeasured suite numbers as unmeasured.**

The seam left three tickets behind, all filed the same day rather than left in this one's prose:
the second copy of the tier bug
([`MatchOps.Payload`](backend-matchops-payload-wrong-tier.md)), the harness's silent exit status
([`--parity` exits 0 on a differ](parity-exits-zero-on-differ.md)), and this ticket's own residue
([the prelude-closure `Nominal`](backend-prelude-closure-reaches-nominal.md)).

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

**Two gates, one per half — the halves are independent and the original single gate was
wrong.** Measured 2026-10-19 at `e1b951a`: the `fib(30)` gate was **already met before 1b
started**, by the `match` spelling rather than the `if` one.

**1b-ii (Match).** `{ rec fib : I64 -> I64 = fn(n) { match (n) { 0 => 0, 1 => 1, _ => fib(n - 1) +
fib(n - 2) } }; fib(30) }` → **`832040` through `Backend.Run` in 0.6 s**, against **7.2 s** for the
interpreter on this machine (three runs, same answer each time). Yes, this passes today — so
1b-ii is measured by *coverage*, not by this case: the `if` spelling
(`… fn(n) { if (n < 2) { n } else { … } }`) is refused `not ported yet: backend decision tree`,
as is every constructor match:

- `{ Color = enum { Red, Green }; open Color; match (Color.Green) { Red => 1, Green => 2 } }` →
  expects **`2`**; currently `not ported yet: backend Nominal`.
- The `Red` variant → expects **`1`**.

**1b-i (prelude value graph).** `{ 5 <= 6 }` → **`True`**, currently `not ported yet: backend
prelude value`. Note the rendering: `Driver.Describe` prints a `VCon` by its **name**
(`Driver.cs:69-74`), not as an `I64` — measured with `--file`, which prints `VALUE True`. So the
backend's `Runtime.Describe` must render a `Bool` constructor the same way, or this case becomes a
`differ` even though the emitter is correct. Also expected: `{ min(3, 4) }` → **`3`**,
`{ abs(0 - 5) }` → **`5`**, `{ max(2, 9) }` → **`9`**.

**Both halves.** `--parity`: **differ stays 0**; `not ported` falls and the fork reports the new
value-match / both-errored / both-elab-failed split, not just the total. Suite unchanged at **967
cases, 0 failed**; xUnit at 217, or more with a new test.

## Not in this slice

`Struct`/records/tuples/`Proj`/`Dot`/`Open` (1c), refs and effects (1d), traits/dictionaries and
type-case (1e), the annotation pass (1f). `Implicit application` is 1e and is expected to stay
refused here.

## Sharpens when

**Measured 2026-10-19 — the halves are severable *in the emitter* but not *in their gates*, and
the first attempt failed by ignoring the second half of that.** The value-graph walk (finding 1)
and the decision-tree walk (finding 2) share only `Emitter.cs`'s `Scope`/`Ctx` plumbing, no data
structure. `fib(30)` passing today by the `match` spelling while the `if` spelling and every
constructor match are refused is the proof that **1b-ii is reachable with no prelude-value work at
all**.

**But that is the literal-`I64` `Switch` shape, which was already done in 1a′.** Every remaining
`Match` shape is a `Destruct`, and a `Destruct` branches on constructors *by definition* — so it
needs a constructor **value** to branch on, which is 1b-i's representation. Measured, and the
coupling is hidden by check order (`EmitMatch` validates the tree at `:326` *before* emitting the
scrutinee):

```
$ quill run '{ match (True) { True => 1, False => 2 } }'
quill: not ported yet: backend decision tree      <- the tree check throws first
$ quill run '{ x = True; match (x) { True => 1, False => 2 } }'
quill: not ported yet: backend prelude value      <- tree passed, the ambient True value is missing
```

So **the constructor value representation is the shared prerequisite**: it is 1b-i's to build and
1b-ii's to branch on. Consequences for briefing:

- 1b-ii's progress is measured by `--parity`'s `NOTPORTED` histogram (`decision tree` 106 falling,
  with cases re-blocking at a later stage such as `prelude value`/`Nominal`), **not** by an
  end-to-end value — a case that gets past the tree and dies at `prelude value` is a success for
  1b-ii and a missing prerequisite for 1b-i.
- The enum gate (`{ Color = enum { Red, Green }; open Color; … }`) is **not** a 1b gate at all: its
  scrutinee is `Dot .Red` over `Var 2`, so it needs `Term.Nominal` and `Term.Dot`, which are 1c and
  owned by neither half.
- `Tools/Quill.DumpTerm` gained `--ambient=N` (the index space the emitter's `Var` actually uses,
  which `--env=N` cannot reach past the context width) and a real `InvalidCastException` crash fix
  (`std` holds `ModuleEntry.Impl`; both `VModule` and `VStruct` printed assumed `Field`) at
  `e1b951a`. Both were found by the first, failed attempt — so the attempt was not wasted even
  though it landed no emitter code.
