---
title: .NET backend — prelude value-graph term shapes (slice 1b-i)
parent: ../quill-design-map.md
labels:
  - wayfinder:note
status: open
---

# Prelude value-graph shapes (slice 1b-i)

Measured on this branch with `tools/Quill.DumpTerm` at `0e3b17e`. Folded into
`dotnet-backend-term-shapes.md` by the integrator; this file is only the prelude half.

## 1. A `VLam`'s `Closure.Body` has already lost its outermost `Lam`

`Nbe.cs:88` is `case Term.Lam l: value = new Value.VLam(new Closure(env, l.Body))` — the
closure holds the *body* of the lambda, not the lambda. So the closure carries one
parameter implicitly and each further leading `Term.Lam` in `Closure.Body` is one more.

`--ambient=34` (`i64_to_bool`) prints:

```
Ambient[34] of 70 (base context entries 32) =
VLam
  closure env =
  Environment[37]
  ...
  closure body =
  Match handler=0 effectBranches=0 patterns=[0 | _]
    scrutinee =
    Var 0 -> <unresolved index 0>
    tree =
    Switch at=base cases[1]
      case 0 =
      Leaf branch=0 bindings=[]
      default =
      Leaf branch=1 bindings=[]
    body[0] =
    Var 4 -> <unresolved index 4>
    body[1] =
    Var 3 -> <unresolved index 3>
```

`Var 0` is the implicit parameter; it is **not** shadowed by a `Lam` in the body. This is
why `{ i64_to_bool(1) }` threw `not ported yet: backend prelude value (chain 0 at Match)`
before the fix: arity was counted as the number of leading `Lam`s, which is 0.

**Rule:** `arity == 1 + (leading Term.Lam count of Closure.Body)`. `Var 0` of the closure
body is parameter 0; `Lam`-introduced binders start at index 1.

## 2. A closure's `Environment` is an ambient environment in its own right

`Ambient[34]`'s closure environment has 37 entries whose first six are
`VCon True, VCon False, VCon True, VCon False, VNominal enum#36, VAtom ()`. These are a
prefix of the same context chain the program's ambient is built from (global entry index
`37 - 1 - i`), not a captured frame. So a prelude closure's generated method is **static**
over its own `Scope(closure.Environment)` — no closure object and no environment argument
is needed. `EnsureClosureMethod` does exactly that.

## 3. Emitted constructor value shape (read this before using `Runtime.Con`)

`Runtime.Con` is a C# `record` with one property, `string Name`:

```csharp
public sealed record Con(string Name);   // src/Quill.Backend/Runtime.cs
```

`Runtime.Describe` matches `Con c => c.Name`, mirroring
`Driver.Describe`'s `Value.VCon c => c.Name` (`Driver.cs:72`). An ambient
`Value.VCon` emits `ldstr name; newobj Con(string)`, and `{ True }` runs to `True`.

No payloads: slice 1b-i only needs `Bool`, whose constructors are nullary. A
payload-carrying `Term.Con` or `Value.VCon` still throws
`not ported yet: backend constructor payload` — that is 1c's general nominal tier.

## 4. `{ 5 <= 6 }`'s body reaches a `Term.Nominal`

After closure conversion the `<=` chain compiles down to `i64_to_bool`, whose `Match`
bodies are the constructor terms. `{ 5 <= 6 }` now throws:

```
quill: not ported yet: backend Nominal :: Nominal { Decl = enum#37, Captures = [Var { Index = 7 }, Var { Index = 0 }] }
```

`enum#37` is `Option` (`std/bootstrap.qll`, `pub rec Option = fn(A : Type) { enum { Some(A), None } }`),
so the closure body being compiled is inside `i64_to_bool`'s *environment*, which spans the
whole bootstrap module — the nominal term is one of the ambient entries (index 7), not
anything the `<=` chain introduces. Emitting a `Term.Nominal` is the general nominal tier
that 1c owns; it is **not** blocked on Match and not on the decision tree.

`{ min(3, 4) }` separately throws `not ported yet: backend decision tree`, so the
`i64_to_bool` `Switch` gets past the emitter and then hits the `Match` half — report to the
integrator whether that `Switch` is the `Destruct` shape `s1bii` owns.

## 5. `Term.Var` of a prelude lambda as a bare value

`{ <= }` does not even elaborate (`unsupported prefix operator: <=`), so there is no
program that uses a multi-parameter prelude function as a *value* in this slice. The
`EmitClosureValue` path therefore only ever sees arity-1 closures; anything longer throws
`not ported yet: backend prelude value (arity N as a value)`.
