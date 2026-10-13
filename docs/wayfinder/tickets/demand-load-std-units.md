---
title: Demand-load the std units, so a path is found by asking for it
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by:
  - load-std-units-in-any-order.md
---

# Demand-load the std units, so a path is found by asking for it

Split off 2026-10-04 from [load std units in any order](load-std-units-in-any-order.md), whose
ruling took **option 2 only** (derive the order by walking imports) and left this one — its
option 1 — as its own ticket. It is the larger of the two and the one that answers the
*position* question rather than only the editing:

> **Demand-load.** `import "std/X"` resolves by loading `X`, recursively, instead of by `X`
> having to sit below its importer in a list. Adding a unit becomes adding a file.

## Why it is not just the derivation again

The derivation keeps `Loader`'s contract intact: a loader takes a **list** of prelude units,
lowest first, and serves exactly those — a path the list does not name is not found. Option 2
made that list computed instead of written. Demand-loading makes it stop being a list:
`below` becomes "what this unit actually imports, transitively", which is what the two
remaining open questions of the parent ticket are waiting for.

## What it touches

- `src/Quill.Compiler/Prelude.cs` — `Order`/`Index`/`Stages` (a lazily-built cache keyed by
  path rather than a scheduled array; `SyntaxStage` still reads the bootstrap, which stays
  pinned because it is what reflection reads `Syntax` off).
- `src/Quill.Compiler/Loader.cs` — `_prelude`, `Stdlib` (`_prelude[^1]` is undefined in a
  graph: a unit may import two, neither after the other), and `Seeded`'s
  `SeedFrom(prelude[^1].Metas)`.
- `Loader.Expanded` (`Loader.cs:44`) already refuses a cycle loudly; demand-loading moves
  the decision from "cannot be expressed" to "refused or knotted, on purpose".

## Open questions inherited, still unanswered

1. **What replaces `below`** — transitively-imported units, as a set; then `Stdlib` needs a
   definition that is not "last".
2. **What `Std` is inside a std unit** — nothing in `std/` reads it today, so this is free to
   decide, but it must be decided rather than inherited from an array.
3. **Cycles** — keep refusing, or knot them the way mutually-recursive nominals are knotted.
4. **Metas seeding when the graph branches** — which unit's metas seed which.

## Sharpens when

A second std unit needs a peer that is not below it, or the derived order's tie-break has to
be explained to someone adding a unit.
