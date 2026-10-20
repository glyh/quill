---
title: Compiling a prelude closure reaches a Nominal it cannot emit
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
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

The decl is `enum#37`, `declComplete=True ctors=[Some/1, None/0]` — that is `Option`. Captures are
`[Var 7, Var 0]`, so it is a type former applied to two terms, i.e. an `Option(…)` *type* appearing
inside a closure body rather than a runtime constructor value. `i64_to_bool`'s closure does not
carry one; `<`/`<=`/`min`/`max`/`abs` do. `Options`/`Lists`/`Functors` are ambient entries
(`std/stage2.qll` and friends), so the likely chain is one of those closures being pulled in.

## Next probe, in order

1. **Make the emitter print the offending term.** `Emitter.cs`'s default arm currently prints only
   `${term.GetType().Name}`; adding `:: {term}` (one line, as done for the reconnaissance above)
   and running `{ 5 <= 6 }` names the subterm directly. Revert the print once it has answered.
2. Follow that subterm up: is it a `Let.Type`, a `Pi` domain, or a value in a closure's
   `Environment`? The answer decides whether this is a small fix (skip a type position the emitter
   has no reason to emit) or the first real demand for 1c's nominal tier.
3. **Do not assume `Nominal` is unimplementable here.** A type former in a *type* position needs no
   emitted value at all; a type former treated as a *value* does. Those are very different
   amounts of work and step 1 distinguishes them.

## Gate

```
dotnet build -v q --nologo
echo '{ 5 <= 6 }' > /tmp/le.qll
dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/le.qll
#   expect: True      (Driver.Describe renders a VCon by NAME, Driver.cs:69-74 -- measured
#                      with the runner's --file, which prints "VALUE True")
#   today:  quill: not ported yet: backend Nominal :: Nominal { Decl = enum#37, Captures = [Var { Index = 7 }, Var { Index = 0 }] }
echo '{ 1 < 2 }' > /tmp/lt.qll ; dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/lt.qll     # want True, today the same Nominal
echo '{ min(3, 4) }' > /tmp/mn.qll ; dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/mn.qll   # want 3, today the same Nominal
echo '{ max(2, 9) }' > /tmp/mx.qll ; dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/mx.qll   # want 9, today the same Nominal
echo '{ abs(0 - 5) }' > /tmp/ab.qll ; dotnet src/Quill.Cli/bin/Debug/net10.0/Quill.Cli.dll run /tmp/ab.qll   # control: 5 today, must stay 5
```

Measured at filing (`4c04563`): `<=`, `<`, `min`, `max` and fib's `if` spelling fail with
`enum#37`; `abs(0 - 5)` already returns `5`, so it is the control rather than a second target.
The `match` spelling of fib already returns `832040`.

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
