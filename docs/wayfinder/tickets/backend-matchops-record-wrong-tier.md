---
title: MatchOps.ProdItem and Field read the interpreter's tier too
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: [backend-slice-1c-ii.md]
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
