---
title: The negative-tuple-arity refusal, and crash-class mutants in the suite
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: closed
closed_date: 2026-10-04
resolution: Closed 2026-10-04 — every item under *Remains* is measured. The guard stays (`Primitives.cs:165`); `elaborate/tuple-negative-arity.qll` pins the in-process refusal, `elaborate/tuple-negative-arity-crash-class.qll` the crash class, and `--isolated` (already in `test/Quill.Conformance`) classifies either outcome as one case's failure — measured both ways (`b3fa0fc`, `ddd138d`). Standing process note, kept here rather than in code: the mutation sweep runs with `--isolated`, because a crash-class mutant is only classifiable per case that way.
assignee:
blocked_by:
---

# The negative-tuple-arity refusal, and crash-class mutants in the suite

> **Filed 2026-10-03** — the mutation sweep row for `Primitives.cs:165` (in
> [coverage-gaps-from-the-mutation-sweep](coverage-gaps-from-the-mutation-sweep.md)) said the
> refusal is "only observable as a process-killing stack overflow". Measured that day: the
> guard is correct semantics, and the suite can now pin it — with a gap that remains.

## The item

`Primitives.cs:165` — `TupleArityType`'s `if (n.Value < 0) throw new FunException("Tuple: the
number of components is negative")`. Removing it (the sweep's mutant) makes `tuple_arity(-5)`
return `Type -> tuple_arity(-6) -> …`, an infinite `VPi` chain, and consumers diverge:

- **Re-refuse:** unifying the chain with `VU` gives `type mismatch: cannot unify VPi with VU` —
  still `error`, so a case expecting `error` still passes, and the mutation is invisible.
- **Force-walk:** `Unify.Mentions` recurses natively over the chain, each level forcing the next
  `tuple_arity(n-1)` through the evaluator. The native stack overflows — a
  `StackOverflowException`, which .NET does not let a catch intercept, so the process dies
  (verified: exit 134, core dump) taking every other case's result with it. The same walk can
  also still be running when the runner's 60s elaboration timeout fires (verified under
  `--isolated`). Which fires first is a race; both were observed.

## Ruling

**The guard stays, and it is the assertion.** `Tuple(n, …)`'s first argument must be positive;
`Primitives.cs:165` asserts it. No depth budget is added to the evaluator — the refusal is the
correct semantics, not a case a budget should absorb.

## What pins it now

- `elaborate/tuple-negative-arity.qll` (`{ y = Tuple(0 - 5); 1 }`, expects `error`) — passes on
  the clean tree, and **fails on the mutant** (observed under `--isolated`: the 60s elaboration
  timeout). The guard is pinned in-process, at the cost of a minute when it is broken.
- `elaborate/tuple-negative-arity-crash-class.qll` (`{ g = fn(x) { x }; g(Tuple(0 - 5)); 1 }`,
  expects `error`) — **added 2026-10-04**, the crash-class spelling above, beside its sibling.
  Passes on the clean tree (`ELAB Tuple: the number of components is negative`; suite 962 cases,
  0 failed). This shape cannot be trusted in-process on the mutant (it kills the runner), so the
  case exists for `--isolated` — see the measurements under *Remains*.
- The runner's `--isolated` mode (filed 2026-10-03, `test/Quill.Conformance/Program.cs`) runs
  each case as a `--case` child process, so a crash is one case's failure. Verified against
  the mutant: the runner survives, reports the case as failed, exits 1.

## Remains

- **The in-process suite cannot be trusted with this mutant.** The case above is in the suite,
  and on the mutant its elaboration never terminates — it either overflows (runner dies) or
  times out at 60s (survivable), a race. `--isolated` removes the race: both outcomes are one
  case's failure.
- **The crash-class shape has no in-process case.** — **done 2026-10-04**:
  `elaborate/tuple-negative-arity-crash-class.qll` is the case; see *What pins it now*.
- **The mutation sweep should run with `--isolated`.** Any mutant whose failure mode is a crash —
  this one's shape, or any future infinite-type bug — is only classifiable per-case in isolated
  mode. **Measured 2026-10-04** with the guard at `Primitives.cs:165` deleted and rebuilt
  (`echo "build exit=$?"` → `build exit=0`), against the crash-class case in a one-case root:
  - `dotnet test/Quill.Conformance/bin/Debug/net10.0/Quill.Conformance.dll --isolated <root>` —
    both runs: `FAIL elaborate/tuple-negative-arity-crash-class.qll: FAIL elaboration did not
    finish within 60s`, then `conformance (isolated): 1 cases, 1 failed`, runner exit 1,
    60s elapsed. The runner survives and the case is classified.
  - the same case as a raw `--case` child (the way `--isolated` spawns it) —
    `StackOverflowException` deep in `Budget.Request`/`Context.Unify` during elaboration,
    `Aborted (core dumped)`, exit 134, 1s elapsed.

  So the race described above landed both ways in one session: the timeout under the isolated
  harness, the uncatchable crash in the raw child. Either outcome is one case's failure under
  `--isolated` and a dead in-process run. The guard was restored afterwards (`git diff
  src/Quill.Compiler/Primitives.cs` empty against the pre-ablation commit); the ablation is in no
  commit.
