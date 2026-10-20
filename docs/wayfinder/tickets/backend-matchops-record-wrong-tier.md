---
title: MatchOps.ProdItem and Field read the interpreter's tier too
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: closed
closed_date: 2026-10-20
resolution: Closed 2026-10-20 in slice 1c-ii. The emitted tier names its own shapes -- `Runtime.Prod`/`ProdTy`/`Record` -- and `ProdItem`/`Field` read those, so `Quill.Kernel.Value` no longer appears in `MatchOps` at all. Item 2: the cases that reach them are exercised through `--parity` and now agree with the interpreter — `values/core-113.qll` (`x + yz.0 + yz.1`) → `6`, `core-135`/`core-136`/`core-137` (tuple and record patterns) → `1`, `core-020`/`core-021` (field reads) → `1`, and the whole 41-case `Struct` bucket. Item 3: `Field` keeps the **last**-wins rule on purpose — `Nbe.Match.cs`'s field occurrence uses `Last` while `Nbe.DotValue` uses `FirstOrDefault`, so a pattern binding and a dot expression are deliberately different reads, and both are now implemented as the interpreter has them. `VProdTy`/`VRecord` as *type* values still need 1e's type-case; nothing in this slice reads a type's identity.
assignee:
blocked_by: []
# Filed 2026-10-20 while closing the MatchOps.Payload ticket, which ends with the rule this
# ticket is the residue of: "whichever change next touches MatchOps should check every member
# against both rules". Checked: ConstructorName and Payload now read the emitted tier; these two
# still read the evaluator's, and are unreachable only because no struct or tuple can be emitted.
---

# `MatchOps.ProdItem` and `Field` read the interpreter's tier too

`src/Quill.Backend/Emitter.Match.cs`'s `MatchOps` is the helper class emitted code calls into for
a `Match`. `ConstructorName` and `Payload` read the **emitted** tier (`Runtime.Con`) as of
`b416d65` and 1c-i. Two members still read `Quill.Kernel.Value`:

```csharp
public static object ProdItem(object value, int index) => value switch
{
    Value.VProd prod => prod.Items[index],          // <- the evaluator's tier
    Value.VProdTy prodTy => prodTy.Items[index],    // <- the evaluator's tier
    _ => throw new InvalidOperationException(…),
};

public static object Field(object value, string name) => value switch
{
    Value.VRecord record => record.Fields.Last(field => field.Name == name).Value,   // <- ditto
    _ => throw new InvalidOperationException(…),
};
```

## Why this is not a live failure today

`Term.Struct` and `Term.Prod` have no emitting case, so no emitted value can be a record or a
tuple, so neither member is ever called with one. **That is the whole risk, and it is the same
shape as the two tier bugs already fixed**: it compiles clean, passes xUnit, and throws
`InvalidOperationException: a match reached a … its type rules out` at run time inside emitted code
the moment 1c-ii lands, on the cases that reach a field or tuple pattern.

## The fix, when it can be tested

1. Give the emitted tier its own record and tuple values (`Runtime.Record`, `Runtime.Prod`, or
   whatever 1c-ii names them) and read those here.
2. Add the cases that reach them — `values/core-020.qll`/`core-021.qll` (a field read),
   `values/core-113.qll` (`Tuple`) — and confirm through `--parity`, not xUnit: emitted code's
   failures are invisible to the suite.
3. Note `Field`'s last-wins rule (I3) and `VProdTy`/`VRecord`-as-a-type-value, which a struct
   *type* passed as a value (`classify(Point)`, `values/core-093.qll`) needs — that is also where
   `Runtime.Nominal`'s dropped captures become load-bearing (see 1c-i's `shortcut:` note).
