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

1. **Target: emit IL, not C# source.** `Core.term` → IL, built with `System.Reflection.Emit`
   (`ILGenerator`) and saved with `PersistedAssemblyBuilder` (the .NET 9+ type that writes a
   dynamic assembly to disk). This **supersedes** the first answer of the day (emit C# and compile
   it in-process with Roslyn): `ILGenerator.Emit(OpCodes.Tailcall)` writes the prefix C# has no
   syntax for, and F#'s coverage of mutual and higher-order tail calls depends on exactly that
   prefix (measured below). The Roslyn dependency goes away entirely.
   Cost accepted: the emitted code is **no longer readable**, so an emitter bug is debugged by
   disassembling rather than by diffing C#; and ECMA-335 encoding is ours to get right (the
   generator does the encoding, not the stack depths).
2. **Value model: native .NET types, typed where the term carries a type.** `Atom.I64` is IL
   `int64`; closures are real methods plus generated closure classes. **Not** the evaluator's
   `Value` hierarchy. *Measured 2026-10-20, and this ruling was half wrong:* a constructor is not a
   generated class but a `Runtime.Con` record carrying its name and payloads — matching what the
   interpreter branches on (a name) rather than inventing a tag tier — and no closure class exists
   yet, because every generated method is static; a captured local is still refused (31 cases,
   [filed](../tickets/backend-captured-local.md)).
3. **Effectful code: a state machine only where the effect row needs it.** Pure arrows emit plain
   IL methods with `tail.` at tail positions; arrows carrying effects emit a machine whose fields
   hold what is live across a suspension, with an `int` state as the resume point and one switch
   at entry (F#'s resumable-code shape). Accepted limit: a tail call inside a machine is an
   ordinary call, because `tail.` is illegal in such a method. Detail and alternatives:
   [the two choices](dotnet-backend-choices.md).
4. **Recursion: direct style on the CLR stack where the row is pure, with `tail.` at tail
   positions and a depth guard elsewhere.** The guard exists for the shapes `tail.` cannot cover
   (a tail call inside a machine, and non-tail recursion).
5. **Acceptance of the first slice: `Backend.Run(elaborated)` beside `Driver.Run`, then
   diff the suite.** The 651 value-producing conformance cases run through both paths and
   the `VALUE` lines are compared. The interpreter stays the reference until parity is met.

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

### What F# does, measured on this machine (2026-10-13)

F# compiles to the same runtime and is the closest precedent, so it was measured rather than
recalled. Two mechanisms, not one, and `--tailcalls-` separates them:

| F#, 20 M deep | `tail.` ON | `tail.` OFF (scan: 0 prefixes) |
| --- | --- | --- |
| self tail | ok | **ok** — a loop, no `tail.` involved |
| mutual tail | ok | **overflow, 300 k** |
| higher-order: tail call to a function value that calls back | ok | **overflow, 300 k** |
| non-tail | overflow | overflow |

The IL scan of the ON build finds four `tail.` prefixes: `Even`, `Odd`, `ThroughValue`, and
`Invoke` — F# synthesizes the closure's `Invoke` and puts the `tail.` inside it, which is how a
tail call to an *unknown* callee stays unbounded. ROSLYN HAS NO SYNTAX FOR THAT CALL SITE.

Citations, from `dotnet/fsharp` (`src/Compiler/…`): loop conversion is `IlxGen.fs:4586`
(`// Look for tailcall to turn into branch`, `I_starg` back to the entry slots), restricted to
values in the same closure/method; everything else in tail position goes through `CanTailcall`
(`IlxGen.fs:4958-4988`) and the `tail.` prefix is written at `ilwrite.fs:1928`. It is
**best-effort, not a language guarantee** — no `tail.` inside `try`/`finally`, with pinned or
byref locals, or in a struct-arg call — and calls through function values are *assumed* critical
(`Optimizer.fs:4403`). `emitTailcalls` (`CompilerConfig.fs:809`, `--tailcalls-`) is read only by
the IL writer, which is why the self-tail loop survives the flag (measured above).

**So the general case is not reachable from C# source emission.** The two C#-expressible
substitutes are: a self-tail `while` loop (free, covers F#'s cheapest case), and a
trampoline/TailCall sentinel for tail positions whose callee is unknown (covers the other two,
at the price of a calling-convention change or a wrapper on every tail-calling function).
Real `tail.` — including F#'s `Invoke` trick — requires emitting IL, which is a second backend,
not a flag. **Open, to be ruled.**


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

## Emitting closures, and the two IL traps it cost (1c-ii, 2026-10-20)

A lambda that reads nothing from the enclosing method stays a **static** method — that is what
makes the prelude value graph, and its `tail.` prefixes, cost nothing. A lambda that reads a local
cannot: its body becomes an **instance method of a generated class** holding one `object[]` of
captured values (`Emitter.EnsureLambda`), and the delegate is built over that instance. Captures
keep their *positions* in the scope list (`Binding.Env` replaces the entry in place), so every de
Bruijn index, and every ambient index behind it, still means what the elaborator decided; that
invariant is what let the slice land without touching `Var` resolution.

Three things about the emitted tier that a reader needs before touching it: a lambda captures
**everything the enclosing method's scope holds**, not only the free variables its body reads
(cheap to emit, and a `shortcut:` in `EnsureLambda` says when to tighten it); a delegate stored in
the **object** tier keeps its own `Func<…>` shape from the point it was emitted, so
`Runtime.Apply` calls a non-`(object → object)` delegate through `DynamicInvoke`; and
`Runtime.Describe` must render a structural value with the **interpreter's** spelling
(`VModule`/`VStruct`/`VRecord`/`VProd`/`VAtom`), because `--parity` compares those two strings and
a case whose result is a module or a tuple would otherwise report a `differ` that is only a name.

Two things about writing that IL were not guessable and cost real time. Both are recorded because
1d's machines will build delegates too.

1. **`ldftn` on an instance method needs its `this` on the stack but does not consume it.** The
   correct sequence is Roslyn's, and it has **no `dup`**:
   `ldloc instance; ldftn instance M; newobj Delegate(object, native int)`. A `dup` (the reflex
   from `ldvirtftn`/`callvirt` shapes) leaves one extra item on the stack, which surfaces as
   `InvalidProgramException: Common Language Runtime detected an invalid program` **at the JIT of
   the *enclosing* method** — the stack trace names `Program.Entry`, not the closure. Grinding on
   the ordering of `TypeBuilder.CreateType` for a dependent type was a dead end: a minimal
   Reflection.Emit repro in `/tmp` that emitted the same seven instructions settled it in one run,
   and the IL the emitter had written was already textually identical to the answer.
2. **`PersistedAssemblyBuilder` does not care when a dependent type is created**, so
   "create the closure class first" is not a fix and "create it last" is not a bug. What it does
   care about is that every `DefineType` is created before `Save`; `Emitter.Build` creates the
   closure classes and then the enclosing type.

### Seeing what the emitter wrote

`QUILL_BACKEND_IL=<path> quill run <file.qll>` writes the emitted image out (`Emitter.cs`'s debug
hook). There is no `ildasm` on `PATH`, but this machine has one built from the runtime tree:

```sh
QUILL_BACKEND_IL=/tmp/p.dll dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run prog.qll
/home/lyh/pullground/runtime/artifacts/bin/coreclr/linux.x64.Checked/ildasm /tmp/p.dll
```

That, not a hypothesis about the term, is what shows whether a `tail.` survived, whether a local is
`int64` or `object`, and which `Runtime.*` helper a path actually calls — the `+`-emitted-as-`Sub`
bug of 1c-ii (an extra pushed field slot, see [the term-shapes topic](dotnet-backend-term-shapes.md))
was visible as one line of disassembly and invisible in the source.

## Staging

Each slice is separately verifiable and leaves the previous one working.

| slice | contents | acceptance |
| --- | --- | --- |
| 1a′ | project, runtime, `Reflection.Emit` + `PersistedAssemblyBuilder` host, `Backend.Run`; IL emitter for atoms, primitives, `Var`/`Let`/`Lam`/`Ap`, `Fix`; **plus the one `Match` shape a recursion probe cannot be written without** (below); `int64` typed where `Let.Type` says `I64`; `tail.` at the tail positions a `CanTailcall`-style check accepts | `{ rec double : I64 -> I64 = fn(n) { n + n }; double(21) }` → `42` through the backend, and a 20M-deep self-tail and mutual-tail pair that both survive. **Landed 2026-10-17:** all met, verified on the merged tree — `tail.` prefixes 1 (self) and 4 (mutual) in the emitted image, both 20M runs exit 0 |
| 1b | the rest of `Match` over `DecisionTree`s (payload bindings, nominal keys, `Destruct`, guards, non-base occurrences), and prelude-function compilation from the value graph. **Landed 2026-10-19 in two halves** (1b-ii `926eb49`, 1b-i `b416d65`), residue closed 2026-10-20 (`cfc55e7`); `Sequential` and `TypeSwitch` deliberately out. Detail: [the slice ticket](../tickets/backend-slice-1b.md) | `fib(30)` → `832040`, and faster than the 13.6 s baseline. **Met by both spellings as of `cfc55e7`:** the `match` one was already met before 1b started (measured 2026-10-19: 0.6 s backend against 7.2 s interpreted at `e1b951a`), and the `if` one returns `832040` in 0.81 s now that its `Nominal` residue is closed. Read the earlier history in the ticket — this table got the gate wrong twice |
| 1c-i | **the nominal tier**: `Runtime.Con` payloads, an enum declaration as a value *and* a declaration, `Term.Nominal`/`Term.Open`/`Term.Dot`/`Term.Con`, and a type (`VNominal`, `VAtomTy`) emitted as a value where a term demands one. **Landed 2026-10-20** — `--parity` 381 → 419 match, 140 → 178 value-match, 0 differ, `Nominal` refusals 44 → 0; `{ Color = enum { Red, Green }; open Color; match (Color.Green) { … } }` → 2, and payload-bearing matches (`elab-152` → 6, `core-107` → 42) run through the backend. Detail: [the slice ticket](../tickets/backend-slice-1c-i.md). This row's earlier claim — that a program-level enum declaration needs no nominal *value* — held for **payloads** and failed for **type values**: `F(b) = match (b) { True => I64, False => Bool }` used as a type really is a function returning a value |
| 1c-ii | **the structural tier, and real closures**: `Module`/`Struct`/`RecordConstruct`/`Prod`/`ProdTy`/`Proj`/`Dot` on a record/`Open` over a value/`Imported`, the emitted tier's `Runtime.Module`/`Struct`/`Record`/`Prod`/`ProdTy`/`Member`/`Atom`, `MatchOps`' remaining two members, a lambda that reads an enclosing method's local as an instance method of a generated `Closure_N` over an `object[]`, and a structural value that arrives through the ambient environment (an imported unit's module, a prelude module). **Landed 2026-10-20** — `--parity` 419 → 551 match, 178 → 310 value-match, 0 differ; every refusal this row listed is **0** (`Module` 115, `Struct` 41, `Imported` 37, `Prod` 9, `Dot` 8, `Proj` 5, `open of` 7), and `captured local` 31 → 3, the three being a `rec` over an enclosing local. Closure capture and the structural tier are one slice because each other's cases reach it: a struct method *is* a capturing lambda. Detail: [the slice ticket](../tickets/backend-slice-1c-ii.md), with its three residues filed — [rec over an enclosing local](../tickets/backend-rec-over-an-enclosing-local.md), [a type value has no constructor table](../tickets/backend-type-value-has-no-constructor-table.md), [the object tier forgets a delegate's shape](../tickets/backend-object-tier-delegate-shape.md) |
| 1d | refs (`VRef`, `RefCell`), effects: the machine for arrows whose row is non-empty, `perform` handing it the machine, `resume` re-entering at its state, one-shot enforced by clearing it | the 43 ref and 55 effect cases, including `core-098` (43), `core-049` (5) and `ref-nbe-continuation-used` (still `error`) |
| 1e | traits/dictionaries, type-case, generative stamps. **`Sequential` and `TypeSwitch` belong here, not to 1c** — measured 2026-10-20: the `Sequential` refusals are `match (T)` on a `Type` whose arms are `NominalHead`/`AtomType`/`StructType` type patterns (`values/core-078` … `core-096`). They were 21/3 then and are **36/8** after 1c-ii, which is what a bucket does as upstream walls fall | the remaining type-case cases |
| 1f | the elaboration-time annotation pass; typed emission everywhere | typed tier; `--backend dotnet` becomes the default in the runner |
| 2 | `quill build` writing the assembly `PersistedAssemblyBuilder` produces; delete `Driver.Run` as a program path | the binary is the artifact |

**How the table's refusal counts are measured.** `--parity` groups its refusals by message **up to
the term dump**, and `QUILL_PARITY_CASES=1` adds one line per refusing case (kind, path). It used to
group by the whole message, and since every message carried `:: <the entire elaborated term>`, the
histogram read as a list of one-case walls — the 1c-i and 1c-ii counts are the ones measured after
that fix, and "which programs are behind a bucket" is now one command rather than a briefing.

**Constructs that had no owner, and now do.** `Term.Module` and `Term.Imported` had **no emitting
case** in `Emitter.cs` and no row in the table, measured 2026-10-19 from the parity histogram
(`Module` 112 of 900 cases, `Imported` 37). 1c-ii claimed both and landed both, 2026-10-20: a
module is *emitted* (`Runtime.Module`, its bindings evaluated in order into locals that the later
bindings read), while the **prelude's** module is still *skipped* — `EmitProgram` strips the
`open (import "std")` wrapper and pushes its 38 opened members as the ambient environment, which is
why no program pays for evaluating the prelude at all. The two decisions do not conflict: the outer splice is
the ambient environment, anything a program can name is a value. `Term.InsertedMeta` still has no
case and its cases still present as `backend implicit application`, the largest bucket left (100).

**The match shape 1a′ carries, and why it is not 1b's.** `if (c) { … } else { … }` is not a term: it
is a syntax macro (`std/lib.qll:14`) that lowers to `Match` over `Bool`. So *any* terminating
recursion needs a `Match`, which means the slice's own acceptance test — a 20M-deep tail probe — is
unwritable without one. 1a′ therefore compiles exactly `Switch { At = Base }` with **literal
`Atom.I64` keys and leaf arms with no bindings** (`Emitter.cs:320-379`), and refuses everything else
with `not ported yet: backend decision tree`. Ruled 2026-10-17: keep it in 1a′, because the
tail-position arm emission it exercises is the subtle part and 1b would otherwise rewrite it; 1b
inherits it as tested rather than re-deriving it. The staging table originally got this wrong by
listing `Match` wholly under 1b while asking for a probe that requires it — recorded here so the next
reader does not re-litigate it.

Slice 1a (Roslyn, C# source) is **superseded before merge** — its `Runtime.cs`, CLI and parity
harness are salvaged into 1a′, and its C#-text emitter is dropped, since nothing would run it.

## Open

**What is open is what is left to build, not which way to build it**: slices 1d, 1e, 1f and 2
of the staging table, plus the depth-guard question below. The two choices this section used to
call live — the effect representation and the IL-writing route — were both **ruled 2026-10-13**
(sections 1 and 2 below); the option-by-option reading, with the emitted shapes and what each
cost, is kept in [the two choices](dotnet-backend-choices.md).

### 1. Tail-call strategy — ruled 2026-10-13: emit IL

Ruled: **the backend emits IL**, and `tail.` is written at tail positions the way F# writes it,
including inside the closure `Invoke` method F# synthesizes for a tail call to an unknown callee.
That supersedes emitting C# source with Roslyn, which is why slice 1a is re-cut as 1a′.

The alternatives that were weighed, kept for the record:

| option | covers | cost |
| --- | --- | --- |
| **self-tail `while` loop only** (today's slice 1a + a loop pass) | self tail | free; mutually recursive and higher-order tail calls stay stack-bounded |
| **TailCall sentinel + trampoline** | all three shapes | a wrapper/dispatch loop on every tail-calling function, and one allocation per unknown-callee tail call; C#-only |
| **an IL tier emitting real `tail.`** | all three, F#'s exact coverage | a second emitter (IL, not C#) — the C# assembly would have to be emitted as IL too, or post-processed |

### 2. Effects — ruled 2026-10-13: a machine only where the effect row needs it

Ruled: pure arrows emit plain IL with `tail.`; arrows whose row is non-empty emit a machine
(fields for what is live across a suspension, `int` state, one switch at entry), with `perform`
handing the handler the machine itself as the continuation and `resume` re-entering at its state.
One-shot falls out of clearing the state, so `ref-nbe-continuation-used` stays an error for free.
Accepted limit: a tail call inside a machine consumes CLR stack. Option-by-option detail, with the
emitted shapes and the four alternatives: [the two choices](dotnet-backend-choices.md).

### 2b. What was weighed for effects (record)

`resume` re-pushes a captured `Kont` slice (`Nbe.Effects.cs:120`) and is one-shot by choice, so
it fits a state machine well (nothing needs cloning). F#'s shipped shape is the reference and is
worth copying rather than inventing: **one struct machine per computation, `int` program
counters, a single `switch` on the resumption point at `MoveNext` entry, and locals hoisted to
machine fields only when they are live across a suspension** — `ResumableStateMachine<'Data>`
(`FSharp.Core/resumable.fsi:11-31`), the jump table at `LowerStateMachines.fs:934-946`, and
`__resumeAt` becoming a goto (`:602-620`). F# also **refuses** shapes rather than transforming
them: resumption inside `try/finally` or a handler is an error (`:693-696`, `:756`).

The decisive precedent for quill: F# did **not** compose the two mechanisms — the runtime-async
path sets `tail.` emission off rather than solving tail calls inside the machine
(`docs/runtime-async.md:53,358`; `IlxGen.fs:4971`). So this is a real fork, not a detail: either
the state machine is entered only where an effect row makes it necessary (leaving pure code
JIT-friendly), or it is universal and the 4000x ceiling is spent on it.

### 3. Whether the depth guard is a language rule

Its ceiling is an implementation constant; if the language is to promise anything, the constant
needs to be normative. It becomes moot if either option in (1) that covers the general case is
taken.
