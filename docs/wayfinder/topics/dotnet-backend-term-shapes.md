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

## To dump (the emitter's open questions)

Each of these needs the same treatment — the shape written down, and where the information the
emitter needs actually lives (the term, the decision tree, or `Context.Environment`):

1. A match with a constructor **payload binding** (`Some(x) => x`).
2. A runtime **type-case** (`values/core-072.qll`) — the tree, and where the type value comes
   from at runtime.
3. A tuple, and a `Proj`/`Dot` including the method-call fallback (`values/method-300.qll`).
4. A `struct`/record and a field access.
5. A ref (`values/written-param-type-ref-letbound.qll`).
6. An effect with `resume` (`values/core-098.qll`) — and specifically **where the handler and the
   captured continuation live in the term**, since the machine emission depends on it.
7. A trait operation resolving to a dictionary (`values/trait-generic-impl-two-bounds.qll`).
8. A recursive function whose body calls a **prelude function** (`<=`): the concrete `VLam` that
   `Var` resolves to, and whether its captured `Environment` is all prelude values.

Where a type is recoverable only from `Context.Environment` and not from the term, say so — that
is the finding that decides how much of the typed tier is reachable before the annotation pass
lands.
