---
title: Load std units in any order, pinning only the bootstrap
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: []
---

# Load std units in any order, pinning only the bootstrap

## The complaint, measured

Adding `std/functor` required editing `Prelude.Order` (`src/Quill.Compiler/Prelude.cs:29`), a
fixed list, lowest first. Without that edit the import fails with

```
ELAB import not found: "std/functor"
```

**even though the source is embedded and the build is clean** — `strings` on
`Quill.Compiler.dll` lists `std/functor.qll` alongside `std/bootstrap.qll` and the rest. The
resource is there; the loader simply never offers the path, because `Prelude.Of` answers
only for paths that array names.

So the standard library carries a **hand-maintained topological order**. It is seven
entries today, every new unit is another insert whose position has to be right, and the
failure when it is not names nothing about the array — the message says the file is
missing, and it is not.

## What the order is actually for

Four things read it:

- **`Index` / `Of`** (`Prelude.cs:32,42`) — path → stage. A path the array does not name
  throws `not a prelude unit`.
- **`Load(path, Order.Take(i))`** (`Prelude.cs:39,70`) — each unit is elaborated with a
  `Loader` whose `_prelude` is the units **below** it, so its own imports resolve only
  downwards.
- **`Loader.Stdlib`** = `_prelude[^1]` (`Loader.cs:27`) — the unit bound as `Std` while
  that unit elaborates. The order therefore decides what `Std` means *inside* a std unit:
  for `std/functor` it was `std/option`, which is arbitrary.
- **Metas seeding** — `metas.SeedFrom(Prelude.Of(prelude[^1]).Metas)` (`Loader.cs:73`),
  which is what makes an exported macro's value speak of the same metas wherever it is
  used.

One unit is genuinely special, and the ticket keeps it pinned: the **bootstrap**, whose
stage is `SyntaxStage` — what reflection reads `Syntax` off, "so reading it does not wait
on a unit whose own body quotes while it is still being elaborated" (`Prelude.cs:53`) —
and whose `Load` runs the whole `PreludeAbi` check.

## Wanted

Pin the bootstrap. Let every other unit be found by asking for it.

1. **Demand-load.** `import "std/X"` resolves by loading `X`, recursively, instead of by
   `X` having to sit below its importer in a list. Adding a unit becomes adding a file.
2. **Derive the order.** Keep `Order`/`below` exactly as they are, but compute the list by
   walking imports from `stage2` and sorting topologically, instead of writing it by hand.
   The smallest change that removes the hand-maintenance, and it preserves every semantic
   the four readers above depend on.

(2) is the cheaper of the two and may be all that is wanted; (1) is the one that also
removes the *position* question rather than only the editing.

## Ruled 2026-10-04 — option 2 only, and the tie-break is the DFS

Waiting on a human decision was the right call: **the derived order does not coincide with
the hand list**, so "it preserves every semantic the four readers depend on" above is true only
of the readers that consume *membership* (`Index`/`Of`, `Load`'s `below` set), not of the ones
that consume *last-ness*.

- **Rule.** The order is the DFS post-order of the import graph rooted at `std/stage2`,
  following each unit's `import` lines in the order they are written, with the bootstrap
  pinned first. For today's sources that is
  `bootstrap, lib, list, type, option, functor, stage2` — today's hand list is
  `bootstrap, lib, list, option, functor, type, stage2`. `std/type` and
  `{std/option, std/functor}` are incomparable in the graph, so the difference is a
  tie-break, not a fact.
- **Accepted consequence (open questions 2 and 4).** `Std` inside a std unit, and metas
  seeding, become properties of the derived order: inside `std/option` the former unit is
  `std/list` and is now `std/type`. Nothing in `std/` reads `Std`, and the suite is the
  check that metas seeding still holds. Neither question is *answered* here — they are
  unblocked from this ticket, and stay open as language rulings.
- **Open question 3 (cycles).** Unchanged in behaviour: a cycle is refused, not knotted.
  The walk must terminate — it detects the cycle and fails naming it — rather than loop.
- **Open question 5** is untouched: "nothing below `std` is importable by a program" is a
  separate visibility rule and must not start or stop holding because of this change.
- **Option 1 is not this ticket.** Demand-loading is filed as
  [demand-load the std units](demand-load-std-units.md), blocked on nothing, to be taken up
  after this lands (it would replace the derived order rather than extend it).

## Open questions

1. **What replaces `below`?** Demand-loading makes it "the units this one imports,
   transitively" — but `Loader` takes a *list* and `Stdlib` is its **last** element. In a
   DAG, "last" is undefined: a unit may import two, neither after the other.
2. **What is `Std` inside a std unit?** Today it is whatever the array happened to put
   directly below. Nothing in `std/` appears to use it, and `stage2.qll` says only the
   topmost unit is a program's face — so the ticket should rule whether it becomes the
   bootstrap, or disappears for std units. A ruling, not a discovery.
3. **Cycles.** The current order cannot express one, and `Expanded` refuses them loudly
   (`circular import`, `Loader.cs:41`). Demand-loading needs a ruling: keep refusing, or
   knot them the way mutually-recursive nominals are knotted.
4. **Metas seeding under a DAG.** `SeedFrom(prelude[^1].Metas)` assumes a topmost unit.
   Which unit's metas seed which, when the graph branches?
5. **The program's import rule.** "Nothing below `std` is importable by a program"
   (`stage2.qll`) is a separate visibility rule and must not fall out of this change by
   accident.

## Sharpens when

The next std unit is added. That has now happened once — `std/functor`, `7ce4aed` — and
cost a compiler edit plus two rebuilds to discover why a correctly embedded file was
"not found".
