---
title: The syntax spec says keywords are not reserved; `Keywords` reserves them
parent: ../quill-design-map.md
labels:
  - wayfinder:fog
status: open
assignee:
blocked_by: []
---

# The syntax spec says keywords are not reserved; `Keywords` reserves them

Noted 2026-10-04 while ruling on [`fun`](vestigial-fun-keyword.md), whose open question 2 is
this and was deliberately left out of that change.

## The mismatch, measured

`docs/wayfinder/macro-system/SYNTAX_SPEC.md:40` presents its keyword list under

> Keywords (not reserved; can be shadowed):

and `src/Quill.Kernel/TokenTree.cs:44` implements the opposite: `Keywords` is an
`ImmutableDictionary<string, Word>` keyed by spelling — the reader's reserved table, not a list
some rule happens to match. A spelling in it becomes a `Word`, and a `Word` with no rule falls
to the enforester's default arm:

```csharp
src/Quill.Expand/Enforest.cs:302
  throw new ExpandException($"unsupported Phase 7A keyword: {w.Spelling}", term.Span);
```

So the claim is not merely undocumented behaviour, it is **observably false**. Probed
2026-10-04, one program per spelling, each binding the keyword as an ordinary name and
returning it:

| program | result |
|---|---|
| `{ do = 1; do }` | `ELAB unsupported Phase 7A keyword: do` |
| `{ let = 1; let }` | `ELAB unsupported Phase 7A keyword: let` |
| `{ type = 1; type }` | `ELAB type: expected a name` — not a `Word` at all, consumed by the `type` form |
| `{ match = 1; match }` | `ELAB match is written match (scrutinee) { pattern => result, … }` — a rule matches it |

Four spellings from that one list, four refusals: none is shadowable today. The two shapes are
different and worth keeping apart — `do`/`let` are reserved by the *reader*, `type`/`match` are
consumed by a *rule* before a name is expected.

## Why this is fog and not a ticket

- **Which claim is the intended one is a ruling.** Either the spec's parenthetical is wrong (a
  one-line fix, and the rule for adding a spelling stays "reserved or freed"), or shadowing is
  intended and the reader/enforester has to resolve a keyword spelling as a name where a name is
  expected — a scoping question at a phase that currently has no binders to consult.
- **The precedent points one way and the spec the other.** Stage 11 increment 2 freed
  `then`/`with`/`end`/`else`/`Unit` by *deleting* them from `Keywords`, and `fun` followed on
  2026-10-04 (`values/freed-keywords.qll` pins all six). Both treated "a spelling with no rule"
  as a reason to free it, never as a reason to make it shadowable.
- **The surface may still move**, and this is exactly the kind of question the macro model
  settles rather than the reader: if `type` is a prelude macro rather than a keyword, "is a
  keyword shadowable" becomes "may a macro's name be rebound", which is a different and possibly
  already-answered question for library names.

## What would sharpen it

- A ruling on which sentence is authoritative.
- If shadowing is wanted: an example that must work, with the phase it is decided in (reader,
  enforester, or elaborator), because the three have different costs.
- If the spec is wrong: whether `SYNTAX_SPEC.md:40`'s list should also be regenerated from
  `Keywords`, so the two stop drifting (it omits `fun`, which was reserved until today, and lists
  `then`/`with`/`end`/`else`, which are not).
