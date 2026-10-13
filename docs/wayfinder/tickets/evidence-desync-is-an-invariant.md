---
title: A refinement/evidence desync is an invariant, not a language error
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: closed
closed_date: 2026-10-04
resolution: Closed 2026-10-04 (`d682254` carries the target, `1f2ede4` classifies the failure, `f63047d` the checks; merged `f63047d`). `Context.Refinement` carries the branch's `(level, replacement)` and is set only by `RefineContext`, so it reaches neither a sibling branch nor code after the `match`; the Evidence rewrite is factored into `RefinedEvidence`. `Elaborator.MissingImplementation` re-resolves the demand against each entry as `RefinedEvidence` *would* have written it — if that answers, the failure is the implementation's and throws `InvalidOperationException` with the ticket's message shape, while a genuine absence keeps `missing implementation of …` byte for byte. Ablation evidence, with the build's exit code printed before the observation, as required: `build exit=0` then `FAIL invariant failure (InvalidOperationException): evidence for `Size` was not refined: the branch asks for Size(Char), the entry still says Size(A). RefineContext's Evidence channel missed it.`; the ablation was restored, `git diff src/` empty, the case back to `VALUE 9`. No conformance case was added, deliberately: the desync has **no program** that reaches it, `cases/elaborate/elab-196.qll` is the genuine-absence path a user can write, the exact message is pinned in `TraitTests.cs`, and the regression check builds the broken context by hand (hence the five-line `InternalsVisibleTo` in `Quill.Compiler.csproj`). Integrator's own run on the merged tree: **967 cases, 0 failed**, xUnit **217/217**. Limits: one target, not a stack — a nested refinement keeps only the innermost; and the speculative re-resolution on the failure path touches the shared `MetaContext`, which is sound only because it always precedes a throw.
assignee:
blocked_by: []
---

# A refinement/evidence desync is an invariant, not a language error

Decided 2026-10-01, as the answer to
[type-case refinement](type-case-refinement-walks-whole-context.md)'s item 2 ("whether rewriting
`Evidence` is the *right* mechanism"). The ruling is **keep the rewrite, and classify its failure
correctly** — decided on the failure mode rather than on the mechanism, because the mechanism's two
candidates turned out to be indistinguishable by any program (see *Probes* below).

## The defect this fixes, measured

`RefineContext` rewrites each evidence entry's `Args`/`Type` through the same substitution that
refines the branch's context. Both sides must move together: the *demand* is computed fresh inside
the branch (where the matched variable reads as the matched head, per that ticket's now-decided item
1) and the *entry* is a copy made at the binder, before any branch existed.

Removing the entry rewrite (`Evidence = ctx.Evidence`) does not produce an internal error — it
produces:

```
ELAB missing implementation of `Size`
```

on `values/type-case-evidence.qll`, which reads as **the user's** mistake. The branch asked for
`Size(Char)`, the entry still said `Size(A)`, and the failure was reported as a missing impl. That
is the wrong-blame failure mode: a future refinement channel that forgets the evidence list will
look like a user error.

## The change

1. Carry the refinement's target on the `Context` while a branch is being elaborated (`RefineContext`
   already computes `level` and `replacement`; the branching path in `Elaborator.Match.cs` knows the
   branch's extent).
2. In evidence resolution (`Elaborator.Traits.cs:504`), when **nothing matches**, check whether an
   entry exists over that refined level — i.e. the failure is a desync rather than a genuine absence.
3. If it is, throw `InvalidOperationException` naming the mechanism, not `FunException`. The
   invariant class is the existing vocabulary for "the implementation broke", and the conformance
   runner already prints it as `invariant failure (InvalidOperationException): …` rather than as a
   language error (see [a mutation aborts the conformance run](conformance-runner-aborts-a-mutation.md)).

Target message shape:

```
invariant failure (InvalidOperationException): evidence for `Size` was not refined:
the branch asks for Size(Char), the entry still says Size(A). RefineContext's Evidence channel missed it.
```

A genuine absence — no entry over the variable at all — keeps today's `missing implementation of …`,
because that one *is* the user's.

## Acceptance

- Ablating the `Evidence` rewrite (`Evidence = ctx.Evidence`) turns `values/type-case-evidence.qll`'s
  failure from `missing implementation of \`Size\`` into the invariant message above, and the suite
  still reports `961 cases, 0 failed` when nothing is ablated.
- A program with no dictionary in scope at all still reports `missing implementation of …` (the
  user-facing error is not swallowed by the new branch).
- One check left behind that fails if the classification regresses.

## Probes (2026-10-01, on `a19ff3f`)

Run to find a program that distinguishes "rewrite the entries" from "key the lookup by binder".
**None exists**, which is why the decision above is about failure modes and not about semantics:

| probe | program | result |
|---|---|---|
| bound entry found in a branch | the ticket's own `type-case-evidence.qll` | `VALUE 9`; ablate → `missing implementation of Size` |
| demand from a literal, not the parameter | `match (A) { Char => Size.size('c'), _ => 0 }` with an impl | `VALUE 7` — **both routes hold the same dictionary**, so it cannot show which fired |
| pass a dictionary in explicitly | `f[Char][module { size = fn(c) { 100 } }]('a')` | `ELAB type mismatch: cannot unify VTraitDict with VModule` — a dictionary cannot be written literally, so two dictionaries for one key can never compete |

A binder-keyed lookup would need a demand to know **which binder it came from**; a demand carries
`Size(Char)`, not provenance. That design step is why it was not chosen, and it would have to be its
own ticket if anyone wants it.
