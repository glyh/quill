---
title: A type value carries no constructor table, so `M.A.MkA` cannot be emitted
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: []
# Filed 2026-10-20 by the integrator when slice 1c-ii closed. It is the 1c-i shortcut
# meeting the structural tier: 1c-i dropped a nominal's captures and constructor table on
# the grounds that nothing in emitted code reads a type's identity; a type reached through
# a module value is exactly where that stops being true.
---

# A type value carries no constructor table

Emitted code has `Runtime.Nominal(string Name)` and nothing else:

```csharp
// shortcut: no captures and no constructor table, because nothing in emitted code
// reads a type's identity yet -- a match branches on a constructor's name, and a
// constructor value is built from the term. Type-case reads identity; that is 1e.
public sealed record Nominal(string Name);
```

`Emitter.EmitDot` resolves `T.C` **statically**, off a `NominalDecl` in scope (`NominalOf`), which
is what makes an enum declaration usable without evaluating anything. When the type arrives as a
*value* instead — `M.A` where `A` is a module member, `b(I64)` where `b` is a former returned from
a function — there is no declaration in scope, so the read falls through to
`Runtime.MemberOf(typeValue, name)`, which now refuses:

```csharp
Nominal => throw new NotImplementedException("not ported yet: backend constructor through a type value"),
```

Two cases, measured 2026-10-20 (`--parity` on the merged 1c-ii tree):

| case | shape |
| --- | --- |
| `values/elab-162.qll` | `module { pub rec A = enum { MkA(B), StopA } and B = … }`, then `M.A.MkA(M.B.MkB(M.A.StopA))` |
| `values/rec-enum-former-ignores-outer-name.qll` | `b(I64).X(3)`, a former returned from a function and applied |

Both were `backend Dot` refusals before 1c-ii closed; the *refusal kind* moved, the count did not.

## What it needs, and why it may belong to 1e

A constructor value in emitted code is built from the term (`EmitCtorValue`/`CtorSpine`), and a
payload-carrying one used as a *value* is already refused — **25 cases**, measured on the 1c-ii tree
as a `backend constructor `Name` as a value` bucket per name (`Node` 7, `MkA` 6, `Some` 4, `T` 2,
`MkE` 2, and `Bx`/`X`/`Leaf`/`A` 1 each): the emitter has no curried closure over the payloads
collected so far. Putting a constructor table on
`Runtime.Nominal` means emitting, per declaration, a constructor value per constructor: a
`Runtime.Con` for a nullary one and a curried delegate for the rest. That is the same object the
25 refusals need, so **the two should be done once**, and neither is 1c-ii's.

The captures 1c-i dropped are the other half: `enum#71` in a type-case needs the parameters it was
applied to, which is 1e's type-case anyway. So the honest order is 1e first, and this ticket is the
place that says why a type value is not merely "a name".

## Acceptance

`--parity`'s `constructor through a type value` reaches 0 and `backend constructor … as a value`
shrinks with it, `differ` still 0, suite 967/0, xUnit 217. `values/core-093.qll`/`core-094` (a
struct type passed as a value into a type-case) are the neighbouring cases and stay 1e's.
