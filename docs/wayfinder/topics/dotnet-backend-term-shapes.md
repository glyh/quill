---
title: The elaborated term as the backend's emitter receives it
labels:
  - wayfinder:topic
status: open
---

# The elaborated term as the emitter receives it

Companion to [the .NET backend](dotnet-backend.md). Dump a term with
`tools/Quill.DumpTerm` (`quill-dump-term <file.qll> [--expand] [--depth N] [--env N] [--type]
[--no-run] [--no-values]`), which also prints `Context.Environment` as de Bruijn indices with each
entry's type — the "what does `Var i` mean" question, answered directly.

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

**Item 2 — a runtime type-case.** `TypeSwitch` (3 cases) and `Sequential` (14) are **still
refused**, deliberately: they need the interpreter's structural pattern matcher
(`Nbe.Patterns.cs`) and a type-case's runtime type respectively. Not this slice.

### The seam this cost a merge, recorded so it is not re-made

**A constructor in emitted code is `Runtime.Con` (name only), never `Value.VCon`.** `Value`
belongs to the evaluator and never appears on a running program's heap — emitted code runs in a
separately loaded assembly. `MatchOps` was written reading `Value.VCon`, which compiled, passed
xUnit, and failed **only** at run time on the cases that actually reach a `Destruct`
(`InvalidOperationException: a match reached a Con its type rules out`), because the two halves of
slice 1b were built in parallel and neither could observe the other. A helper class emitted code
calls into must also be **`public`** (`MatchOps` and `Runtime` alike): an `internal` one builds
fine and dies as a `MethodAccessException` inside the emitted program.


Where a type is recoverable only from `Context.Environment` and not from the term, say so — that
is the finding that decides how much of the typed tier is reachable before the annotation pass
lands. Open questions still owing a dump: (3) a tuple and `Proj`/`Dot` including the method-call
fallback (`values/method-300.qll`); (4) a `struct`/record and a field access; (5) a ref
(`values/written-param-type-ref-letbound.qll`); (6) an effect with `resume`
(`values/core-098.qll`) — and specifically *where the handler and the captured continuation live
in the term*, since the machine emission depends on it; (7) a trait operation resolving to a
dictionary (`values/trait-generic-impl-two-bounds.qll`); (8's residue) which `Term` inside a
prelude closure body carries a `Term.Nominal` — `{ 5 <= 6 }` reaches one and the program term
itself contains **zero** (measured with a probe), so it is the closure-conversion path's, not the
general nominal tier's.
