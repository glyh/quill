---
title: The elaborated term as the backend's emitter receives it
labels:
  - wayfinder:topic
status: open
---

# The elaborated term as the emitter receives it

Companion to [the .NET backend](dotnet-backend.md). Dump a term with
`tools/Quill.DumpTerm`, which also prints `Context.Environment` as de Bruijn indices with each
entry's type. **There is no `quill-dump-term` command installed and the project is not in
`quill.slnx`** — this doc said it was until 2026-10-20, so the first reader to follow it got
"command not found"; the runnable form is

```sh
dotnet run --project tools/Quill.DumpTerm -- <file.qll> [--expand] [--depth N] [--env N] \
    [--ambient N] [--type] [--no-run] [--values] [--max-bytes N]
``` — the "what does `Var i` mean"
question, answered directly. `--ambient N` is the index space the emitter actually uses (the
program's own `open (import "std")` pushes its members first), which `--env N` cannot reach past
the context's width; `--values` descends into closure environments and is off by default because
that is what turns a dump into 1.7 MB.

**This document is deliberately partial.** It records what has been dumped and verified so far;
the sections below marked *to dump* are the emitter's open questions, not findings. Do not read a
missing section as "nothing to see there".

## Verified: the program is wrapped in the prelude, and `Var` indexes into the context

The elaborated term of any program is

```
Let { Type = <the prelude module's type>,
      Def  = Module { <the whole of std, bindings and impls> },
      Body = <the program> }
```

So a program's own de Bruijn indices start *past* the module. `quill-dump-term --no-values` on the
effect program below prints the base context in index order:

```
+            : level 10 = env[21] : VPi
eq_i64       : level 15 = env[16] : VPi
panic        : level 27 = env[4]  : VPi
Tuple        : level 30 = env[1]  : VPi
Std          : level 31 = env[0]  : VModule[40] partial=False
```

Consequences an emitter must respect, all confirmed against real terms:

- **A `Var` resolves by consulting `program.Context.Environment[i]`.** A `VNeutral` with an
  `HPrim` head is a primitive; a prelude-defined function is a `VLam` over a
  `Closure(Environment, Term)`; an atom is a literal.
- **Indices below the module's arity are prelude bindings, not primitives.** In the dumped `fib`,
  `Var 23` is the prelude's `<=` (a real function from `std/lib.qll`) while `Var 60`/`Var 61`
  index past the module and land on `-` and `+` in the base context.
- **`Std : env[0] : VModule[40]`** — the prelude is one module value of 40 entries, so the
  emitter never needs to emit the module itself: every reference into it arrives as a value it
  can compile (closure conversion over the value graph).

## Verified: an annotated `rec` reaches the emitter with a real signature

```
Let { Type = Pi { Domain = AtomTy{I64}, Codomain = AtomTy{I64} },
      Def  = Fix { Members = [FixMember { Name = "fib", Pure = true, Body = Lam { … } }], Index = 0 },
      Body = Ap { Fn = Var 0, Arg = Atom 2 } }
```

`Let.Type` is the signature the typed tier needs, which is why slice 1a can emit `int64` for a
`rec`-bound function while an unannotated lambda stays `object`-shaped. See
[the elaborator records a type for every term](../tickets/elaborator-records-term-types.md) for the
general case.

## Verified: `if` is not a term

`if ($c) { … } else { … }` is a syntax macro (`std/lib.qll:14`) that lowers to `Term.Match` with a
`Destruct` decision tree over `True`/`False`:

```
Match { Scrutinee = …,
        Bodies = [ <then>, <else> ],
        Tree = Destruct { At = Base { },
                          Cases = [ DestructCase { Name = "True",  Tree = Leaf { Branch = 0 } },
                                    DestructCase { Name = "False", Tree = Leaf { Branch = 1 } } ] } }
```

The emitter consumes `DecisionTree`s, never `if`.

## Landed: items 1, 2 and 8 (slice 1b, 2026-10-19)

**Item 8 — a prelude function's closure, and the `Lambda`/`Body` trap.** A prelude function is
`VLam(Closure(Environment, Body))` where **`Body` has already lost its outermost `Lam`**: `Nbe.cs`
builds it as `new Value.VLam(new Closure(env, l.Body))`. So the closure carries one parameter
implicitly and each further leading `Term.Lam` is one more — the emitted method's arity is
`1 + leading Lams`. `--ambient=34` shows `i64_to_bool` as `VLam` over `Environment[37]`, whose
entries are the `True`/`False` `VCon`s. A closure's `Environment` is an ambient environment in its
own right: it is a whole context chain, not a capture list, which is why the emitted method is
static and needs no closure object.

**Item 1 — a constructor match: the tree carries names, never tags.**
`{ match (True) { True => 1, False => 2 } }` is
`Destruct at=base cases[2] default=none` with cases `True`/`False`, arm bodies `Atom 1`/`Atom 2`.
`if (c) { a } else { b }` is **byte-for-byte this tree** with the two block bodies — `if` never
survives as a term (`std/lib.qll:14`). `Destruct.Cases` holds only the constructor's *name*, so
the emitter compares names, as `Nbe.Match.cs`'s `Cases.FirstOrDefault(c => c.Name == con.Name)`
does. There is no tag number anywhere in the tree. **Payload binders** live in
`Leaf.Bindings` as `Occurrence.Payload(parent, Constructor, Index)` — a leaf binding `Some(x)`
names the payload by constructor name and index. `Occurrence.Child` is `VProd.Items[i]`,
`Occurrence.Field` is `VRecord.Fields.Last(name == …)`.

**Item 2 — a runtime type-case.** `TypeSwitch` and `Sequential` are **still refused**, and they
are **1e's, not 1c's** — measured 2026-10-20: all 21 `Sequential` refusals are `match (T)` on a
`Type` whose arms are `NominalHead`/`AtomType`/`StructType` type patterns (`values/core-078` …
`core-096`), so both trees need the interpreter's type-case matcher (`Nbe.Patterns.cs`) plus a
runtime type. Not this slice.

### The seam this cost a merge, recorded so it is not re-made

**A constructor in emitted code is `Runtime.Con` (name and payloads), never `Value.VCon`.** `Value`
belongs to the evaluator and never appears on a running program's heap — emitted code runs in a
separately loaded assembly. `MatchOps` was written reading `Value.VCon`, which compiled, passed
xUnit, and failed **only** at run time on the cases that actually reach a `Destruct`
(`InvalidOperationException: a match reached a Con its type rules out`), because the two halves of
slice 1b were built in parallel and neither could observe the other. A helper class emitted code
calls into must also be **`public`** (`MatchOps` and `Runtime` alike): an `internal` one builds
fine and dies as a `MethodAccessException` inside the emitted program. All four copies of the tier bug
are fixed as of 1c-ii (`ConstructorName` in `b416d65`, `Payload` in 1c-i, `ProdItem`/`Field` in
1c-ii, which made them reachable by emitting a record and a tuple — `Quill.Kernel.Value` no longer
appears in `MatchOps` at all).

**Two more instances of the same family, 2026-10-20 (`cfc55e7`), and the rule they earn.** The
emitter meets the term at two places where a count or a tier can be off by one, and both failures
wore someone else's name:

1. **A binder count, not a nominal.** `EnsureClosureMethod` pushed one parameter per `Lam` in the
   closure's chain and then emitted the body *with those `Lam`s still on it*, binding each twice.
   One extra local shifted every ambient lookup one entry out — `<='s `Var 14` named `Option`
   instead of `i64_to_bool` — and the emitter reported `not ported yet: backend Nominal`, because
   `Option`'s value is a `VLam` over a closure whose body *is* a `Term.Nominal`. The refusal named
   a node the emitter had no business reaching, and the term was correct all along.
2. **A comparison at the wrong tier.** `EmitSwitch` emitted `Ldc_I8 key; Bne_Un` against an
   object-tier (boxed-`I64`) scrutinee. Not IL, never equal, so every such switch silently ran its
   default arm: `i64_to_bool(0)` → `True`, `{ 9 <= 2 }` → `True`, `max(2, 9)` → `2`.

Residual risk from fix 1, stated because nothing checks it: `Arity` counts *every* leading `Lam` in
the closure body as a parameter, so a body that begins with a `Lam` which is **not** one of the
function's parameters would now emit the wrong number of arguments and silently under-bind (before
it double-bound, which at least threw). No case in the suite or the prelude does this — a prelude
function's written parameter list is exactly its leading `Lam` chain — but an emitted-code
assertion that the method's arity matches the call site's argument count would be the cheap guard.

**The 1c-ii instances, and the rule they extend (2026-10-20).** Four more of the same family in
one slice, and the pattern is worth naming because the code looked right every time:

1. **An extra *scope entry* is the same failure as an extra parameter.** `EmitOpen` pushed the
   container local *and* its opened names, so `{ M = module { pub x = 42; pub y = 10 }; open M;
   x + y }` read `-` where `+` was written → `32`, and `{ M = module { pub z = 5 }; y = 1; open M;
   y }` returned a `Module` where an `I64` was expected. **An opened name is an entry; the thing it
   was read from is not.**
2. **A pushed field slot** (see *the structural tier* above) → `+` as `-`, `3` → `-1`.
3. **A cast that was never sound.** `Convert(object → Func<…>)` as a `Castclass` for *any* delegate
   shape: correct for a value this emitter boxed (a capture, which knows its own shape), wrong for
   the object tier's generic read. Splitting the two paths (`Exact` for captures, the universal
   shape only for `Convert`) turned the last `differ` into a refusal.
4. **A `dup` before `ldftn`** — not a scope error but the same "it compiles and only the runtime
   disagrees" shape; the recipe is in [the backend topic](dotnet-backend.md).

Every one of them was found the same way: **read the emitted IL**, not the C# emitter. Three of the
four were balanced, plausible source that no amount of staring at the term would have caught.

So: **a `not ported yet: backend <Node>` message is a claim about the node as the emitter has it,
not a measurement of the program**, and a wrong answer from emitted code (a `--parity` differ) can
be an index/tier mistake in the walk rather than a missing feature. Both were found only by
printing the emitter's own arguments — the closure bodies and the `Var`/local counts at the moment
of failure — which is cheaper than any hypothesis about what the term contains. Checks that would
have caught both: emit-time assertions that an ambient index is in range and that a `Bne_Un` has
matching stack tiers.


## Verified: the structural tier (1c-ii, 2026-10-20)

Four dumps, each reproduced with the command above. The emitter's shapes for them are
`Runtime.Module`/`Struct`/`Record`/`Prod`/`ProdTy` (`src/Quill.Backend/Emitter.Structs.cs`).

### A module, and what a binding list's slots are

`{ M = module { pub x = 1 }; M.x }`:

```
Let { type = Module bindings[2]          # #stamp (Private), x (Public)
      def  = Module bindings[2]          #   Atom ()  and  Atom 1
      body = Dot .x (of = Var 0) }
```

`M` is an ordinary `Let` binding whose value is a *value*: a module is evaluated like anything
else, so it is emitted like anything else. The whole prelude arrives as one more such module —
except that `EmitProgram` **strips the program's own `open (import "std")`** and pushes its 38
opened members as the ambient environment (the module itself has 40 entries), so no program pays for
them. Both facts are needed and they do
not conflict: the outer splice *is* the ambient environment, while anything a program can *name* is
a value.

Two details of the shape: every module's binding list starts with a `#stamp` binding — a `Private`
`Let` whose def is `Atom ()` — so the list is never empty and a constructor-only module's first
member is a `Unit` atom, which has no IL shape of its own (`Runtime.Atom`, and why
`Emitter.EmitAtom` has a non-`I64` case at all). And `Let.Type` holds a whole second `Module`/
`Struct` term: it is a *type position*, so the emitter reads shapes from it (`ShapeOfTerm`) and
emits only the `def`.

**An `open` inside a binding list contributes entries, not members.** `{ N = module { pub x = 1 };
M = module { open N; pub x = 2 }; M.x }` (→ `2`):

```
Let { type = Module bindings[2]   # #stamp, x      <- the type has 2
      def  = Module bindings[3]   # #stamp, Open { of = Var 1, members = [field x] }, x
      body = Dot .x (of = Var 0) }
```

The `def` has one more binding than its `type` has entries, and `x`'s `Var` index counts the
opened member — so a module body's binding loop pushes `open`'s members and adds nothing to the
member table. (I2 at work; `Nbe.cs`'s `Kont.ModuleOpen` leaves `Entries` alone.)

### A struct, a record, and the finding that cost a `differ`

`{ Point = struct { x: I64; y: I64 }; (Point{x = 1; y = 2}).x }`:

```
Let { type = Struct partial=False conFields=[x, y]          # field types
      def  = Struct partial=False conFields=[x, y]          # the same, as a value
      body = Dot .x (of = RecordConstruct { type = Var 0, values = [Atom 1, Atom 2] }) }
```

`Term.RecordConstruct` carries its struct term and the fields in written order, and a field read is
a `Dot` on the *record*, not on the type — so the emitted record keeps its type value (a member the
record has no field for is a method of that type; see below).

**A struct's constructor fields are NOT entries of the scope its bindings are written in.** The
dumped `{ C = struct { v : I64; pub method a(k : I64) { self.v + k } }; C.a(C{v = 1})(2) }` is
`Struct { conFields = [(value, I64)], bindings = [Let add (Method) = Lam (Lam (… `self.v + k`))] }`,
and inside those two `Lam`s `self` is `Var 1` and `+` is `Var 61` — i.e. the body's indices count
**`x`, `self`, and nothing for the field**. `Nbe.StartStruct`/`ResumeStructField` collect the field
*types* into the struct's entries without pushing them, and `Nbe`'s binding loop starts from the
enclosing environment; the elaborator agrees. Emitting a local per field and pushing it (which is
what `EmitContainer` did first) is off by one and gave `self.value + x` → `-1`, because one local
too many shifted `+` onto `-`: a wrong answer, not an exception. The field types *are* members of
the struct's value (`ModuleEntry.Field`, kind `Field`, whose value is the field's **type**) — they
are just not scope entries.

A method is `Lam{Lam{…}}` stored as a member of the struct **value**, and `Dot` on a struct type
returns that function unapplied, while `Dot` on a **record** whose fields do not include the name
applies the type's member to the record (`Nbe.cs`'s `Kont.DotOf`). Both directions are emitted:
`Runtime.MemberOf(record, name)` falls back to `Runtime.Apply(member of its type, record)`.

### A tuple and `Proj`

`(42, True).0` is `Proj { Index = 0, Of = Prod { Items = [Atom 42, Var 35] } }`, where `Var 35`
resolves to the prelude's `True` constructor (`field True` in the ambient environment); a tuple type
arrives as `ProdTy`. `Prod`/`ProdTy` are `Runtime.Prod`/`ProdTy`, and `Proj` is a read of
`Items[index]` — including when the tuple is a constructor payload reached through
`Occurrence.Child` (`values/core-113.qll`: `x + yz.0 + yz.1` → `6`).

### The three read rules, which are deliberately different

| read | rule | where |
| --- | --- | --- |
| a key expression `e.name` on a module or struct | the **last** visible member of that name (I3) | `Nbe.DotValue` |
| `e.name` on a record | the **first** field of that name, else the type's member applied to the record | `Nbe.DotValue` + `Kont.DotOf` |
| a *pattern* binding a field by name | the **last** field of that name | `Nbe.Match.cs:165-167` |

`MatchOps.Field` implements the third and `Runtime.MemberOf` the first two; using one rule for both
is a silent wrong answer on a record with a repeated label, which is why they are not merged.

### `import` inside a program is a value, not a splice to re-emit

`{ M = import "user"; M.r }` is `Let { def = Imported VModule[…] , body = Dot .r (of = Var 0) }`. A
unit's term is anchored in the base context (I5), so what crosses is its **value**: emitting the
splice is `EmitAmbient(value)`, and a `VModule`/`VStruct`/`VRecord`/`VProd`/`VAtom` that arrives
this way is emitted member by member (or item by item) at the point of use. That is 43 of the cases
1c-ii closed — the shapes were minefield-free, but they are *values*, so the tier they build must be
the emitted tier, not the evaluator's.

## Verified: the nominal tier (1c-i, 2026-10-20)

`{ Color = enum { Red, Green }; open Color; match (Color.Green) { Red => 1, Green => 2 } }`:

```
Let { Type = U,
      Def  = Nominal enum#71 ctors [Red/0, Green/0],
      Body = Open members=[ctor Red/0, ctor Green/0] of = Var 0
               Body = Match { Scrutinee = Dot { Of = Var 2, Name = Green },
                              Tree = Destruct at=base cases[Red, Green] } }
```

Three things this settles:

- **`open.Of` and `Dot.Of` are terms, not values**, and the emitter resolves them statically
  (`Var` → the `NominalDecl` its binder recorded; `Lam` chain → the `Nominal` at its end; `Ap` →
  its head). Nothing is evaluated to name a constructor, which is why a declaration can bind a
  *compile-time* entry and still work in a value position (materialised on demand).
- **`Term.Con` is the end of a payload constructor's lambda chain.** A constructor of arity *n*
  arrives as `fn(p0) … fn(pn-1) { Con(name, [p0 … pn-1], nominal) }`, and its parts are variables
  of that chain — so `Runtime.Con`'s `Args` are those arguments, in order.
- **A payload match binds by occurrence, and the tree still carries only names.**
  `match (Pair(10, Some(4))) { Pair(a, Some(b)) => a - b }` is a `Destruct at=base case Pair` whose
  child is `Destruct at=base.Pair[1]` with cases `None`/`Some`; the leaf bindings are
  `base.Pair[0]` and `base.Pair[1].Some[0]` — i.e. `Occurrence.Payload(parent, constructor, index)`,
  read at run time by `MatchOps.Payload`.

**And the shape that made `type T a = …` look unported**: a use site passes a former's type
parameter **implicitly**, even though the constructor's own payloads are explicit —

```
Ap (Explicit) { fn = Ap (Implicit) { fn = Some, arg = InsertedMeta 911 }, arg = 42 }
```

The implicit argument has no runtime value (it *is* the type parameter), so a saturated constructor
spine skips implicit arguments and matches the explicit ones against the declaration's payload
arity. Any other callee keeps `InsertedMeta` unported — `backend implicit application`, 89 cases,
mostly trait dictionaries.

Where a type is recoverable only from `Context.Environment` and not from the term, say so — that
is the finding that decides how much of the typed tier is reachable before the annotation pass
lands. Open questions still owing a dump: ~~(3) a tuple and `Proj`/`Dot` including the method-call
fallback~~ and ~~(4) a `struct`/record and a field access~~ — **answered 2026-10-20 (1c-ii)**, see
*the structural tier* above (`values/method-300.qll` included); (5) a ref
(`values/written-param-type-ref-letbound.qll`); (6) an effect with `resume`
(`values/core-098.qll`) — and specifically *where the handler and the captured continuation live
in the term*, since the machine emission depends on it; (7) a trait operation resolving to a
dictionary (`values/trait-generic-impl-two-bounds.qll`); (8's residue) ~~which `Term` inside a prelude
closure body carries a `Term.Nominal`~~ — **answered 2026-10-20 (`cfc55e7`)**: none does. `{ 5 <= 6 }`
reached `Option`'s type-former closure through a `Var` index one binder too high (see *the seam*
above); the remaining `Nominal` refusals were program-level type declarations, and 1c-i emitted
them, so the count is now **0**.
