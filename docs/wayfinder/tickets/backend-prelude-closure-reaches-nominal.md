---
title: Compiling a prelude closure reaches a Nominal it cannot emit
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: closed
closed_date: 2026-10-20
resolution: Closed 2026-10-20, `cfc55e7`. **The dichotomy in this ticket's title was false: it is neither a type position to skip nor the first demand for a nominal value.** The probe (one line — the default arm's `:: {term}` was already there from the reconnaissance, so the line added was `Environment.StackTrace` and the emitter's own call arguments) showed that no program ever *names* `Option`. `<=`'s closure body is `Lam { Ap(Var 14, Ap(Ap(Var 45, Var 1), Var 0)) }` — `i64_to_bool(le_i64(x, y))`, whose callee `Var 14` sits at context level 50 when the body is read with 2 locals. `EnsureClosureMethod` pushed one parameter per `Lam` in `Arity` and then emitted `closure.Body` *with those Lams still on it*, binding each of them twice; with 3 locals, `Var 14` resolves at `63-1-11 = 51`, and level 51 of `<=`'s 63-wide closure environment is `Option` — a `VLam` over the closure whose body is the `Term.Nominal { enum#37 }`. The emitter was compiling the `Option` type former as a function. The same one-entry shift is in `{ 1 < 2 }` (`61-1-9 = 51`) and absent in `{ abs(0 - 5) }` (`77-1-26 = 50`, correct), because `abs` is arity 1 and has no leading `Lam` — the shift needed a binder chain, not a nominal. The fix is the strip the file already had a helper for: `Emit(LamChain(closure.Body).Body, …)`, one line. That alone took every case in this ticket's gate past the `Nominal`, and exposed the bug it had been hiding: `EmitSwitch` emitted `Ldc_I8 key; Bne_Un` against an object-tier (boxed-`I64`) scrutinee — not IL, and never equal, so every such switch ran its default arm. `i64_to_bool(0)` returned `True`, `{ 9 <= 2 }` returned `True`, `max(2, 9)` returned `2`, and `--parity` reported 3 differ the moment only the strip landed. Both are in `cfc55e7`; the switch fix boxes the key and compares with `object.Equals` when the slot is the object tier. Measured: suite **967 cases, 0 failed**, xUnit **217/217**, `--parity` **900 cases, 381 match, 0 differ, 519 not ported**, `real coverage: 140 value-match` (was 132), `Nominal` refusals **52 → 44** — all 44 now program-level type declarations, which is 1c's, and none of them a prelude closure. `{ 5 <= 6 }` → `True`, `{ min(3, 4) }` → `3`, `{ max(2, 9) }` → `9`, and fib's `if` spelling → `832040` in **0.81 s**. **Consequence for 1c:** nothing in the backend needs a nominal *value* yet; the remaining `Nominal` refusals are type positions (`Color = enum { … }` binds a term whose value is a type), so the first half of 1c that matters is skipping/representing a type declaration, not materialising `Value.VCon.Args`.
assignee:
blocked_by: []
# Split out of backend-slice-1b.md on 2026-10-19, which stayed open on it as its only
# residue. It is its own bug: 1b's two halves both landed and are verified, and this is a
# distinct hole in the closure-conversion path rather than an unfinished half.
---

# Compiling a prelude closure reaches a `Nominal` it cannot emit

Found 2026-10-19 while verifying slice 1b (merged `b416d65`). 1b-i's closure conversion
(`src/Quill.Backend/Emitter.Prelude.cs`) compiles a prelude `VLam` by emitting its
`Closure.Body` in the closure's own environment. For four programs that is as far as it
gets — the emitter's dispatch reaches a `Term.Nominal` and refuses it:

```
$ quill run '{ 5 <= 6 }'
quill: not ported yet: backend Nominal :: Nominal { Decl = enum#37, Captures = [Var { Index = 7 }, Var { Index = 0 }] }
```

Same failure, same `enum#37`: `{ 1 < 2 }`, `{ min(3, 4) }`, and the `if` spelling of `fib(30)`
(`{ rec fib : I64 -> I64 = fn(n) { if (n < 2) { n } else { … } }; fib(30) }`). Note `{ True }`,
`{ i64_to_bool(1) }`, `{ not(False) }`, `{ match (True) { True => 1, False => 2 } }` and
`{ if (True) { 3 } else { 4 } }` all work — the boundary is specific to the closures below.

## The Nominal is NOT in the program, and is therefore not 1c's

Measured, because the obvious reading of the message is wrong. A probe walking the elaborated
`Core.term` of `{ 5 <= 6 }` counts **zero** `Term.Nominal` nodes (and 0 for `{ 1 < 2 }`). So the
`Nominal` the emitter hits is reached only while **compiling a prelude closure body** — it is the
closure-conversion path's own hole, not 1c's general nominal tier.

The decl is `enum#37`, `declComplete=True ctors=[Some/1, None/0]` — that is `Option`. ~~Captures are
`[Var 7, Var 0]`, so it is a type former applied to two terms, i.e. an `Option(…)` *type* appearing
inside a closure body rather than a runtime constructor value.~~ `i64_to_bool`'s closure does not
carry one; `<`/`<=`/`min`/`max`/`abs` do. `Options`/`Lists`/`Functors` are ambient entries
(`std/stage2.qll` and friends), so the likely chain is one of those closures being pulled in.

**Two of those three sentences were wrong, corrected 2026-10-20 (probes below, fix `cfc55e7`).**
`Captures` is not a list of applied arguments — it is what the `enum` declaration captured in its
own scope — and no closure is "pulled in": `Option` is reached because the emitter resolves `<`'s
`Var 14` one entry too far, landing on `Option`'s `VLam`, whose *body* is this `Nominal`. The last
sentence is the only one that was true: `<`/`<=`/`min`/`max` all fail, `abs` does not, and the
difference is not which closures they call but **arity** — `abs` has no leading `Lam` to be bound
twice.

## The probe, and what it answered (2026-10-20)

Kept as the record of the guess, because two of its three branches were wrong in an instructive
way: the subterm was **not** a `Let.Type`, **not** a `Pi` domain, and **not** a value in the
closure's `Environment` either — the index the emitter used to *find* that value was off by one.
The stack at the default arm named the site in three frames (`EmitClosureCall` →
`EnsureClosureMethod` → `Emit`), and the closure bodies it printed named the callee: `Option`'s
body is a `Nominal`, and `<=` had no business calling it.

**Do not assume `Nominal` is unimplementable here.** Step 3's warning was right, and the answer
was the third option neither branch offered: the `Nominal` is reachable only through a wrong
`Var` index, so removing it needed no nominal code at all.

## Gate

```
dotnet build -v q --nologo
echo '{ 5 <= 6 }' > /tmp/le.qll
dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/le.qll
#   expect: True      (Driver.Describe renders a VCon by NAME, Driver.cs:69-74 -- measured
#                      with the runner's --file, which prints "VALUE True")
#   at filing: quill: not ported yet: backend Nominal :: Nominal { Decl = enum#37, Captures = [Var { Index = 7 }, Var { Index = 0 }] }
#   now (cfc55e7): True
echo '{ 1 < 2 }' > /tmp/lt.qll ; dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/lt.qll     # want True, now True
echo '{ min(3, 4) }' > /tmp/mn.qll ; dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/mn.qll   # want 3, now 3
echo '{ max(2, 9) }' > /tmp/mx.qll ; dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/mx.qll   # want 9, now 9
echo '{ abs(0 - 5) }' > /tmp/ab.qll ; dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/ab.qll   # control: 5 at filing and now
echo '{ 9 <= 2 }' > /tmp/neg.qll ; dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/neg.qll     # 2026-10-20: the switched-negation
                                                                                                           # half -- want False, and it was True
                                                                                                           # until the EmitSwitch tier fix
```

Measured at filing (`4c04563`): `<=`, `<`, `min`, `max` and fib's `if` spelling fail with
`enum#37`; `abs(0 - 5)` already returns `5`, so it is the control rather than a second target.
The `match` spelling of fib already returns `832040`. All pass at `cfc55e7`, together with the
switched-negation control the filing was missing.

Plus: suite **967 cases, 0 failed**, xUnit **217**, and `--parity` **differ 0** with a
value-match above the 132 baseline. Use `tools/Quill.DumpTerm` (`--ambient=N --values --depth N`,
or `--expand`); `--ambient` is the index space the emitter's `Var` actually uses, which `--env`
cannot reach past the context width.

## Related, and not this ticket

- **The closure-environment-prefix assumption** this path rests on is unproven in general — see
  the fog entry in the map. A non-prefix capture would silently read wrong ambient entries, and
  nothing currently checks for it.
- `MatchOps.Payload` reading `Value.VCon` is a latent copy of the seam bug fixed in `b416d65`;
  it is unreachable today only because `Runtime.Con` carries no payloads.
