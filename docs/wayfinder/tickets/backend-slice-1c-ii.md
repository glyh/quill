---
title: Backend slice 1c-ii — the structural tier
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: []
# Filed 2026-10-20 by the integrator when 1c was cut in two: 1c-i (the nominal tier) landed,
# this is everything else the staging table's 1c row listed, and none of it is a nominal.
---

# Backend slice 1c-ii — the structural tier

The second half of [the .NET backend](../topics/dotnet-backend.md)'s 1c row: `Struct`, `Prod`/
`ProdTy`/`Proj`, `Dot` on a record, `Module`/`Imported`, and `open` of a module. The nominal half
landed as [1c-i](backend-slice-1c-i.md); nothing here interacts with it except that both write
values into the `object` tier.

## Entry points, measured 2026-10-20 (`--parity` on the merged 1c-i tree)

`900 cases, 419 match, 0 differ, 481 not ported`, `real coverage: 178 value-match`. The refusals
this slice owns, by the message the case reports first:

| count | refusal | example |
| --- | --- | --- |
| 115 | `backend Module` | `values/type-case-struct-field-type.qll`, most `std/*` cases |
| 41 | `backend Struct` | `values/core-140.qll` |
| 37 | `backend Imported` | a `Term.Imported` inside a program term |
| 9 | `backend Prod` | `values/core-113.qll` |
| 7 | `backend Dot` on a record/struct field | `values/core-020.qll` |
| 7 | `backend open of Var`/`Dot`/`Imported` (a module or a `sig`) | `values/elab-080.qll`, `std/open-lists-brings-bare-names.qll` |
| 5 | `backend Proj` | `values/type-case-former-parameter.qll` |

Adjacent refusals that are **not** this slice's, listed so the histogram is not mistaken for a
plan: `backend Pi` 3 and `backend conversion object -> Fn` 1 (a function *type* as a value — it needs
a closure over the `Pi`'s binder, so it is its own shape), `backend RecursiveOccurrence` 5,
`backend PatternSynonym` 1, `backend Sig` 2. The eighth `backend Dot` refusal is nominal and stays
1c-i's residue (`values/rec-enum-former-ignores-outer-name.qll`).

`Module` and `Imported` are the largest single bucket in the whole histogram, and both are
**unowned work rather than a slice's residue**: the staging table's "constructs no slice owns" note
already says so. The whole prelude arrives as one `Module` term, so whichever shape lands first
should decide whether the module is *skipped* (its value is the ambient environment the emitter
already builds) or *emitted*.

## What the term looks like

Dumped, not assumed:

- `struct {x : I64; y : I64}` is `Term.Struct` with `ConFields` and a `Bindings` list; a field read
  is `Term.Dot`, a tuple element is `Term.Proj`. `MatchOps.Field` and `MatchOps.ProdItem` read
  these, and both still read the **evaluator's** tier (`Value.VRecord`/`Value.VProd`) — see
  [the record tier ticket](backend-matchops-record-wrong-tier.md), which must land with this.
- A `module { … }` term is `Term.Module` holding `BindingTerm`s (each with `Slots()`), and
  `open` over one has `OpenMember.Field` members rather than `OpenMember.Constructor`.

## Acceptance

- The staging table's 1c gate: the `values/` match/struct/record population matches the
  interpreter, i.e. `--parity` **`differ` stays 0** and value-match rises by the size of this
  slice's buckets.
- `--parity` kept green means the same three things 1b and 1c-i were held to: `differ` 0, suite
  967/0, xUnit 217 — and a report of the new `value-match` / `both-errored` /
  `never-elaborated` split, not just the total.

## Not in this slice

refs and effects (1d); traits, dictionaries and type-case — including every `Sequential` and
`TypeSwitch` refusal (1e, corrected from the staging table's claim that they were 1c's); the
typed tier (1f).
