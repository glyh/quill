---
title: The object tier forgets a delegate's shape
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: []
# Filed 2026-10-20 by the integrator when slice 1c-ii closed. It is a *new* refusal: 1c-ii's
# conversion work made the object tier reachable for delegates, and this is the one case where
# the object tier's answer is not good enough to come back from.
---

# The object tier forgets a delegate's shape

A quill function value in emitted code is a `Func<param, ret>` whose shape is chosen at the point
it is emitted (`Cty.Fn`). When such a value is stored where the object tier holds it — a match
binder, a module member, a captured value, a record field, a tuple item — the shape is *erased*:
`object` records only "this is a delegate". `Convert` says so:

```csharp
// Only the universal shape can be read back out of the object tier: a delegate
// stored there keeps its own shape, and the object tier does not record which.
else if (to is Cty.Fn fn && fn == Cty.ObjFn) ctx.Il.Emit(OpCodes.Castclass, fn.Clr());
else throw new NotImplementedException($"not ported yet: backend conversion object -> {to.GetType().Name}");
```

Reading it back at the universal shape works. Reading it back at a **narrower** one cannot: the
stored delegate really is `Func<object, long>`, and no cast turns it into `Func<object, object>`.

One case, measured 2026-10-20 (`--parity` on the merged 1c-ii tree):

```quill
# values/core-061.qll — a variable pattern binds a closure scrutinee
{ h = match (fn(u : Unit) { 1 }) { x => x }; h(()) }
```

The match binder erases the shape (`MatchValue` is the scrutinee's *natural* shape, `object` for a
lambda), and the program's own type is `Unit -> I64`, so the arm's value is asked for at
`Func<object, long>` and the refusal fires. The interpreter has no such step: `VLam` is `VLam`.

## The three ways out

1. **Adapt on conversion**: emit, per target shape, a small wrapper that takes the universal
   delegate and calls it through `Runtime.Apply`, so `object → Func<A, B>` is a new closure rather
   than a cast. Costs one generated method per (source, target) shape pair and one allocation per
   conversion; sound for any shape.
2. **Stop erasing**: keep the object tier's delegates canonical (`Func<object, object>` only) and
   erase to it at every store, which means every typed delegate that enters the object tier gets a
   wrapper — the same machinery as (1), applied on the way in rather than on the way out.
3. **Refuse, as now**, until the typed tier (1f) gives the emitter a shape for every binder from
   the annotation pass, at which point `MatchValue` stops being `object` for a lambda.

3 is what the tree does today and is the cheapest while 1f is the answer to "what shape is this
binder"; 1f is also what makes the *other* half of this problem disappear — `values/core-061.qll`
is a case whose binder's shape the elaborator already knows and the emitter does not.

## Acceptance

`--parity`'s `conversion object -> Fn` reaches 0 with `differ` still 0, suite 967/0, xUnit 217 —
and, if shape 1 or 2 is taken, `Runtime.Apply`'s `DynamicInvoke` fallback (`shortcut:` note) is
what the wrapper replaces, so measure the object tier's call cost before and after.
