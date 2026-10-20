---
title: A .NET backend — the two choices (ruled 2026-10-13)
labels:
  - wayfinder:topic
status: decided
---

# The two choices, and what decided them

**Ruled 2026-10-13, so this document is now the record rather than a question:**

- **Decision 1 — effectful code: option 1**, the machine only where the effect row needs it. Pure
  arrows emit plain IL methods with `tail.` at tail positions; effectful arrows emit a machine.
  Accepted limit: a tail call *inside* a machine consumes CLR stack, because `tail.` is illegal in
  a method that carries a state machine. F# made the same call.
- **Decision 2 — `Reflection.Emit` + `PersistedAssemblyBuilder`**, because `OpCodes.Tailcall` is
  the whole reason the pivot to IL is cheap, and `ILGenerator` computes `maxstack` and encodes
  instructions.

Read on for what each option was and why, which stays useful when a slice is revisited.

**Where things stand, in four lines.**

- The backend emits **IL now, not C#** — your ruling today, which supersedes the earlier
  "emit C# with Roslyn". The reason it is affordable: `ILGenerator.Emit(OpCodes.Tailcall)`
  writes the prefix C# has no syntax for, so F#'s coverage is reachable.
- **Why this cost is real:** F# gets unbounded recursion three ways and C# gets it one way. My
  probe, 20M deep on this machine: self tail → a loop (free, no `tail.`); mutual tail → a `tail.`
  prefix; a tail call through a function value → a `tail.` prefix inside the closure's `Invoke`
  method. With `--tailcalls-` the last two die at 300k with exit 134. C# writes zero prefixes.
- **`tail.` and state machines do not compose.** F# does not solve this; it turns `tail.`
  emission *off* inside its async path (`docs/runtime-async.md:53,358`). So decision 1 below is
  genuinely a fork, not a detail.
- **Nothing about the interpreter changes.** `Nbe.Eval` stays the elaborator's normalizer, the
  import evaluator, and the macro runtime. What the backend replaces is `Driver.Run`.

---

# Decision 1 — how effectful code is emitted

## The program the examples use

This one is verified against the interpreter, and I use it throughout:

```quill
{ effect Ask = sig { ask : I64 -> I64 };
  f : I64 ->{Ask} I64 = fn(x) { perform Ask.ask(x) + 1 };
  match (f(2)) { v => v, effect Ask.ask n => resume(n * 10) } }
```

`f` performs `ask(2)`; the handler resumes with `20`; `f` returns `20 + 1`. **It evaluates to
`21`.** The effect row on `f` is `{Ask}`; `f`'s *caller* is a `match` with a handler, not a
function.

Two real cases from the suite pin the hard corners, and both are listed so each option can be
judged against them:

- **`core-098` → 43** — the handler resumes from *inside a branch*, and performs again before
  resuming.
- **`core-049` → 5** — a continuation is **stored into a ref** (`q <- fn(u) { resume(()) }`) and
  never called. The continuation must therefore be an ordinary heap value, not a stack frame.
- **`ref-nbe-continuation-used` → `error`** — the *same* continuation is resumed twice, and that
  must stay a language error. This is the one-shot rule.

---

## Option 1 — machine only where the effect row needs it — **RULED, chosen**

Pure arrows become plain IL methods, with `tail.` at tail positions. Effectful arrows become a
machine whose fields hold what survives a suspension.

The machine for `f` (all snippets are shape sketches, not literal IL):

```
.class sealed auto $F_Ask
  .field int64 x            // live across the suspension -> hoisted
  .field int32 state        // 0 = not started, 1 = after ask, 2 = done
  .field int64 ret
```

Its step method switches on `state`, exactly like F#'s resumable code:

```
.method bool MoveNext(object resumed)
  switch (state)
    case 0:  stfld state, 1
             callvirt Ev.Ask_ask(int64 x, class Cont k)   // k IS this machine
             ret false                                    // the handler owns the run
    case 1:  <resumed + 1>;  stfld ret
             stfld state, 2
             ret true
```

Three things are worth noticing in those eleven lines.

1. **No stack is captured.** `perform` hands the handler *the machine itself* as the
   continuation. That is the whole reason effects fit .NET at all.
2. **`core-049` works**, because a machine is an ordinary object: storing it in a ref is just
   storing it.
3. **The one-shot rule is free.** `resume` re-enters at `state == 1` and moves it on; a second
   resume finds the state gone. `ref-nbe-continuation-used` stays an error without extra code.

The handler side:

```
// handler: effect Ask.ask n => resume(n * 10)
  <n * 10>;  callvirt $F_Ask.MoveNext
```

And the pure half of the program is untouched — a plain method, with the prefix F# uses:

```
.method static int64 $pure_loop(int64 n, int64 acc)
  ... tail. call $pure_loop ... ret
```

**Cost, stated plainly:** a tail call *inside* `f` is an ordinary call, so it consumes CLR stack,
because `tail.` is illegal in a method that also contains a state machine. Option 2 removes that
limit; F# never did.

**Why I recommend it:** the boundary is already in the types. The effect row is computed during
elaboration (`Term.Pi.Row`), and the interop is one-way — effectful code may call pure code, a
pure arrow can never call into an effectful one — so no dispatch or boxing is needed where the
two meet. Roughly a sixth of the `values/` cases are effectful; the rest keep the JIT's
optimizations, including the new `tail.`.

---

## Option 2 — machine where needed, plus a sentinel inside it

Same as option 1, and additionally an effectful function's *own* tail calls stop consuming CLR
stack. A tail call returns a sentinel instead of a frame:

```
.class sealed auto $Tail
  .field class Quill.Machine m
  .field object payload
```

one driver loop serves every machine and every tail-calling method:

```
while (r is $Tail t) r = t.m.MoveNext(t.payload);
```

**This is cheaper here than it would be in direct style**, because a machine already returns a
step result — the sentinel is one more kind of result, and the driver that runs machines is
already there to consume it.

**Cost:** one `$Tail` allocation per such tail call, and every function that can return a
sentinel must be called through the driver rather than with a direct `call`.

**Choose this if** "no quill program dies of recursion depth" is meant to be a *guarantee* rather
than an implementation property. F# did not: it gave up on tail calls inside its async path.

---

## Option 3 — universal state machine for all functions

Every function, pure or not, becomes a machine. A leaf like `add`:

```
.class sealed auto $add
  .field int64 a
  .field int64 b
  .field int32 state
  .method bool MoveNext()
    switch (state)
      case 0:  stfld state, 1
               <a + b>;  stfld ret
               ret true
```

A call site is then an allocation plus a driver step, with no `call` in the program at all:

```
newobj $add;  stfld a;  stfld b
call Quill.Driver.Run(class Quill.Machine m)
```

**What it buys:** one model. Tail calls, effects and `resume` become the same problem, every
function is automatically a continuation, and there is no tier boundary to police.

**What it costs:** it is the interpreter's own design — `Nbe.Machine`'s `Kont` dispatch, one
class per function — re-paid in IL. Nothing is ever a direct call, so the JIT cannot inline,
cannot keep a value in a register, and cannot devirtualize. Expect a small multiple over the
interpreter, not the 4000× that motivated the backend. It also leaves the `tail.` support you
just chose with no plain methods to prefix.

---

## Option 4 — one-shot continuation object plus a trampoline

Effectful code is emitted in continuation-passing style: the continuation is an **argument**, not
a captured stack.

```
.method static object $f(int64 x, class Cont k, class Ev ev)
  callvirt Ev.Ask_ask(int64 x, class Cont k)     // the handler decides
```

`resume` is then a call on the object:

```
// handler: effect Ask.ask n => resume(n * 10)
  <n * 10>;  callvirt k.Resume(object)
```

with a trampoline to keep the IL stack flat under CPS:

```
while (!k.Done) k = (Cont) k.Step(...);
```

**Why it is on the list:** it is closest to what the interpreter does today, `core-049`'s stored
continuation is natural, and `Cont`'s `used` flag expresses the one-shot rule directly.

**Cost:** one continuation object per call — not per suspension — and every signature inside an
effectful component changes shape, so the effect row's extent decides how much code is written in
this style. That propagation is what makes it heavier than option 1 in a language whose effect
rows are usually narrow.

---

# Decision 2 — which tool generates the IL

## Option A — `Reflection.Emit` + `PersistedAssemblyBuilder` — **RULED, chosen**

```
var il = mb.GetILGenerator();
il.Emit(OpCodes.Ldarg_0);  il.Emit(OpCodes.Ldc_I4_1);
il.Emit(OpCodes.Bgt, recurse);
il.Emit(OpCodes.Ret);
il.MarkLabel(recurse);
il.Emit(OpCodes.Tailcall);          // the line C# could not write
il.Emit(OpCodes.Call, mb);
il.Emit(OpCodes.Ret);
```

`OpCodes.Tailcall` is the entire reason this pivot is cheap. `ILGenerator` also computes
`maxstack` and encodes instructions, so ECMA-335 stays out of your way.
`PersistedAssemblyBuilder` (the .NET 9+ type) saves the assembly to disk, which is what
`quill build` will need.

## Option B — raw `System.Reflection.Metadata` PE writer

What F#'s own `ilwrite.fs` does (`:1928`, `if tl = Tailcall && cenv.emitTailcalls then
emitInstrCode codebuf i_tail`). Full control, no reflection-emit restrictions, artifact exactly
as designed.

```
var body = new BlobBuilder();
body.WriteByte(0xFE); body.WriteByte(0x14);   // tail.
WriteInstruction(body, OpCodeValues.Call, ...);
body.WriteByte(0x2A);                          // ret
```

Then method-body streams, exception tables, tokens, PE headers — none of it checked for you.
More code and more places to be subtly wrong, for a feature `ILGenerator` already covers.

---

# What today's pivot does to work already done

Honest accounting, because one fork's work largely dies here.

| artifact | fate |
| --- | --- |
| `src/Quill.Backend/Runtime.cs` (checked I64 arithmetic mirroring `Primitives.cs`, describe, depth guard) | **survives** — the emitted IL calls into it |
| `src/Quill.Cli` `quill run <file>` | **survives** |
| the parity harness (diffs backend output against the interpreter across the value cases) | **survives**, and is how the IL emitter gets measured |
| `src/Quill.Backend/Emitter.cs` — the C#-text emitter | **dies**; rewritten as IL emission |
| the Roslyn dependency and `CSharpCompilation` host | **dies**; no NuGet dependency left |
| the emitter's *decisions* (what each `Term` becomes, how `Fix` groups and closures are laid out) | carries over as design, not as code |
| the term-shapes survey | incomplete then — since written up in [the term-shapes topic](dotnet-backend-term-shapes.md), which 1a′–1c-i kept growing |

**What happened to the two "not yet"s above, recorded rather than left standing** (2026-10-20):
the fork's branch *was* read and run before merging — slice 1a′ landed 2026-10-17 with the
integrator's own build, xUnit and parity numbers — and the term-shapes doc exists; its *verified*
sections are the ones a dump backs, and the ones still marked *to dump* are listed there.

## One consequence worth naming before you decide

"Emit all IL" trades away the property that made C# look attractive: emitted code is **no longer
readable**. When a conformance case disagrees, the debugging story becomes "disassemble and read
IL" (or emit an ILAsm text side-file for the cases that disagree) instead of "diff the C#". That
is a real cost paid on every future emitter bug, and it is the reason a reviewer will ask why
Reference 1 and Reference 3 were not combined: Roslyn for the spine, IL only for the functions
that need `tail.` — rejected because Roslyn cannot fold an externally emitted method body into
its assembly, so that route is two assemblies and a cross-assembly call at every boundary.

---

# What I need from you

Nothing — both decisions are made and recorded above. The next step is a slice 1a' fork that emits
IL, salvaging `Runtime.cs`, the CLI and the parity harness from the dead Roslyn branch.
