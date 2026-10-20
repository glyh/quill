---
title: A `rec` whose body reads an enclosing method's local
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: []
# Filed 2026-10-20 by the integrator when slice 1c-ii closed: this is the residue of the
# `captured local` bucket (31 → 3). It is no longer a lambda -- lambdas capture now -- but a
# recursive group, which needs one closure object *shared* by several members.
---

# A `rec` whose body reads an enclosing method's local

`Emitter.EnsureMember` emits one static method per `Fix` member and refuses when the enclosing
scope holds any local:

```csharp
// A `rec` whose body reads an enclosing method's local would need the same closure
// object every member shares; it is refused rather than emitted unreachable.
if (enclosing.Locals.Any(b => b is Binding.Local or Binding.Env))
    throw new NotImplementedException("not ported yet: backend captured local");
```

A lambda's captures are threaded by [1c-ii](backend-slice-1c-ii.md): `EnsureLambda` generates
`Closure_N` with one `object[]`, turns the body into an instance method, and replaces the captured
scope entries with `Binding.Env`. A `Fix` cannot reuse that as it stands, because the members are
emitted from `_fixMembers` (a `MethodBuilder` per member) and every sibling's body pushes the
*others* as `Binding.MethodRef`: the members call each other directly, so the environment would
have to reach each of them, not just the one the `let` binds.

The three cases left, measured 2026-10-20 (`--parity` on the merged 1c-ii tree):

| case | shape |
| --- | --- |
| `values/elab-111.qll` | `{ k = 2; rec down = fn(n) { … k + back(n - 1) … } and back = … }` |
| `values/elab-112.qll` | the same shape, one member |
| `values/rec-module-members.qll` | `module { pub rec loop = …; rec a = … and b = … }` — the module's own earlier slots are locals here |

`values/rec-returns-closure.qll` — a recursive function *returning* a lambda — closed with 1c-ii,
because the capture there belongs to the inner lambda, not to the group.

## Two shapes, and the decision

1. **One closure class per `Fix`**, built once at the definition site, with every member emitted
   as an instance method of it. Siblings then read each other through the same `object` field the
   environment already arrives in — `EnsureMember` pushes `Binding.Env(fixOwner, slot)` for a
   sibling instead of `Binding.MethodRef`, and the definition site materialises N delegates over
   one instance. Keeps the direct-call path for the case that needs it least, and makes
   `_fixMembers` a per-`Fix` record rather than a map.
2. **Thread the environment as one more argument** on every generated method. Cheaper to emit and
   it is what `docs/wayfinder/topics/dotnet-backend.md` records as the second candidate shape for
   captures at large, but it changes every call site and makes the arity of a generated method
   lie, which the prelude's closure-spine flattening relies on.

Shape 1 is the one 1c-ii already paid for, so it is the cheaper of the two now.

## Acceptance

`--parity`'s `captured local` reaches 0 with `differ` still 0, suite 967/0, xUnit 217 — and the
20M self-tail probe (`{ rec loop : I64 -> I64 = fn(n) { match (n) { 0 => 42, _ => loop(n - 1) } };
loop(20000000) }` → `42`) still `tail.`-prefixed in the emitted image, since this touches
`EmitCall`'s tail path if shape 2 is taken.
