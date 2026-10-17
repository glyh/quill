---
title: The elaborator records a type for every term it builds
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: []
---

# The elaborator records a type for every term it builds

## Question

`Core.term` is untyped: `Lam(Term Body)` (`Core.cs:19`) and
`Ap(Term Fn, Explicitness, Term Arg)` (`Core.cs:20`) carry no types, and no annotation table
exists anywhere in `src/`. A type-directed consumer therefore cannot ask "what is this
subterm's type" even though the elaborator knew it at every `Infer`/`Check` call.

Record it: an elaboration-time side table from term node to its type, exposed on
`Elaborated`.

## Why now

[A .NET backend](../topics/dotnet-backend.md) was ruled to emit **native** .NET types (`long`,
generated classes per nominal, real delegates) rather than the evaluator's `Value` model. A
typed emitter needs a signature for every lambda, and the term does not carry one. Measured
2026-10-13 by dumping the elaborated `fib`: the only types reachable today are the ones the
surface happened to write — `Let(Type, Def, Body)` keeps its annotation, so an annotated
`rec fib : I64 -> I64` arrives typed, while `(fn(x) { x + 1 })(7)` does not.

## What it touches

- **Every `Infer`/`Check` entry point** in `Elaborator.cs` and its partials: the type is in
  hand at each, so the recording is mechanical — but it must be *total* for the emitter's sake,
  and the binder-introducing cases (`Lam`, `Pi`, `Fix` members, `Struct` fields, `Impl` methods)
  are where a missed node is a hole rather than an error.
- **`Core.term` identity, not spans.** The table must key on node identity. Terms are records,
  so structural equality means two distinct occurrences of `Atom(I64 1)` are the *same* key;
  either key by reference or key by a path/index. This is the design question in this ticket,
  and it collides with the `EquatableArray` immutability rules.
- **The evaluator is unaffected** — this is compile-time information the backend reads; `Nbe`
  must keep working exactly as it does, and the suite must not move.
- **`Elaborated` gains a field** (`Driver.cs:23`), whose consumers are the runner and the CLI,
  plus the xUnit tests that construct or read it.

## Acceptance

- A test that walks a program's term and finds a recorded type for every node the emitter
  needs a signature for, with the unannotated-lambda case (`(fn(x) { x + 1 })(7)`) covered —
  that is the node today's term cannot answer.
- The full suite unchanged: 967 cases, 0 failed, xUnit green.
- A named answer to the identity question (reference versus path), written down, because a
  structural key over records silently collides.

## Sharpens when

A consumer needs a term's type that the surface did not write — today that consumer is the
backend's typed tier, and it is the only one.
