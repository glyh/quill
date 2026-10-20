---
title: Backend slice 1c-i — the nominal tier
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: closed
closed_date: 2026-10-20
resolution: Closed 2026-10-20. `Runtime.Con` carries payloads, `MatchOps.Payload` reads the emitted tier, an enum declaration is a value and a declaration at once, its constructors are emitted from the declaration (not from a value), and a type is emitted as a value where a term demands one. Integrator's own run on the merged tree: suite **967 cases, 0 failed**, xUnit **217/217**, `--parity` **900 cases, 419 match, 0 differ, 481 not ported**, `real coverage: 178 value-match` (was 140), `Nominal` refusals **44 → 0**. Gates met: `{ Color = enum { Red, Green }; open Color; match (Color.Green) { Red => 1, Green => 2 } }` → `2`; the 1b residue's five programs unchanged (`5 <= 6` → `True`, `1 < 2` → `True`, `9 <= 2` → `False`, `min(3,4)` → `3`, `max(2,9)` → `9`, plus `abs(0-5)` → `5` and both `fib(30)` spellings → `832040`); payload-bearing matches through the backend: `values/elab-152.qll` (`Pair(a, Some(b)) => a - b`) → `6`, `core-107` (`type Option a = Some(a) | None`, `Some(42)`) → `42`, `elab-156` → `5`, `elab-161` (recursive former) → `4`.
assignee:
blocked_by: []
# Filed 2026-10-20 by the integrator, after 1b closed and its residue measured: all 44
# remaining `Nominal` refusals were program-level type declarations, i.e. 1c's. Split from
# 1c-ii (struct/record/tuple/Proj/Dot-on-record/Module/Imported) because none of those is a
# nominal and each blocks a different shape.
---

# Backend slice 1c-i — the nominal tier

Slice 1c of [the .NET backend](../topics/dotnet-backend.md)'s staging table. It is cut in two
because the table's 1c row mixed two independent things: **the nominal tier** (this ticket) and
**the structural tier** (`Struct`/`Prod`/`Proj`/`Dot` on a record, `Module`/`Imported`), which is
[1c-ii](backend-slice-1c-ii.md).

## What the term actually looks like (dumped, not assumed)

`{ Color = enum { Red, Green }; open Color; match (Color.Green) { Red => 1, Green => 2 } }`,
`Tools/Quill.DumpTerm`:

```
Let { type = U,
      def = Nominal enum#71 (ctors [Red/0, Green/0]),
      body = Open members=[ctor Red/0, ctor Green/0] of = Var 0
               body = Match { scrutinee = Dot .Green (of = Var 2),
                              tree = Destruct at=base cases[Red, Green] } }
```

So the slice needs four shapes, plus one value:

1. **`Term.Nominal`** — an enum declaration. Its value is a *type*, and the emitter does not need
   that value to name a constructor: the declaration is in scope. A `let` bound to one therefore
   contributes a **compile-time binding** (`Binding.Nominal`), and a value is materialised only
   where a term demands one.
2. **`Term.Open`** over a nominal — binds each constructor by name. The members are
   `OpenMember.Constructor`, and the declaration is resolved from `open.Of`.
3. **`Term.Dot`** on a nominal — `T.C`. Resolved statically off the declaration; a member of a
   record is 1c-ii.
4. **`Term.Con`** — the end of a payload constructor's lambda chain (`Nbe.Enum.cs`'s
   `ConstructorValue`): `fn(p0) … fn(pn) { Con(name, [p0 … pn], nominal) }`. The emitter reaches it
   through `EnsureClosureMethod`, so this is where `Runtime.Con`'s argument array comes from.
5. **Ambient values** — `VNominal` (`Bool`, `Option`), `VAtomTy` (`I64`), and `VCon` with payloads.

## The substantive finding: a constructor's type parameters are not runtime arguments

`type Option a = Some(a) | None` — the *natural* spelling of an enum with a parameter — compiles
`Some(42)` to

```
Ap (Explicit) { fn = Ap (Implicit) { fn = Some, arg = InsertedMeta 911 }, arg = 42 }
```

The implicit argument is the former's type parameter, and at run time it has no value. The emitter
refused it as `not ported yet: backend implicit application` — the message 89 cases still
produce, mostly for trait dictionaries — so it *read* like a 1e wall and was not one. **`CtorSpine` therefore skips implicit
arguments and matches the explicit ones against the declaration's payload arity**, which is the
erasure rule stated as a rule: a constructor's implicit parameters are exactly its type
parameters, and a `Con` is built from its name and payloads. Measured effect: 21 cases that would
otherwise re-block at `implicit application` now produce values, including every `type T a = …`
case in the suite.

Two more resolutions come with it, all of them `NominalOf`/`CtorOf` falling through a shape rather
than adding a case:

- a former **applied** to its captures (`Option(I64)`) is the same declaration as `Option`;
- a former **written inline** where `open`/`Dot` uses it — `Lam(Dot(Ap(Lam(Nominal), Var 0), "C"))`
  — is the same declaration again (`elab-156`);
- an enum **under** a lambda chain (`F = fn(A) { enum { … } }`, and the `rec` spelling) is a value
  *and* a declaration, so its binding carries both (`FormerOf`);
- a **prelude** constructor of a former (`Some`, from `Elaborator.Enum.cs`'s `ConstructorMember`) is
  the same `fn(A) { T(A).C }` shape with the former quoted into the term (`AmbientCtor`).

## Two traps

**1. `MatchOps.Payload` was the second copy of the tier bug, exactly as
[its ticket](backend-matchops-payload-wrong-tier.md) predicted.** It read `Value.VCon` while
emitted code produces `Runtime.Con`, so it was unreachable only because `Runtime.Con` had no
payloads; giving `Con` its `Args` array made every payload-binding match throw
`InvalidOperationException: a match reached a Con its type rules out` *inside emitted code*, on
cases that build and pass xUnit. Fixed in the same change and exercised through `--parity`
(`elab-152`, `core-107`, `elab-156`, `elab-161`), which is the only mode that can see it. The same
check applied to the rest of `MatchOps` found `ProdItem` and `Field` still reading the evaluator's
tier, unreachable until 1c-ii: filed, not fixed, on the same reasoning — see
[the sibling ticket](backend-matchops-record-wrong-tier.md).

**2. `Runtime.Nominal` drops the captures, and that is a limit with a name.** `enum#N captures[…]`
identifies a nominal applicatively (E11), so `Option(I64)` and `Option(Bool)` are different types
whose *emitted* values are the same record today. Nothing in this slice reads a type's identity —
a `Destruct` branches on a constructor's **name** (`MatchOps.ConstructorName`), and the interpreter
does the same (`Nbe.Match.cs`'s `SelectArm`) — so no case can see the difference. **Type-case
reads it, and that is 1e.** Marked `shortcut:` on `Runtime.Nominal`.

## Gates, measured on the merged tree

| gate | result |
| --- | --- |
| enum declaration + constructor match | `{ Color = enum { Red, Green }; open Color; match (Color.Green) { Red => 1, Green => 2 } }` → **`2`** (and `core-105`, `bare-constructor-pattern-after-open`, `selective-open-nominal-constructor`) |
| the residue's five programs | `{ 5 <= 6 }` → `True`, `{ 1 < 2 }` → `True`, `{ 9 <= 2 }` → `False`, `{ min(3, 4) }` → `3`, `{ max(2, 9) }` → `9`; also `{ abs(0 - 5) }` → `5`, `fib(30)` → `832040` by both spellings |
| payload-bearing matches, via `--parity` | `values/elab-152.qll` → `6`, `values/core-107.qll` → `42`, `values/elab-156.qll` → `5`, `values/elab-161.qll` → `4`, `values/enum-captures-payload-value.qll` → `3`, `values/elab-160.qll` → `1`, `values/elab-163.qll` → `1` |
| suite / xUnit / differ | 967 / 0 failed, 217 / 217, `differ` **0** |

| | before | after |
| --- | --- | --- |
| `--parity` match | 381 | **419** |
| value-match | 140 | **178** |
| not ported | 519 | **481** |
| `Nominal` refusals | 44 | **0** |

## What the "no nominal *value* is needed yet" reading got wrong

1b closed with "nothing in the backend needs a nominal *value* yet: 1c's first half that matters
here is representing or skipping a type declaration". Half of that held and half did not:

- **Right**: no case needs `Value.VCon.Args` reached through a *declaration*. Payloads arrive
  through `Term.Con` and through ambient `VCon`s, both of which are terms and values the program
  actually builds.
- **Wrong**: four cases (`values/written-param-type-value-indexed-ok`,
  `-other-branch`, `-mentions-earlier-value`, `values/pattern-pin-type-lowercase-alias`) demand a
  **type as a value** — `VAtomTy`/`VNominal` reached through a `Var` — because `F(b) = match (b) {
  True => I64, False => Bool }` is a *function returning a type*, so its body really is a value
  position. That is what "type formers as values where the term needs one" means concretely, and it
  is why `Runtime.AtomType` exists. Two of the four now produce values; the other two are behind
  `ref` (1d) and type-case (1e).

## Not in this slice, with the measured wall each is behind

Counts are this run's `--parity` histogram; the message a case reports first is a claim about the
tree, not about the slice that should own it.

- **`implicit application`, 89** — trait dictionaries (1e), plus type parameters where the callee
  is not a constructor (the erasure rule above is deliberately scoped to constructors, because a
  dictionary implicit is a real runtime argument).
- **`decision tree Sequential` 21 + `TypeSwitch` 3 — type-case, i.e. 1e, not 1c.** STATUS and the
  staging table both said these were "1c's entry points"; measured, every one of the 21 is
  `match (T)` on a `Type` (`values/core-078` … `core-096`, `pattern-*-type-case`), whose arms are
  `NominalHead`/`AtomType`/`StructType` type patterns. Corrected in both docs.
- **`Struct` 41, `Prod` 9, `Proj` 5, `Dot` on a record 7, `Module` 115, `Imported` 37,
  `open` of a module/sig 7** — the structural tier, [1c-ii](backend-slice-1c-ii.md).
- **`captured local`, 31** — a nested lambda reading another method's local, which needs a real
  closure object; not nominal and not structural, filed as
  [its own ticket](backend-captured-local.md).
- **`RefNew` 14, `RefSet` 2, `RefGet` 1** — 1d. **`EffectDecl` 45** — 1d. **`TraitRef` 34** — 1e.
- **`backend Pi` 3, `RecursiveOccurrence` 5, `PatternSynonym` 1, `Sig` 2,
  `conversion object -> Fn` 1** — other shapes, none of them nominal; listed so the next reader
  does not have to re-derive which slice they belong to (they do not have one yet). A `Pi` used as
  a *value* is the interesting one: its codomain is a term under a binder, so emitting it needs a
  closure the emitter does not build here.
- **`rec-enum-former-ignores-outer-name`** stays `not ported yet: backend Dot`: `b = F(2)` where
  `F`'s body is a block ending in a former, so the declaration is not recoverable without evaluating
  the block at emit time. It is also the one case whose *answer* depends on captures (`a(I64)` vs
  `b(I64)` are distinct E11 instances that the emitted tier conflates), so resolving it by name
  would hide the limit above rather than fix it.
