---
title: MatchOps.Payload still reads the interpreter's Value.VCon
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: closed
closed_date: 2026-10-20
resolution: Closed 2026-10-20 in slice 1c-i. `Payload` reads `Runtime.Con.Args`; `Runtime.Con` gained the argument array, so the path is reachable and is exercised through `--parity` — `values/elab-152.qll` (`Pair(a, Some(b)) => a - b`) → 6, `core-107` → 42, `elab-156` → 5, `elab-161` → 4. The rule this ticket ends with was applied to every other member while here, and two were still violating it: `ProdItem` and `Field` read `Value.VProd`/`Value.VRecord`, unreachable only until 1c-ii emits a record or a tuple — filed as [the record tier ticket](backend-matchops-record-wrong-tier.md) rather than fixed, on exactly this ticket's reasoning that an unexercisable fix looks verified and is not.
assignee:
blocked_by: []
# Found 2026-10-19 by the integrator while fixing the identical bug in
# MatchOps.ConstructorName (b416d65). Filed rather than fixed because it cannot be
# exercised today, and an unexercisable "fix" is exactly the kind of change that
# looks verified and is not.
---

# `MatchOps.Payload` still reads the interpreter's `Value.VCon`

`src/Quill.Backend/Emitter.Match.cs`'s `MatchOps` is the helper class emitted code calls
into for a `Match`. Its `ConstructorName` was fixed in `b416d65` to read `Runtime.Con`
(the emitted tier) instead of `Value.VCon` (the evaluator's tier). **`Payload` was not**,
and still reads the evaluator's type:

```csharp
public static object Payload(object value, int index) => value switch
{
    Value.VCon con => con.Args[index],          // <- should be the emitted tier
    _ => throw new InvalidOperationException($"a match reached a {value?.GetType().Name ?? "null"} its type rules out"),
};
```

## Why this was not a live failure before 1c-i

*(The state as filed; `Con` has carried its `Args` since 1c-i — see the resolution.)*

**`Runtime.Con` carried no payloads** — it was `public sealed record Con(string Name)`, a
deliberate slice-1b shortcut, because the only constructor 1b-i reached was `Bool`. So no
emitted value could be a payload-carrying constructor, `Payload` was never called with one,
and nothing in the suite could flip.

**That is the whole risk**: the bug and its invisibility have the same cause. The moment
1c adds payloads to `Runtime.Con` (or the general nominal tier arrives), `Payload` starts
receiving `Runtime.Con` values, the `Value.VCon` case never matches, and every
payload-binding match throws `InvalidOperationException: a match reached a Con its type
rules out` — **at run time, inside emitted code, on cases that build and pass xUnit.**
That is the exact shape of the bug `b416d65` fixed; this is its second copy.

## The fix, when it can be tested

Fixing it *now* would be untestable, so the ticket is deliberately left open with the fix
deferred to the change that makes it reachable. When 1c gives `Runtime.Con` its arguments:

1. Change `Payload` to read the emitted tier (`Runtime.Con`'s payload array, whatever 1c
   names it) and update the `MatchOps` remark, which currently documents the correct rule
   for `ConstructorName` only.
2. **Add the case that exercises it** — a payload binding such as
   `values/elab-152.qll`'s `Pair(a, Some(b)) => a - b` — and confirm through `--parity`,
   not through xUnit, since emitted code's failures are invisible to the suite.

## The generalisable rule this is the second instance of

**Two tiers, one name.** `Quill.Kernel.Value` is the evaluator's, and `Quill.Backend.Runtime`'s
types are the emitted program's; emitted code runs in a separately loaded assembly and can
hold only the latter. A helper emitted code calls into must (a) read the emitted tier, and
(b) be `public`, since it cannot call `internal` — both were violated here and both compiled
clean. Whichever change next touches `MatchOps` should check every member against both rules,
not just the one that is failing.

**CLOSED 2026-10-20 — both fixes landed in 1c-i, and the second sentence paid off.**
`Runtime.Con` is `record Con(string Name, object[] Args)` and `Payload` reads `con.Args[index]`;
this path now runs for real, confirmed through `--parity` on the payload-binding cases above,
not through xUnit. Checking the rest of the class found `ProdItem` and `Field` still reading the
evaluator's tier — the same bug, a third and fourth copy — filed as
[`MatchOps.ProdItem` and `Field`](backend-matchops-record-wrong-tier.md), because 1c-ii is what
makes them reachable.
