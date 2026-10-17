---
title: A .NET backend — target, value model, and staging
labels:
  - wayfinder:topic
status: decided
---

# A .NET backend

Detail for [a .NET backend](../tickets/dotnet-backend.md). The ticket holds the question;
this holds the ruling and the evidence behind it.

## What is decided (2026-10-13)

1. **Target: emit C#, compile in-process with Roslyn.** `Core.term` → C# source text →
   `Microsoft.CodeAnalysis.CSharp` (`CSharpCompilation.Create` + `Emit(MemoryStream)`) →
   load → invoke the entry method. Not IL by hand, not `dotnet build` as a subprocess.
   Cost accepted: one dependency (`Microsoft.CodeAnalysis.CSharp`, already in the local
   NuGet cache), a second compiler in the process, and the emitted source's assembly
   identity is ours to manage.
2. **Value model: native .NET types, typed where the term carries a type.** `Atom.I64` is
   C# `long`; a nominal's constructors are generated classes; closures are real C#
   delegates/local functions. **Not** the evaluator's `Value` hierarchy.
3. **Acceptance of the first slice: `Backend.Run(elaborated)` beside `Driver.Run`, then
   diff the suite.** The 651 value-producing conformance cases run through both paths and
   the `VALUE` lines are compared. The interpreter stays the reference until parity is met.
4. **Recursion: direct style on the CLR stack, with a self-imposed depth guard.** Quill
   recursion is no longer bounded by memory but by the native stack — a deliberate
   language-semantics change, recorded below with the measurement that forced it.

**The interpreter is not deleted by this work.** `Nbe.Eval` is the elaborator's normalizer
(`Elaborator.cs:93`), it evaluates every import (`Loader.cs:130`) and every std unit
(`Prelude.cs:86`) at compile time, and it runs macros. What the backend replaces is the
*program execution* path — `Driver.Run`, and with it `src/Quill.Cli`'s reason to exist.

## The measurement the ticket was missing

The ticket said "compile time is unmeasured, so the cost being optimized is unknown". Measured
2026-10-13, on the merged tree, naive `fib`, one process:

| what | time |
| --- | --- |
| `fib(25)` through the interpreter (`--file`) | 1.76 s |
| `fib(30)` through the interpreter (`--file`) | **13.6 s** |
| trivial program `42` (startup + elaboration + prelude) | 0.54 s |
| the same `fib(30)` as plain C#, JIT'd (`-c Release`) | **3.4 ms** |

The 1.76 → 13.6 s step for 5.5x the calls is the superlinearity
[deep non-tail recursion](../tickets/deep-non-tail-recursion-is-superlinear.md) records; the
whole-program floor is 0.54 s, of which 0.19 s is `dotnet` startup. So the ceiling on any
backend's win here is four orders of magnitude, and the compile step starts from a 0.54 s
floor that a Roslyn compile (~0.2-0.4 s) does not dominate.

## Tail calls: the CLR has them, C# cannot emit them (probed)

The CLR does have a `tail.` IL prefix. What it does not have is any way for **Roslyn** to emit
it — C# has no tail-call guarantee, and no attribute or compiler flag asks for one. Measured
2026-10-13, .NET 10.0.112, x86-64, `-c Release`, one probe process per depth:

| shape | 200 k | 250 k | 280 k | 2 M | 20 M |
| --- | --- | --- | --- | --- | --- |
| self tail call `F(n, acc) => n==0 ? acc : F(n-1, acc+1)` | ok | ok | **stack overflow** | overflow | overflow |
| mutual tail `Even`/`Odd` | ok | — | — | overflow | overflow |
| non-tail `1 + F(n-1)` | ok | — | — | overflow | overflow |

The JIT did **not** convert even the textbook self-tail shape into a loop. A stack overflow is
exit 134 and uncatchable, so the failure is a process death, not an exception.

Consequences for the backend: (a) tail position must be recovered at emission if tail calls
are wanted later — either an explicit `while` loop when the call target is a known emitted
method, or a `tail.`-prefixed IL emitter as a second tier; (b) until then the depth guard is
load-bearing. The guard's ceiling must sit well under 250 k (that bound is for a two-argument
static method with a trivial frame; emitted quill frames are larger), and it must raise an
ordinary quill error rather than dying.

**Semantics change, stated plainly:** under the interpreter a quill program's recursion depth
was bounded by memory (the `Kont` stack is heap). Under the backend it is bounded by the CLR
stack. Nothing in the suite recurses deeper than 200 k (the deepest literal in any case is
`loop(200000)` in `elaborate/ref-budget-limit.qll`, and that is *elaboration*-time evaluation,
which stays on the interpreter), so no case is expected to flip — but the language's guarantee
is weaker afterwards.

## The finding that sequences the typed tier: `Core.term` is untyped

`Lam(Term Body)` (`Core.cs:19`) and `Ap(Term Fn, Explicitness, Term Arg)` (`Core.cs:20`) carry
no types, and no annotation table exists anywhere in `src/`. A fully type-directed emitter
therefore needs one of:

- **elaboration-time annotations** — the elaborator knows the type at every `Infer`/`Check`
  and can record a node → type side table. This is the real prerequisite;
- a type-recovery pass over `Core.term`, which is re-inference and therefore larger;
- staying dynamic.

What *is* recoverable from the term alone, verified by dumping `fib`: `Let(Type, Def, Body)`
keeps the written type, and a `rec` group is bound by such a let, so
`rec fib : I64 -> I64 = …` reaches the emitter as `Let{ Type = Pi{ AtomTy I64 → AtomTy I64 } }`.
`Con` carries its nominal and `Match` carries its decision tree.

**Staging consequence:** slice 1 emits typed signatures where the term carries one (annotated
`let`/`rec` bindings over atoms) and `object` elsewhere, and the annotation pass is filed as its
own ticket for the rest. This is a `shortcut:`, and it is why the first slice's speed number
will be below the 4000x ceiling.

## What the emitter's input actually is (dumped, not assumed)

Three facts from the elaborated `fib` that shape the emitter:

1. **The program term is wrapped in the prelude.** It is `Let{ Type = …, Def = Module{…}, Body = … }`
   where `Def` is the whole `std` module as a term — `BindingTerm`s, `Impl`s included.
2. **References into that module are de Bruijn indices.** `Var 23` is `<=` (a prelude
   *function*, defined in `lib.qll`), while `Var 60`/`Var 61` index past the module into the
   base context, where they are primitives (`+`, `-`). So a `Var` means "look in
   `Context.Environment`" — where the entry may be a primitive (`VNeutral` with `HPrim`), an
   evaluated closure (`VLam` over a `Closure(Environment, Term)`), an atom, or a struct/dict.
3. **`if` is not a term form.** `if ($c) {…} else {…}` is a syntax macro in `std/lib.qll:14`
   that lowers to `Match` with a `Destruct` tree over `True`/`False` — so the emitter consumes
   `DecisionTree`s, never `if`.

Therefore the emitter compiles a **(term, value-environment) pair**, not a term: its
environment is `Context.Environment` at the bottom, pushed to by each binder, and a `Var`
resolves either to a C# local (for a binder the emitter introduced) or to a *value* that must
itself be emitted — which for a prelude function is a `VLam` whose `Closure` holds a term and
the environment it was closed over. That is closure conversion over the value graph, and it is
the reason the prelude needs no special case: prelude functions, trait dictionaries and
structured records all arrive as values with terms inside them.

## Staging

Each slice is separately verifiable and leaves the previous one working.

| slice | contents | acceptance |
| --- | --- | --- |
| 1a | project, runtime, Roslyn host, `Backend.Run`; emitter for atoms, primitives, `Var`/`Let`/`Lam`/`Ap`, `Fix`; typed `long` where `Let.Type` says `I64` | `{ rec double : I64 -> I64 = fn(n) { n + n }; double(21) }` → `42` through the backend |
| 1b | `Match` over `DecisionTree`s, and prelude-function compilation from the value graph | `fib(30)` → `832040`, and faster than the 13.6 s baseline |
| 1c | `Con`/nominal/struct/record/tuple, `Open`, `Proj`/`Dot` | the `values/` match/struct/record population matches the interpreter |
| 1d | refs (`VRef`, `RefCell`), effects (`Perform`/`Tunnel`/`resume` continuations) | the 43 ref and 55 effect cases |
| 1e | traits/dictionaries, type-case, generative stamps | the remaining type-case cases |
| 1f | the elaboration-time annotation pass; typed emission everywhere | typed tier; `--backend dotnet` becomes the default in the runner |
| 2 | `quill run` / `quill build` in `src/Quill.Cli`; delete `Driver.Run` as a program path | the binary is the artifact |

## Open

- **Runtime type representation (slice 1e).** Because type-case executes (`DecisionTree.TypeSwitch`,
  `Nbe.Match.cs:93-120`) and generative identity is ref-cell reference identity
  (`Nbe.Generative.cs:79`), the typed tier needs a runtime descriptor for type values and a
  machine encoding for stamps. Named here; not designed yet.
- **Effects under direct style (slice 1d).** `resume` re-pushes a captured `Kont` slice
  (`Nbe.Effects.cs:120`) and is one-shot by choice. Direct-style alternatives are `async`/await
  state machines for effectful arrows only (`~>` pure versus `->{Log}` is statically known), or
  keeping a trampoline for effectful code. Undecided.
- **Whether the depth guard is a language rule.** Its ceiling is an implementation constant;
  if the language is to promise anything, the constant needs to be normative.
