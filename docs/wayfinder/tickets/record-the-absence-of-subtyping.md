---
title: Record the absence of subtyping
parent: ../quill-design-map.md
labels:
  - wayfinder:grilling
status: closed
closed_date: 2026-10-04
resolution: Ruled 2026-10-04 and recorded (`351369d`): `quill` has no subtyping relation — NbE convertibility is the only equality, records are structural and exact, `grep -rniE 'coerc' src/` = **0**, and the one width rule (module↔signature unification in `Unify.Structs.Modules`) is named in three comments and pinned by `values/core-021.qll`. Detail: `topics/subtyping.md`. The status flip was the last thing outstanding; the ticket was already resolved in its body.
assignee:
blocked_by: []
---

# Record the absence of subtyping

## Question

Confirm and record that `quill` has no subtyping relation — convertibility (NbE) is the
only equality, records are structural, and no subtype rule exists anywhere.

## Context

- The Graydon-constraint audit (2026-10-01,
  [review-2026-10-01.md](../../ideas/review-2026-10-01.md)) found **zero** mentions of
  subtyping across `docs/wayfinder/quill-design-map.md`, `docs/STATUS.md`, and
  `docs/wayfinder/topics/`. It is the one row of Graydon's ten constraints with no record
  at all.
- The de facto answer is "none": there is no subtype relation, records are structural,
  and the only equality is NbE convertibility. But no document says so.
- The corpus's rival approach (a bounded set of subtyping rules — a mutable reference a
  subtype of a shared one) is roughly what heap brands do *without* a subtyping rule:
  `Ref(h, A)` and `Ref(h', A)` are simply different types, related by nothing.

## Resolution

**Ruled 2026-10-04: recorded, with the exception named.** `quill` has no subtyping relation —
NbE convertibility is the only equality, records are structural and exact, and there is no
coercion term anywhere (`grep -rniE 'coerc' src/` = **0**). Checking is conversion-only:
`Elaborator.Check` (`Elaborator.cs:342`) falls through to `AgreeWithExpected`
(`Elaborator.Structs.cs:331`) → `Context.Unify` (`Elaborator.cs:100`) → `Unify.Values`
(`Unify.cs:11`), and convertibility is quote-equality (`Nbe.Rec.cs:115`). `Ref(h, A)` and
`Ref(h', A)` are different types related by nothing — `Unify.cs:84` requires heap
convertibility.

**The one exception, already landed:** module↔signature unification admits width.
`Unify.Structs.Modules` (`Unify.Structs.cs:17`) lets a partial side — a signature's instance —
need only its own members present in the other side: extra members tolerated, missing members
refused, one-directional, no coercion. Pinned by `values/core-021.qll` and documented in
`port-structs-records-signatures.md:56`. `grep -rniE 'subtyp' src/` = **3** hits, all comments
naming this rule (`Core.Structs.cs:8`, `Unify.Structs.cs:17`, `Elaborator.Traits.cs:592`).

Detail: [`topics/subtyping.md`](../topics/subtyping.md).
