---
title: The elaborated match tree as the backend's emitter receives it
labels:
  - wayfinder:topic
status: open
---

# The match decision tree as the emitter receives it

Companion to [the .NET backend](dotnet-backend.md) and to
[the term-shapes topic](dotnet-backend-term-shapes.md). Dump with
`tools/Quill.DumpTerm` (`quill-dump-term <file.qll> --expand --depth N --no-run`); the
`Match`/`tree =` node prints directly. Findings here were measured 2026-10-19 on the
backend slice 1b-ii fork.

## To dump: item 1 — a constructor match, and where payload binders live

`{ match (True) { True => 1, False => 2 } }` elaborates to

```
Match handler=0 effectBranches=0 patterns=[True/0() | False/0()]
  scrutinee = True                       (a Var in a nested program; a Dot/Con at top level)
  tree =
  Destruct at=base cases[2] default=none
    case True  = Leaf branch=0 bindings=[]
    case False = Leaf branch=1 bindings=[]
  body[0] = Atom 1
  body[1] = Atom 2
```

The `if` spelling is byte-for-byte this tree with `body` = the two block bodies; `if` never
survives as a term. **`Destruct.Cases` carries only the constructor's *name***, so the emitter
branches by comparing the value's constructor name, exactly as `Nbe.Match.cs`'s
`Cases.FirstOrDefault(c => c.Name == con.Name)` does. There is no tag number anywhere in
the tree.

A payloaded match (`values/elab-152.qll`, `Pair(a, Some(b)) => a - b`) nests the payload
positions in `Leaf.Bindings` as `Occurrence.Payload(parent, Constructor, Index)`: a leaf that
binds `a` and `b` names each payload by its *constructor name and index*, and the walk reads
them off the constructor value's `Args[Index]`. `Occurrence.Child` is a tuple slot
(`VProd.Items[i]`), `Occurrence.Field` a record field (`VRecord.Fields.Last(name==…)`); both
mirror `Nbe.Match`'s `At` case-for-case.

## To dump: item 2 — the shapes still refused, and their counts

After the walk landed, `--parity`'s `not ported yet: backend decision tree` went from **106 to
0**, with **0 differ**. The 106 cases re-blocked at later stages, in these proportions:

| landed on | n |
|---|---|
| `backend implicit application` | 36 |
| `backend prelude value` | 29 |
| `decision tree Sequential` | 14 |
| `backend captured local` | 11 |
| `backend Prod` | 7 |
| `decision tree TypeSwitch` | 3 |
| `Pi` / `Dot` / `conversion object -> Fn` | 3 |
| **produced a value** | **3** |

The three that now run are `macros/core-332.qll` (7), `values/core-108.qll` (7) and
`values/core-132.qll` (42). `Sequential` and `TypeSwitch` are deliberately still refused:
they need the interpreter's structural pattern matcher (`Nbe.Patterns.cs`) and a type-case's
runtime type respectively, neither of which is this slice.

**A `Destruct` cannot be exercised without a constructor *value*.** The tree passed but the
program re-blocked at `backend prelude value` for `match (True)`, because the ambient `True` is
a `VLam`/`VCon` the emitter cannot yet build. That block is 1b-i's (`s1bi`), not 1b-ii's.

## The interface 1b-ii needed from the constructor representation

The walk (`src/Quill.Backend/Emitter.Match.cs`) branches on constructors through one helper,
`MatchOps.ConstructorName(object value)`, which returns `((Value.VCon)value).Name` and reads
payloads as `((Value.VCon)value).Args[i]`. The interpreter's `Value.VCon` is the reference
shape. Nothing else was needed — the walk does not construct a constructor value, only reads
one.
