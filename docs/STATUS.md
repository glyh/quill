# STATUS — canonical current implementation snapshot

This is the **authoritative** status document for the `quill` compiler. When other docs disagree
with this file, STATUS.md wins.

**The implementation is the C# port in the repository root**: `src/` (compiler), `std/` (prelude
source), `test/` (the suite and its runners). The OCaml prototype was deleted on 2026-09-25,
when the port measured as a superset of it (`port-fails: 0` over every program in the repo). The
entries from 2026-09-16 onward describe waves of porting work and remain accurate as history; the
entries that describe the prototype's own implementation are its record, and its code lives in
`git log`. Paths written in a dated entry are the paths of that date — the port was lifted from
`dotnet/` to the repo root on 2026-09-26.

Last updated: the IL backend's slices 1a′, 1b and 1c-i are landed and measured — 967 cases, 0
failed, xUnit 217, parity 0 differ — 2026-10-20.

---

## Completed

### The .NET backend — slices 1a′ and 1b (2026-10-19)

The compiler has a second execution path: `Core.term` → IL, built with
`System.Reflection.Emit` + `PersistedAssemblyBuilder` (`src/Quill.Backend/`), driven by
`src/Quill.Cli`'s one command, `quill run <file>`. **The interpreter remains the reference** —
the conformance runner's default and `--file` modes call `Driver.Run` — and `quill build` does
not exist yet.

- **Slice 1a′ (2026-10-17)** — `Backend.Run`, the emitter for atoms/primitives/`Var`/`Let`/
  `Lam`/`Ap`/`Fix`, `int64` typed where `Let.Type` says `I64`, and real `tail.` prefixes: a
  byte-scan of the emitted image finds 1 (self) and 4 (mutual) occurrences, and 20M-deep self and
  mutual tail probes both exit 0. Slice 1a (Roslyn, C# source) was superseded before merge and
  its `Runtime.cs`, CLI and parity harness were salvaged.
- **Slice 1b (2026-10-19, two halves `926eb49` + `b416d65`)** — the rest of `Match` over
  `DecisionTree`s, and the prelude value graph: `Runtime.Con`, `EmitVar`'s ambient fallthrough,
  and closure conversion over `Closure(Environment, Term)`. `Sequential` (14 refusals) and
  `TypeSwitch` (3) remain deliberately out.
- **Measured on the merged tree** (the integrator's own run, not a fork's): suite **967 cases, 0
  failed**, xUnit **217/217**, `--parity` **900 cases, 381 match, 0 differ, 519 not ported**
  (`real coverage: 140 value-match`), `decision tree` refusals **106 → 17**. `{ True }`,
  `{ i64_to_bool(1) }`, `{ not(False) }`, a constructor match, `{ if (True) { 3 } else { 4 } }`
  and `fib(30)` in its `match` spelling (`832040`, **0.59 s** against **6.70 s** through the
  interpreter on this machine) all run through the backend. `{ 5 <= 6 }` → `True`,
  `{ min(3, 4) }` → `3`, `{ max(2, 9) }` → `9` and fib's `if` spelling → **`832040` in 0.81 s**
  as of `cfc55e7`.
- **The 1b residue closed 2026-10-20 (`cfc55e7`), and it was neither of the two things the probe
  was asked to choose between**: not a type position to skip, not the first demand for a nominal
  value. `EnsureClosureMethod` bound one parameter per `Lam` in a closure's chain *and* emitted
  the body with those `Lams` still on it, so each was bound twice and every ambient index in the
  body read one entry too far out — from `i64_to_bool` to `Option`, whose type-former closure the
  emitter then tried to compile, hence the `Nominal`. One line (`LamChain(...).Body`; the helper
  was in the file, unused). It exposed a second bug it had hidden: `EmitSwitch` compared an
  object-tier (boxed-`I64`) scrutinee with `Ldc_I8 … Bne_Un`, which is not IL and never equal, so
  every such switch ran its default arm — `i64_to_bool(0)` was `True`, `{ 9 <= 2 }` and `max(2, 9)`
  were wrong. Fixed in the same commit; it is why the two could not land apart, since the strip
  alone turned 3 suite cases into `differ`s. Detail:
  [compiling a prelude closure reaches a Nominal it cannot emit](wayfinder/tickets/backend-prelude-closure-reaches-nominal.md).
- **All 44 remaining `Nominal` refusals are program-level type declarations** (`Color = enum { … }`
  binds a term whose value is a type), which is 1c's, and none of them is inside a prelude closure.
  So nothing in the backend needs a nominal *value* yet: 1c's first half that matters here is
  representing/skipping a type declaration, not materialising `Value.VCon.Args`.
- **Slice 1c-i, the nominal tier (2026-10-20)** — `Runtime.Con` carries payloads and
  `MatchOps.Payload` reads the emitted tier; an enum declaration is a value *and* a declaration, so
  its constructors are emitted from the declaration; `Term.Nominal`/`Open`/`Dot`/`Con` are
  emitted; and a type is emitted as a value (`Runtime.Nominal`, `Runtime.AtomType`) where a term
  demands one. Measured on the merged tree: suite **967 cases, 0 failed**, xUnit **217/217**,
  `--parity` **900 cases, 419 match, 0 differ, 481 not ported**, `real coverage: 178 value-match`
  (was 140), `Nominal` refusals **44 → 0**. Gates: `{ Color = enum { Red, Green }; open Color;
  match (Color.Green) { Red => 1, Green => 2 } }` → `2`; the 1b residue's five programs unchanged;
  payload-bearing matches through the backend (`values/elab-152.qll` → `6`,
  `values/core-107.qll` → `42`, `values/elab-156.qll` → `5`, `values/elab-161.qll` → `4`). The
  slice's one semantic rule: **a constructor's implicit parameters are exactly its type
  parameters**, so a saturated constructor spine skips implicit arguments and matches the explicit
  ones against the declaration's payload arity — without it, every `type T a = …` enum's
  constructors re-block at `backend implicit application`, which reads like a 1e wall and is not.
  `Runtime.Nominal` drops the captures on purpose (nothing reads type identity but type-case, which
  is 1e). Detail:
  [the slice ticket](wayfinder/tickets/backend-slice-1c-i.md).
- **Not built** — the typed tier (only `Let.Type`'s written annotations are used; the
  elaboration-time annotation table is
  [still owed](wayfinder/tickets/elaborator-records-term-types.md)); the structural tier —
  `Struct`/`Prod`/`Proj`/`Dot` on a record/`Module`/`Imported`
  ([1c-ii](wayfinder/tickets/backend-slice-1c-ii.md), which now owns `Module` 115 and `Imported`
  37, the two largest first-failure buckets); a nested lambda reading another method's local
  (`captured local`, 31, [filed](wayfinder/tickets/backend-captured-local.md)); and `quill build`.
  Two gaps are filed rather than fixed: [`MatchOps.Payload`](wayfinder/tickets/backend-matchops-payload-wrong-tier.md)
  read the evaluator's tier (**closed** 2026-10-20, and checking the rest of the class found
  [`ProdItem`/`Field`](wayfinder/tickets/backend-matchops-record-wrong-tier.md), still latent until
  1c-ii). [`--parity` exits 0 on a differ](wayfinder/tickets/parity-exits-zero-on-differ.md) is
  **closed** (2026-10-20): the mode now exits 1 the moment `differ > 0`, so it can be a gate.

### The rest of the session's landings (2026-09-28)

After the pattern-grammar work in the entry below, same day:

- **The arity-2 deferred-call crash is gone** (`b6ca5ff`). A recursive call applied more than once
  whose fixpoint was deferred cored the compiler — `exit=134`, a 22,619-line
  `Quote → QuoteStuckMatch → OpenArm → Eval` chain — whenever an impl's evidence was quoted, which
  is every structural list comparison. `Value.VGlued` now carries the whole argument **spine**, so a
  deferred call applied again stays deferred (supplying an argument is not an *inspection*), with no
  ceiling, no refusal, and no `Kont` frame removed. It was the last blocker on the two impls below.
- **`Eq(List(a))` and `Eq(Option(a))` ship**, with `export Lists.{list_eq}` and
  `export Options.{option_eq}` and nothing else from those units: a no-import program resolves
  `Eq(List(I64))` from base scope while a sibling name (`map`, `get_or`) stays unbound. That was the
  selective export's first real use.
- **`open M.{a, b}`** (`ab95ccb`) — the mirror of the selective `export`. It carries every member
  kind (value, constructor, named impl, role, macro) and supplies **only** what is named, which
  means listing an impl does not drag its trait's name in. `Syntax.Open`/`Binding.Open` gained the
  selection field, so the two forms are symmetric in the AST, and `export M.{…}` stopped rejecting a
  unit's role or macro along the way.
- **A unit-level `rec` with written implicit binders works** (`88f862f`), so the non-recursive
  wrapper `std/list.qll` had been forced to write around it is gone. Two carries in the same family
  followed: an impl's `Vars`/`Bounds` now survive readback and rebuild (`65c1e39`), including the
  renaming route (`51ae973`).
- **A struct's type is its fields.** `IsTypeLike` asks only `Kind == Field` entries to be type-like
  — a `pub` method's payload is its *definition*, which was the whole refusal (`bf03e95`) — and a
  type position takes the *signature* side of a struct while a value position keeps definitions
  (`46b7212`). Matching compares fields, so a public method no longer hides a record from a
  field-naming head, while equality still sees every shown member.
- **The last live unported path is closed** (`f69ed8f`): reflecting a pattern-synonym use that
  supplies its type arguments. What the unported-path audit still lists are paths no program reaches.
- **The suite**: three byte-identical case files deleted, and five coverage-gap cases added — each
  one proven by removing the guard it names and showing the case fail. 947 cases, 0 failed. The
  measurements behind that live in
  [the suite investigation](wayfinder/tickets/suite-redundancy-measured.md); the short version is
  that 947 cases take 5.6 s, so slowness is the harness: build once, then `--no-build` and the built
  DLLs, and the CLI REPL for ad-hoc programs instead of a process each.

### The pattern grammar's two rules, and five pattern forms (2026-09-28)

Decided in one grilling session on 2026-09-27 and landed as four changes the next day, each
measured against the suite before the next was merged. The rulings, the probes and the check
sites live in
[a pattern binder is lowercase; naming an existing term takes `^`](wayfinder/tickets/pattern-binders-are-lowercase-and-references-are-pinned.md).

- **Case decides a pattern name's role.** A bare lowercase name binds, a bare uppercase name
  refers, and an uppercase name that resolves to nothing is an error — scope no longer decides.
  A trait declaration takes an explicit lowercase parameter
  (`trait Eq(a) = sig { eq : a -> a -> Bool }`) and an impl head's free name must be lowercase
  (`impl Size(Option(a))`); function and type-former binders stay uppercase, because they
  *declare* and referencing them is what makes an enclosing-binder pattern writable. Retires a
  silent typo: `impl Size(Optoin(A))` used to become a generic impl over fresh variables.
- **`^name` pins an existing term.** Surface syntax only — a resolved uppercase reference and a
  pin are the same internal thing (a term in a pattern position, tested by convertibility) — and
  it is *required* where the case rule would otherwise bind a lowercase name or read an uppercase
  *value* binding as a constructor. A pin to a compile-time-known atom contributes that value to
  exhaustiveness; otherwise it covers nothing. New capability: `match (v) { Some(^x) => … }`
  matches an outer value, which was impossible before.
- **Type patterns gained arrows, universes and tuple types.** `I64 -> Bool` matches an
  *explicit, non-dependent* Pi only — a dependent codomain is refused rather than bound to one
  arbitrary instance — while `[a] -> b` matches an implicit Pi with its binder nameable, so
  `[k] -> k -> k` matches `[k : Type] -> k -> k` with the codomain matched as a pattern. `Type`
  and `(a, b)` / `Tuple(2, a, b)` are patterns too. `a -> b` deliberately does not match a
  polymorphic type.
- **An unreachable arm is an error** (rule 8: coverage by a variable or wildcard reports, with no
  value reasoning). `values/core-133` moved from `0` to `error`, the cost the ruling accepted;
  Stage 12's non-fatal channel turns this into a warning, and `values/stuck-match-pruned-arm` was
  rewritten to keep its subject — a stuck match reading back — without the arm that is now
  illegal.
- **Matching compares a record's *fields*, not its members.** `impl Size(struct { a : I64 })` now
  serves a record that also carries a public method or a public binding; previously such a member
  silently hid the type from every head naming its fields. Equality is unchanged
  (`x : P = U{a = 1}` is still refused) and an extra *field* still needs the opt-in
  `struct { a : p; _ }`, which is decided but not built yet.

Suite at the end of it: **903 cases, 0 failed**; xUnit **206**.

### A written parameter type captures nothing (2026-09-27)

- A meta inserted while elaborating a written parameter type listed every binder in
  scope in its spine. At a call each spine entry must invert to a variable, but an
  earlier parameter (like a method's `self`) is a value, so
  `f = fn(a : I64, r : Ref(I64)) : I64 { a }; x = ref(40); f(0, x)` refused with
  `a meta's spine argument is not a variable`. The shape worked or failed on *how* the
  argument was made — a λ parameter inverted, a literal or a let-bound value did not.
- `Context.WithoutSelfInMetas` (the method ticket's fix) is generalised to
  `Context.NoCapture`: while a written parameter type is elaborated every entry is
  `Defined`, so the type's metas abstract over nothing bound before them and a call's
  values still invert. All three method sites (`MethodType`'s result, `MethodBody`,
  `Params`) and both `Syntax.Lam` sites (`InferLam`'s written domain and the
  Lam-against-`Pi` check) route through it.
- The dependence that matters survives: the value-mentioning `Ref(F(b))` works, and
  `f(True, True)` for `f = fn(b : Bool, v : F(b)) : F(b) { v }` is refused at the same
  type mismatch as before. 11 conformance cases added (ten values, one `error`) —
  **`conformance: 876 cases, 0 failed`** — xUnit **`188/188`**.
- Fixed 2026-09-28 (was a separate ticket): a struct former with its type argument supplied
  in a written parameter type, `fn(o : Box[I64]) : I64 { o.v }`, elaborates — a struct read
  in type position now takes its signatures (`TypeOfExpr` re-quotes the struct `InferStruct`
  reports), while the evaluated struct keeps member *definitions* for the runtime
  (`struct-former-in-written-parameter-type.md`).

### A generic impl's head variable takes its bound (2026-09-27)

- The head half landed first as `trait-op-takes-innermost-impl` (2026-09-27): an impl
  whose head is a pattern over types registers and serves its instances, its free
  names bound as its own type variables. The **bound** was missing: a free name in an
  impl head is a parameter of the impl, and the evidence its body uses for that
  parameter is now an implicit argument of the impl, resolved from scope when the impl
  is selected — exactly as a call to a bounded function resolves its hidden
  dictionaries (`traits.md`, "Resolution").
- `impl Size(Option(a))` with a body that calls `Size.size` on its element therefore
  works: the impl's dictionary is a function of the `Size(a)` dictionary, and
  `Size.size(Some(5))` resolves `Size(I64)` at the use. A demand on anything but a bare
  head variable (the impl's own container, or another trait at a structured type) is
  left where it was made, so it fails at the impl's **definition** naming the missing
  evidence — never as a misleading use-site error. This is what `derive` rests on.
- Measured on the gate: `std/list.qll` carrying a non-recursive
  `pub impl probe : Eq(List(A))`, `export Lists.{probe}` in `std/stage2.qll`, a
  no-import program comparing two lists answers `VALUE True`, and `{ 1 }` stays
  `VALUE 1`. Both `std/` edits were reverted; `std/` is unchanged. The recursive body
  a real structural equality needs is still blocked by
  [a recursive helper matching two lists cores](wayfinder/tickets/recursive-match-on-two-lists-cores.md),
  so [Eq for List and Option](wayfinder/tickets/std-eq-for-list-and-option.md) waits on
  that one alone.
- 8 conformance cases added (the bound used in the body, with two head variables,
  through `open`, `export` and an import, and two definition-site errors) —
  **`conformance: 865 cases, 0 failed`** — and one xUnit test pinning the
  missing-evidence message, **`188/188`**.

### The std library's public surface (2026-09-27)

- `std`'s library layer is the surface a program just uses, reached by qualification:
  `Std.Lists` (length, reverse, append, concat, map, filter, fold, find, head, tail,
  nth, head_or, nth_or, take, drop, zip, zip_with, any, all, range) and
  `Std.Options` (map, bind, get_or, or_else, filter, is_some), with the bare
  one-per-language helpers `and`/`or`/`min`/`max`/`abs` beside `not`. `std/list.qll`
  **is** the `Lists` module, `std/option.qll` is new and **is** `Options`, and
  `std/stage2.qll` publishes both as members (`pub Lists = import "std/list"`)
  instead of flattening them — so a bare `map`/`length` is reachable only through a
  program's own `open Std.Lists`, and `Std.Lists.map` is the one spelling. The
  prelude's binding is `Std` (was `stdlib`), so the unit, the path and the handle
  agree. The prelude order now puts `std/lib` below `std/list`/`std/option`, so a
  library binding is written in the language's surface (operators, `Bool`) rather
  than around it.
- Every operation is total: `head`/`tail`/`nth`/`find` answer an `Option`,
  `head_or`/`nth_or`/`get_or`/`or_else` take the default first, `take`/`drop`
  saturate, `zip`/`zip_with` truncate to the shorter list, indices are 0-based, and
  `range(n)` is `[0, n)` — `Nil` for `n <= 0`. Policy comes first and the subject
  last (`map(f, xs)`, `fold(f, z, xs)`, `nth(i, xs)`).
- **Breaking, and intended**: the old bare `rev`/`map`/`fold`/`append`/`option_map`/
  `option_bind` stop existing. The seven `test/conformance/cases/std/` pairs were
  rewritten to the qualified spelling and 51 added — **`conformance: 857 cases, 0
  failed`**, xUnit **`187/187`** (the rename touched `LoaderTests`).
  The new cases pin every behaviour above plus the two namespace decisions: `open
  Std.Lists` makes a bare name reachable, a bare `map` stays out of scope, and
  `Std.Strings` does not exist.
- Documentation: `std/README.md` carries the seam, the layout, the surface and the
  module table; every `pub` binding carries one comment line saying what it returns
  and what it does on the empty/out-of-range case, and each module one line naming
  what belongs in it.
- **Landed 2026-09-28 — this bullet was "Not landed, and the reason" until then**: the
  surface's two conditional impls now ship (`Eq(List(a))`, `Eq(Option(a))`, reached through
  `export Lists.{list_eq}` / `export Options.{option_eq}`; suite 916 cases, and the export's
  first real use measured to leak nothing else). The paragraph below is the original reason,
  kept because it names the two compiler gaps that had to close first: the surface's two conditional impls
  (`Eq(List(a))`, `Eq(Option(a))`) have no spelling in the language yet. An impl
  head's free name binds as the impl's own type variable, with no bound written and
  no hidden dictionary threaded for it, so `Eq.eq` on an element of that variable
  has no evidence; the probe in the ticket's implementation report fails
  `ELAB missing implementation of Eq`. `Lists`/`Options` therefore ship without an
  `Eq` impl and `std` re-exports no impl from them, which also leaves "an impl
  selectively re-exported by a prelude unit reaches a *program's* base scope"
  unmeasured. Also out by decision: `Options.map2`, `Show`, `Ord`, a `Strings`
  module, `fold_right`, `slice`/`range(lo, hi)`, and every new surface syntax.

### Repo layout — the port lifted to the root (2026-09-26)

- `dotnet/` is gone: `dotnet/src` → `src/`, `dotnet/std` → `std`,
  `dotnet/test/{Quill.Tests,Quill.Conformance}` → `test/{Quill.Tests,Quill.Conformance}` (beside
  `test/conformance/cases`, which never moved), `Quill.slnx` and `Directory.Build.props` → root.
  The wrapper only ever existed to tell the two implementations apart, and the prototype's
  deletion retired that job. Relative paths kept their shape — `src/` and `test/` moved together,
  so every `ProjectReference` and the prelude's `..\..\std\*.qll` still resolve — which is why
  the move needed no project-file edit at all.
- **Measured after the move** (integrator run): `dotnet build` succeeds from the root,
  `conformance: 773 cases, 0 failed`, xUnit `185/185`. Both counts are higher than the
  2026-09-25 entries below (763 cases, 185 xUnit) because cases landed after that measurement;
  the suite grew, nothing regressed.
- Docs updated for the new layout: `CLAUDE.md`, `README.md`, `test/conformance/cases/README.md`,
  this header, and the comments in `test/Quill.Conformance/Program.cs` and `src/Quill.Compiler/Prelude.cs`.
  Dated entries below keep the paths they were written with.

### .NET port — parity re-measured (2026-09-25)

- **Port: `conformance: 763 cases, 0 failed`, xUnit `185/185`, `dune test` green with
  `conformance: 763 cases, 0 failed, 31 known prototype divergences`** (integrator runs).
- **The corpus measured, 2026-09-25: `agree 732 | port-fails 0 | prototype-fails 31 | both-fail 0
  | hang 0` over 763 programs**, with zero untagged disagreements. Note the two counts: the
  harness reported 31 prototype failures, while `test/conformance/prototype-divergences.txt`
  lists **34** entries, so three entries were never exercised by that corpus. `port-fails: 0` is
  the gate for [deleting the prototype](wayfinder/tickets/delete-the-prototype.md); the other
  half is the two unprobed residues. The harness bounds the corpus, not the language: every hole
  found on 2026-09-24/25 was found by probing, and none was visible in this number.
- **The refusal inventory is 14 sites, re-swept and classified**: **3 real gaps** — the checker's
  unhandled-effect error (`Nbe.Effects.cs`), a pattern head that is a member of a non-nominal
  (`Elaborator.Enum.cs`), generalising a pattern synonym over a stuck neutral
  (`Elaborator.Patterns.cs`) — plus 7 **unreachable** by enumeration, 1 in flight, 1 ruled and
  queued (an unused type parameter), 1 ruled and deferred (a typed operator macro, where the
  prototype hangs too), and 1 unprobed residue. Each gap's reaching program is written on its
  ticket. All three hide behind rows of the first audit marked *fixed*: a fixed row named a
  **route**, never the throw.
- **Four tickets closed as stale**, every scope bullet verified in `dotnet/src` rather than by
  prose: procedural macros (including the
  per-binding interleaving it called "not done": `MacroRuntime.Advance` called by
  `Expander.cs:244`), syntactic roles,
  effects, and
  the generative former's identity residue
  (already fixed the day it was written, `1f70e82`). So the remaining port work is no longer
  three waves but the handful of sites above.
- **Landed:** a stuck match reads back its arms (`Term.Match` carries its arms'
  `Patterns`; `values/stuck-match-pruned-arm`, rewritten 2026-09-28 — the arm it used to shadow
  is now an unreachable-arm error); a budget error names the **call stack**
  (outermost → innermost, `<request> at <site>`, one stack across macro applications and checker
  demands, outermost 3 / `… N more …` / innermost 3) — the last of the ruling's three observable
  budget behaviours, xUnit 183 → 184.
- **Ruled:** an unused type parameter is an error at its declaration **in both implementations**
  (all type formers, enum and record alike; a lambda is a former iff its result is a type);
  a **nested field pattern is supported** (the port ahead of the prototype on purpose); a match
  whose head is known but a pattern inspects an unknown part **waits**; a pattern synonym's
  generalized types are its **implicit type parameters**.
- **Corrected in the record:** the budget ticket's headline program used a
  `macro m(_) <diverging>` annotation that exists in **neither** implementation — both runners
  reject the spelling, and the divergence it stood for was reproduced with the prototype's own
  program instead. A transcribed probe that nobody ran is worth less than one command.

### .NET port — procedural macros and interleaving (2026-09-17)

- **393 of 689 conformance cases pass in C#** (172 xUnit), up from 334/672. Added:
  procedural macros (reflection over the prelude's `Syntax`, definitions and calls,
  quotes, the runtime, type-aware macros, operator macros); a unit's expansion and
  elaboration interleaved per top-level binding, so a macro compiles as of its
  definition; impl resolution by argument type; a method's `~>` row; stuck matches.
- `dotnet/std/stage2.qll` now compiles as a unit; binding it as the prelude is next
  (operators ~171 cases, `type` ~89).
- User rulings this round, each ticketed: impls resolve to the most precise matching
  one (`traits.md` "Resolution"; nothing to order until impls can be generic); a block
  may declare a pattern synonym; `core-210`/`core-214` qualify `Syntax.RExpr`. Open
  design ticket: a local open expression `M.(e)`. 19 known prototype divergences.

### .NET port — wave 3: prelude stage 1 (2026-09-17)

- **334 of 672 conformance cases pass in C#** (131 xUnit), up from 206/650. Added:
  primitives (one table, checked I64); the expander's `IMacroRuntime` with roles
  through imports; references (`Mutate` rows, heap discharge, E6 through stores);
  `~>` arrows, method rows, generative modules; prelude stage 1 elaborated from
  `dotnet/std/stage1.qll`, bound as `stdlib`, opened around every program;
  let-generalisation; both conformance runners now evaluate `error` cases.
- User rulings this wave, each ticketed as a prototype defect and listed in
  `prototype-divergences.txt` (13 in all): checking against an implicit function
  type binds first; `panic` with an unknown message stays stuck; a reference
  operation infers `Ref`; a method may infer its row with `~>` (in progress); a
  signature check takes the last member (I3).
- Remaining walls: prelude stage 2 — operators (171), `type` (~89) — and
  procedural macros (54).

### .NET port — wave 2 (2026-09-16)

- **206 of 650 conformance cases pass in C#** (90 xUnit), up from 91/621. Added:
  export; traits and impls with evidence resolution and bounds; recursive enums,
  groups and recursive records (`RecursiveOccurrence`); record, struct-type and
  type-case patterns, pattern synonyms; syntactic roles, operators, order groups
  and `syntax` rule forms; algebraic effects (rows on arrows, deep one-shot
  handlers as machine frames, tunneling, E6).
- Prototype defects found this wave, each fixed in C# only and listed in
  `test/conformance/prototype-divergences.txt` (9 in all): a recursive record field
  of its own type, a type-case former head's arity, pattern synonyms binding by
  position and not found through `open`.
- Remaining walls: the prelude (327 cases wait on its operators, forms and names),
  procedural macros (54), refs (34).

### .NET port — slice 2a and wave 1 (2026-09-16)

- **89 of 618 conformance cases pass in C#** (73 xUnit). Added since slice 1:
  first-class modules and `open` with open choices; imports of compilation units
  (base-anchored, value transport, cycles, elaborate-once); implicit parameters and
  insertion; structs, record construction, signatures, module-type unification,
  struct methods; `rec` values and groups with the checker budget and lazy delta;
  `match` compiled to decision trees, non-recursive enums as nominals (E11).
- Built by five parallel forks against written conventions (port ticket,
  "Porting conventions"); each wave-1 ticket records its merge and follow-ups.
- The prelude's source is copied to `dotnet/std/stage{1,2}.qll`; the OCaml
  prototype is not maintained once the port is done.
- A bare constructor pattern resolves like any other name (binder or open); the
  prototype's by-name lookup is ticketed and listed as a divergence.

### .NET port — slice 1: reader to evaluator (2026-09-16)

- The C# port lives in `dotnet/` in this repo, so `test/conformance/cases` stays
  one copy. Three projects (`Quill.Kernel`, `Quill.Expand`, `Quill.Compiler`, plus
  `Quill.Cli` and two test projects): the split enforces that `Quill.Expand` cannot
  reference the elaborator, as `core_tt_expand` cannot today. `dune build`
  ignores `dotnet/` (root `dune`, `(dirs :standard \ dotnet)`).
- The whole pipeline runs for the prelude-free subset: a hand-written reader
  (`System.Buffers.SearchValues`), enforestation with blocks read one statement at
  a time, scope-set expansion, bidirectional elaboration with metavariables, and
  the evaluator as an explicit frame-stack machine (a million nested calls run
  without touching the native stack).
- **12 of 604 conformance cases pass**, each for a genuine reason: literals,
  lambdas, `let` with shadowing and annotations, and three elaboration cases.
  41 xUnit cases cover the reader, hygiene, the machine, unification and
  kernel equality.
- Unported forms raise `NotImplementedException`, which the runner counts as a
  failure and never as a passing `error` case; so does a name the base context
  lacks inside the prelude open, and (until `std`'s syntax roles are ported) any
  enforest error.
- The domain model is the port's specification. Two prototype defects are
  ticketed and fixed in C# only: a meta's solution under a spine is renamed
  without lifting under binders, and checking a lambda ignored its written
  parameter type. The shared suite states the specified behaviour: cases the
  prototype gets wrong are listed in `test/conformance/prototype-divergences.txt`,
  which the OCaml runner expects to fail (`elab-049` is now `error`; three cases).
- Kernel records hold sequences as `EquatableArray<T>`: `ImmutableArray<T>`
  compares by reference, which made structurally equal records unequal.
- Decisions and the slice plan: `docs/wayfinder/tickets/port-core-tt-to-dotnet.md`.


### Stage 11 increment 2 — keyword surface (2026-09-16)

- `then`, `with`, `end`, `else` and `Unit` are no longer keyword tokens: no parser
  rule matched them (a syntax form's rule literals compare by spelling, so the
  prelude's `if` form matches `else` as a plain token; `Unit` had an identical
  `Ident` path in expression and pattern position). They are ordinary identifiers.
- Already library, confirmed by survey: `if`/`else`, `&&`/`||`, arithmetic and
  comparison operators, prefix `not`, `type` (stage-2 std macro).


### Shared conformance suite (2026-09-16)

- `test/conformance/cases/<area>/<name>.qll` + `<name>.expect` is the
  language-behaviour suite both the prototype and the .NET port run; extra units
  are `<name>.unit-<unit>.qll`. 601 cases (`values`, `macros`, `imports`,
  `elaborate`), extracted from the Alcotest binaries. **It is now the only copy:**
  the Alcotest cases it covers are deleted (`test_core` 443 → 172 cases,
  `test_elaborate` 500 → 269), so a language behaviour is tested in exactly one
  place.
- `.expect` is a value, a constructor name, `ok` (elaborates) or `error` (fails).
  Error wording is not pinned - it is implementation-specific.
- Runner: `test/conformance/run_conformance.ml`, `dune test test/conformance`.
  Internal tests stay in Alcotest: shapes, reflection round trips, budget
  accounting, macro nominals plumbing, an exact error constructor
  (`expect_elab_error`/`expect_expand_error`), a type rather than a value
  (`check_type`/`check_conv`), and a program whose expectation lives in the
  caller.


### Staged prelude; `type` is a std macro; `export` (2026-09-16)

- The prelude elaborates once, in two stages: stage 1 declares `Bool`, `Option`,
  `List` and `Syntax` as enums (no elaborator); stage 2 is a driver run against it
  that re-exports it and defines the operators, `Eq` and `type`
  (`Macro_driver.init_ctx`, `std_syntax`, `std_load_syntax`).
- `type` is a std syntax form over the macro `type_decls` expanding to
  `rec … = enum { … } [and …]; export …; open …`; the compiler's type declaration
  is deleted. An `import "std"` delivers roles and compiled macros
  (`Expand_ctx.unit_syntax`).
- `export M` / `export M.{a, b}`: members, enum constructors, named impls, a unit's
  roles and macros; clashes are errors.
- Blocks apply declaration macro calls and read unread items; a block's
  `rec … and …` enum group elaborates; type-case refinement skips names bound
  before its target, and reaches every channel an entry can live in - the names,
  the self entry, the resume entry, the members an `open` introduced, the self
  methods, the constructor entries and the trait evidence. Its target is the
  variable the scrutinee *evaluates to*, so a type-case on an alias of a bound
  type variable refines it, and a scrutinee evaluating to anything else refines
  nothing.
- Known: in a recursive function, `match (l) { …, Cons(m, Nil) => …, Cons(m, rest) => … f(rest) }`
  fails at run time with "match on non-constructor value" (pre-existing; the
  prelude's macro avoids the shape).

### Effects on the arrow; `~>`; bound sets (2026-09-16)

- A row sits on its arrow: `A ->{Log, Exc} B`, open `A ->{Log | e} B`, a variable
  alone `A ->{e} B`, inferred `A ->{_} B` (unsolved at the entry is
  `UnsolvedEffectRow`). A bare arrow is pure. `can` is deleted.
- A row is known effects plus a **set** of row variables (`A ->{Log | e1, e2} B`),
  so a result may unite several callbacks' rows. A tail solved to a row is
  spliced in (`normalize_effect_row_value`), so a union disappears as its
  variables are solved; a union of two unsolved tails against a concrete row is a
  mismatch, not a guess.
- A type that mints its own row (`Callback = Unit ~> I64`) is a function of that
  row; a parameter naming one mints it at the definition that takes it (rank 1),
  so its caller chooses the effects. A binder written out in the annotation stays
  rank 2.
- `~>` (a base role): every function type is read on its own - each parameter's
  type is a signature in its own right, and the chain's **final** arrow carries
  the variables its parameters minted (`Elab_poly_arrows`), so a partial
  application is pure; on a definition the final arrow also infers what the body
  performs. A final `~>` with no parameter to collect from mints its own variable.
- Definitions: `fn(n : I64) : I64 { … }` when pure, `fn(n : I64) ->{Log} I64 { … }`
  or `fn(g : Unit ~> I64) ~> I64 { … }` when not; `pub method m() ->{Exc} I64 { … }`.
  `: T` is the pure member of that family: it states the function's type with an
  empty row (so it needs every parameter's type), and a body that performs under
  it is `EffectsInPureResult`, which names the effects and points at the arrow
  forms. A method with no declared row reads the same way.
- Trait bounds are a set: `[A : {Eq, Show}]` (a single bound may stay bare).
- Row unification sends each side's unnamed effects to the other side's tail.


### `export`; block-level enum groups (2026-09-16)

- `export M` / `export M.{a, b}` re-exports a module's public members or an enum's
  constructors as members of the enclosing module, opening nothing locally;
  clashes are `ExportClash`; a unit's roles re-export with it. A module with
  public impls is `ExportImpls` (undecided).
- A container's public members are unique (2026-09-21, C# port only): a repeated `pub`
  name is `duplicate member: \`x\` is already public`, and a repeated constructor is
  `duplicate constructor`. A private rebinding of a public name, an `open`'s name
  shadowed by a `pub`, and a nominal's own label are all still legal. The prototype keeps
  last-wins, so `values/core-121`, `elaborate/duplicate-enum-constructor` and
  `macros/spliced-public-decl-is-a-duplicate` are recorded divergences (22 lines).
- A block's `rec A = enum { … } and B = enum { … }` elaborates with the module
  knot; its slots become `Let`s.
- The `type` prelude macro is blocked: the prelude has no elaborator to compile a
  procedural macro, and `std` delivers no macros (see the ADT ticket).

### Enums: ADTs as values (2026-09-16)

- `enum { Red, Some(A) }` is a type value; its constructors are members:
  `Color.Red`, `open Color`, and patterns `Color.Red`. It elaborates as the block
  type declaration it names (`TypeDef`), so `type … = …` shares its path.
- `Option = fn(A : Type) { enum { Some(A), None } }`: `Option.Some` is generic over
  the former's parameters; `Option(I64)` is one type (E11 captures).
- `rec Tree = enum { Leaf, Node(Tree, Tree) }`, `rec L = fn(A : Type) { enum { … L(A) … } }`,
  and a module's `rec A = enum { … } and B = enum { … }`. Block enum groups and
  enum/struct mixes are errors.


### Effects follow-ups (2026-09-16)

- **Tunneling by lexical handler and instance.** A call with an open row is
  `Tunnel { named; handlers }`; a request whose effect instance (family and
  parameters) its row does not name passes the handlers lexically enclosing the
  call (`effect_request.skips`; each `EffectBranch` carries its handler id).
- **E6 through outer references:** storing a closure that names a handled effect
  into a reference not local to the match is `HandledEffectEscapes`; so is the
  shape where another branch fixed the closure's row.
- **Refs:** an unhandled `Mutate` names the reference (`effect Mutate(r)`); a
  `let` or block with private mutation discharges its local heaps; local-heap
  detection is one pass over older metas; `EffectRef` carries the effect's id.

### Macro-system fixes for a `type` macro (2026-09-16)

- **`pub` before a declaration syntax form or macro call** makes every
  declaration it returns public (read items included). `InstantiateBinding` and
  `MacroCallBinding` carry `public`, reflected both ways (`Syntax.publish`).
- **`List(TokenTree)` holes and parameters:** a `: Decl` form's last hole of that
  kind takes the rest of the use as unread token trees (`CapTokens`); a macro
  parameter of that kind takes its argument's tokens. A hole written as a
  macro's whole argument in a replacement takes the captured tokens. Anywhere
  else (not last, not `: Decl`) is an error at the definition.
- **`type` is an ordinary identifier.** The built-in type declaration is the base
  role `TypeDeclaration`, resolved by scope set, so a user form named `type`
  shadows it.

### E11: captures from the enclosing scope; generative modules sealed (2026-09-16)

- A nominal captures the variables its enclosing module or function body names
  (from the first bound variable on), so `Set(I64, less).T ≠ Set(I64, greater).T`.
- A `let` or module member whose value performs something seals its type at the
  binder: a declared type member becomes `st1.Symbol`, distinct from `st2.Symbol`.
  An unnamed effectful module's declared type may not escape a field access
  (`GenerativeTypeEscapes`).
- Run-time stamps: a module's private stamp slot is a fresh cell per evaluation
  of a generative module; type-case compares nominal instances by captures and
  stamp. Sealing picks declared nominals by identity; a sealed type may not leave
  its binder's scope or its module's type.
- A `rec` struct occurrence carries captures and unfolds per instance.
### Nominal identity is applicative (E11, first half, 2026-09-15)

- A nominal's identity is its declaration id plus `captures`, the values of the
  declaration's own free variables, compared by conversion (`VNominal`,
  `NomRef`). `NomRef` builds the nominal directly; constructors come from
  `Core.nominal_decls` over an instance's captures. No environment scan.
- `TypeBind` / `NominalDef` build nominals in their evaluation scope: a type
  declared under a binder evaluates, and `Set(I64, less)` twice shares `T`.
- Module values quote each binding at the depth the earlier bindings push.
- Open: generative nominals under an effectful maker (sealing), and `rec` struct
  identities under binders (see nominal-identity-applicative-by-purity).

### Handlers tunnel callback effects (2026-09-15)

- Handling is lexical (E5): a call whose latent row has an open tail is wrapped
  in `Core.Tunnel`; a request of an effect family the row does not name, which a
  handler lexically enclosing the call in the same function body handles, skips
  those handlers (`effect_request.hops`, decremented by each handler of that
  family). A lambda or method body starts with no enclosing handlers
  (`Ctx.handler_scopes`). `find(user, 1)` answers 999.
- A handled effect may not escape (E6): a match whose result type carries a
  function whose row names a family it handles is `HandledEffectEscapes`. A saved
  continuation may outlive its handler (its row is the residual).
- Not covered: per-instance (vs per-family) routing; an escape through an outer
  ref's type; `resume` returns the scrutinee value without re-running the value
  branch (pre-existing).

### Refs in effect rows (2026-09-15)

- **Using a reference is an effect.** `ref(e)`, `deref(r)` and `r <- e` perform
  `Mutate(h)` on the reference's hidden heap. `Ref : [h : Type] -> Type -> Type`
  takes the heap as an implicit argument, so `Ref(I64)` is unchanged and each use
  gets a fresh one. `VRefTy`/`RefTy` carry (heap, element).
- **A signature names the reference or infers:** `(r : Ref(I64)) -> Unit can
  {Mutate(r)}` (`Mutate : [h] -> [A] -> Ref(h, A) -> Type` maps a reference to its
  heap's effect) or `can _`.
- **Local mutation is pure.** At a function boundary, a heap created while
  elaborating the function that its type (domain, result) does not mention and no
  older heap aliases is dropped (`Elab_effects.discharge_local_heaps`):
  `sum_to : I64 -> I64` with a local accumulator checks and evaluates in a type.
  Returning the reference, or merging its heap with an outer one, keeps the effect.
- **Top-level references work:** the entry's runtime handler
  (`runtime_handled_effects`) discharges `Mutate` on any heap.
- Discharge happens at function boundaries and the entry; a non-function `let` or
  block does not discharge on its own.

### One declaration per primitive; checked I64 arithmetic (2026-09-15)

- `Nbe_prim.declarations` is the one table of primitives (name, type, reducer:
  `Atoms` or `Special`); the elaborator's prim types and the evaluator's reducers
  derive from it. The pure-arrow combinators live in `core.ml`.
- I64 `+ - * /` are checked: overflow (including `min_int / -1`) fails with
  "integer overflow in <op>", as division by zero fails.

### Tuple types; one grammar for types (2026-09-15)

- Tuple types are `Tuple(n, T1, …, Tn)`: a built-in whose type is
  `(n : I64) -> tuple_arity(n)`, the arity computed from `n` (a negative `n` is an
  evaluation error). It reduces to the flat product type; projections are
  unchanged. `*` is only multiplication: `I64 * Bool` as a type is rejected.
- Annotation types (bindings, parameters, fields, signature members, `e : T`) are
  read with the expression grammar; the separate type grammar is deleted. A user
  type operator such as the prelude's `~>` works in every annotation position.

### Methods follow the arrow rule (2026-09-15)

- A method is pure unless it declares a row: `pub method bump() can {Log} { … }`,
  `can _` infers it (`Syntax.MethodBinding.effects`, reflected as `DeclMethod`'s
  row). The row sits on the method's innermost arrow, so a call performs it once
  every argument is supplied; a body performing beyond it is `UnhandledEffects`.
- A trait method signature's arrow carries its row like any arrow, and an impl's
  method is checked against it.

### A bare arrow is pure (2026-09-15)

- `A -> B` is `A -> B can {}` (E3): an omitted row is the closed empty row
  (`Elab_type_expr.elaborate_effect_row`). `A -> B can _` infers the row (a fresh
  meta tail); `_` may also stand for a written row's tail, `can {IO | _}`
  (`Syntax.effect_row.inferred`, reflected as `MkEffectRow`'s third field).
- `A ~> B` is prelude sugar for `A -> B can _` (`order arrow`, right-associative).
  It is read only by the expression grammar: type annotations (`x : T`, binding
  types) go through the separate type grammar, which reads no user operators, so
  there write `can _` (or bind `Callback = Unit ~> I64` first).
- A plain `rec fact : I64 -> I64` is now known pure, so its calls get the lazy
  delta shortcut. A pure call is deferred (`VGlued`) only in checker requests,
  not inside a macro application, whose result is read at once.
- Test helpers that run a program now use `Ctx.run` (unbudgeted), not the
  checker's `Ctx.eval`.

### Recursive records by identity; records only as let bindings (2026-09-15)

- A record type is a value: `P = struct { x : I64 }`, or with parameters
  `Pair = fn[A : Type, B : Type] { struct { fst : A; snd : B } }`.
  `type X = struct { … }` is a parse error naming these forms.
- `rec Numbers = struct { head : I64; tail : Option(Numbers) }` (under any
  parameters) mints a record identity. The body sees `Numbers` as a recursive
  occurrence (`Core.RecOcc` / `VRecOcc`, replacing `SelfTypeRef` / `VSelfType`),
  bound by a core `Let`; the finished value is recorded under the identity
  (`Core.finish_record`). An occurrence unfolds on demand (`Nbe.force_shape` at
  field access, record patterns and match domains); conversion and unification
  compare two occurrences by identity and unfold one against anything else. Two
  same-shape recursive records are distinct; plain records stay structural.
- `rec A = struct { … } and B = struct { … }` is a recursive group
  (`RecGroupBinding` / `LetRecGroup`, reflected `DeclRecGroup` /
  `RawLetRecGroup`); one knot (`elab_rec_group`) serves a lone `rec` struct and a
  group. A group holds struct types or values, not both.
- `rewrite_record_self_refs` (matched by spelling) is deleted. `Self` means only
  the struct being defined. A struct's fields are checked for duplicates.
- Limit: the identity is minted once at elaboration, so a `rec` struct under a
  binder shares one identity across evaluations until E11 lands.
- `rec even : I64 -> Bool = fn(n) { … odd(n - 1) … } and odd : … = …` is a group of
  mutually recursive values (2026-09-15). A fixpoint is a member of a recursive
  group: `Core.Fix { members; index }`, `VFix` over a `fix_closure`; a single
  `rec` is a group of one. Every body is checked once seeing every member at its
  annotated type (or a meta); each member keeps its own name and purity, so
  check-time unfolding under the budget and the pure-call lazy delta work per
  member. A module's group bindings are emitted in member order (the group
  used to come out reversed).

### One-pass effects; unhandled effects at the top are errors (2026-09-15)

- Effects are computed while inferring and checking, not by a second walk:
  each context carries a sink `perform` and a latent row at an application emit
  into; a lambda body, a type, a handled scrutinee and a unit elaborate in a
  fresh one. `elab_effect_collect.ml` (`collect_effects`, `compile_time_safe`)
  and the deferred-output table are deleted.
- A handler discharges what it handles from its scrutinee and its branch bodies
  (deep). A let or module/struct member whose value performs is opaque at check
  time.
- An entry expression and an imported unit's top-level bindings are checked
  against the row the runtime handles (none yet): an unhandled effect is
  `UnhandledEffects` naming it
  ([unhandled-effects-pass-the-checker](wayfinder/tickets/unhandled-effects-pass-the-checker.md)).

### Fresh declaration binders, no string-built ids (2026-09-15)

- Every declaration binder - types, constructors, effects, traits, pattern
  synonyms, module items, macros - gets a fresh resolved name
  (`Expand.bind_declaration`); a declaration exports its label (`Syntax.label`),
  and the elaborator keys its context by resolved name. A macro's `type Tmp`
  no longer takes the caller's `Tmp`.
- `Syntax.new_id`, `var`, `lam`, `let_in`, `seq` and `no_scopes` are deleted: a
  macro gets a name from `quote` or an `Id` parameter. A reflected id's `Scopes`
  carries the resolved name it was minted with, and a reflected `#` name is
  accepted only under that certificate, so a macro cannot forge one. The
  empty-scope and operator spelling fallbacks for macro heads are gone.

### Performance after M9 (2026-09-14)
- M9 run 2 slowed the suites up to ~125x (`test_macro_driver_stage7`
  0.13s → 16s). Two causes, both fixed without semantic change:
  - **Cubic item loop.** A definition context re-applied every active scope to
    *all* its remaining unread items before each item. An item's extent is
    structural (`take_statement`), so only the item being read takes the
    scopes; several scopes are added as one union, not one traversal each.
  - **Context refinement copied the whole context.** `refine_context_type_var`
    (type-case branches) was ~90% of `init_ctx` before M9 too, and grew with
    the larger prelude. The substitution now preserves sharing and memoises
    values and environment tails by physical identity.
- Side by side with `0262a02`: stage7 0.07/0.05s, syntax 0.30/0.34s,
  elaborate 7.5/8.3s, core 6.9/7.5s.

### Regression coverage
- Imports, module files, records, record patterns, methods, `self`/`Self`, qualified
  patterns/constructors, algebraic effects (`perform`, handlers, `resume`) all have
  regression test coverage. See [regression coverage](wayfinder/topics/regression-coverage.md).

### Type-case / generic programming
- Primitive and nominal type-head matching, structural record type reflection,
  open-`Type` fallback, generic equality dispatch.
  See [type-case / generic programming](wayfinder/topics/type-case-generic-programming.md).

### Record type reflection
- `struct … end` type patterns over constructor fields.
  See [record type reflection](wayfinder/topics/record-type-reflection.md).

### Algebraic effects
- Nominal effect families, latent rows with open row tails, `perform`, match-based
  handlers, `resume`. Deep handler semantics, lexical resume in nested lambdas,
  one-shot continuations. See [algebraic effects](wayfinder/topics/algebraic-effects.md).

### References
- `Ref(A)`, `ref(e)`, `deref(r)`, `r <- e`. Opaque mutable cells, aliasing and
  closure-capture semantics preserved; using one performs `Mutate` on its heap
  (see "Refs in effect rows"). See [references](wayfinder/topics/references.md).

### Modules and the strict phase rule
- `open <module-expr>` is an item of a module or struct body, not only a `do`-block
  statement: it scopes over the *subsequent* bindings, exports nothing, and carries
  its runtime scope extension as `Core.OpenBind`. Imported modules are strict about
  prelude **syntax** — `Enforest.parse_module` has no `?open_prelude` flag and the
  loader no longer harvests the prelude for them, so a module that uses `+` writes
  `open (import "std")` itself. Prelude *values* still reach a module through the
  importer's elaboration context; see
  [imported module elaboration context](wayfinder/tickets/imported-module-elaboration-context.md).

### Brace surface syntax (2026-09-14)
- Bodies are brace groups: `fn(x) { … }`, `method m() { … }`, `macro m(x) : K { … }`,
  `infix (op) g ($a, $b) { … }`, blocks `{ … }`, `module { … }`,
  `sig { … }`, `struct { … }`, `multi { … }`, `syntax head { rule => replacement, … }`.
- `if (c) { t } else { e }` (a prelude template, `if ($c) $t else $e`) and
  `match (v) { pattern => result, effect E.op x => result }` (Rust-style arms: a `{ … }` result ends its arm, any other ends at `,`; `|` is pattern union).
- `->` is only the function-type arrow; `=>` separates a pattern from its result
  and is reserved (`infix (=>)` is an error).
- Record types are `type P = struct { x: I64 }`; construction, record patterns and
  `can {…}` rows are unchanged. `module M do … end` is gone (`M = module { … }`).
- Newlines are whitespace; `;` separates. A trailing `;` before `}` discards the
  block's value, and a block statement may be a bare expression (`_ = e`).
- Old `-> body` / `do … end` / `match x do` forms fail with an error naming the
  new form. The reader's keyword-pair grouping helpers are deleted; arms split
  by "a pattern holds no bare `=>`, a result holds no bare `|`".
- A template hole ending a group now extends its capture to the whole group.
- Prelude and every test source migrated mechanically
  ([surface-syntax-braces](wayfinder/tickets/surface-syntax-braces.md)).

### Struct items in source order (2026-09-15)

- A struct's items are one source-ordered list: a field is an item
  (`Syntax.FieldBinding`, reflected `DeclField`; `RawStruct` carries only items).
- A field's type sees the opens and bindings written before it, as a module's
  items do; a later binding is not visible. Its type leaves the struct as a
  value, quoted at the struct's own level.
- A method is checked after the last field, so `self` has every field. A field
  type mentioning an earlier method is `FieldTypeMentionsMethod` (a cycle).
- `Self` in an item other than a field or method is the fields written so far.
- No dependent fields: a field is not a binder, so `struct { n : Type; v : n }`
  leaves `n` unbound. A struct does not see its own name (`C` binds after
  `C = struct { … }`); `C.k` inside it is unbound.

### Decl macro output types; opening a module parameter (2026-09-15)
- `: Decl` is one declaration, `: List(Decl)` any number: the annotation is the
  type the body is checked against where the macro is defined
  (`Syntax.macro_compiled` wraps the body in `Annotated`, `Decl` written as
  `Syntax.Decl` at the annotation's scopes). `quote { … }` checked against
  `Syntax.Decl` must hold exactly one non-hole item (`QuoteNotOneDecl`);
  anywhere else it is the list. The `VU` instantiation workaround is gone.
- A parameter means what the same type means as an output: `(d : Decl)` takes
  a `{ … }` group holding exactly one item (`HoleOneDecl`, captured `CapDecl`,
  the macro sees a `Decl`); `(d : List(Decl))` takes a group of any number
  (`HoleDecl`, a `Decls`). A syntax form's `$(d : Decl)` hole is unchanged.
- `open` binds what the module's **type** lists (I2): `Core.Open`/`OpenBind`
  carry the members (`OpenField name`, `OpenImpl i`), and the evaluator pushes
  each as a projection of the module value. A module parameter (a neutral) opens
  by projection; a module with more members than its signature opens only the
  signature's. A signature names every impl it requires, so a parameter's impl
  opens by projection too.
- Signatures are telescopes (2026-09-15): `sig { T : Type; empty : T }` reads
  `empty : self.T`, so a parameter `s : Stack` has `s.empty : s.T` (abstract) and
  an argument is checked against the member types its own `T` gives
  (`Core.Sig` → `VSig` closure, `Nbe.module_type_of`). An impl a signature
  requires is named, `eq_T : impl Eq(T)`: provided under that name, reached as
  `s.eq_T`, brought into trait resolution by `open s`; an anonymous `impl` in a
  `sig` is a parse error.

### Macro signatures (2026-09-15)
- A macro's type binders, `(x : Expr(T))` parameters and `: Expr(T)` output are
  its **signature**, a pi type elaborated where the macro is defined
  (`Syntax.macro_signature`, carried on `Expand_ctx.macro_entry`). A name in it
  must resolve there and a promised `T` must be a type. The `_ = T` body device is
  deleted; `MacroBinding`/`MacroDef` carry `output`, reflected both ways.
- A macro whose signature promises a type waits for the elaborator, which applies
  it like a function over types (`Elab_resolve.apply_typed_macro`): binders become
  metas, typed arguments are checked at their types, the result meets the
  expected type, every binder must then be solved ("cannot infer A for
  `default`"), the macro runs with them, and its output is checked at the promised
  type ("macro `n` promises Expr(I64) …"). Any number of binders. A macro that
  promises no type still runs during expansion.
- A typed argument elaborates once, at the call. Where the output places it
  unchanged it becomes `Syntax.Elaborated` (internal, reflected as the argument
  itself), and elaborating that reuses the core, weakened past the binders the
  output added (`Elab_defs.shift_term`, which now widens inserted metas' masks).
  A rebuilt argument is new syntax and elaborates normally; an argument whose
  core holds an `open` placed under new binders elaborates again.
- A typed call's effects are its output's: the elaborator records the output by
  the call's node (`Elab_resolve.deferred_outputs`) and the effect pass reads it
  there. Every effect read follows the elaboration it reads — a type is required
  pure after it elaborates and before it is evaluated
  (`Elab_type_expr.require_pure`), so a typed call works in a type position too.
- `open` of a non-module is `NotAModule` in every form (expression, module item,
  struct item, effect pass); an open's entries are its module type's public
  fields and impls, and a value whose entries do not line up with its type is an
  internal invariant failure, never a silently narrower open.
- The elaborator's copied macro table is gone: it asks its macro runtime, so a
  typed macro works inside the unit that defines it and when imported
  ([macro-annotation-constraints-mean-nothing](wayfinder/tickets/macro-annotation-constraints-mean-nothing.md)).

### Order groups and structural hole extents (2026-09-15)
- Precedence is relative: `order g : stronger_than(a) weaker_than(b) assoc(right)`
  declares a group (a binder resolved by scope set, `pub`, delivered by `open` and
  import binders); the order is transitive and a cyclic declaration is an error.
  `infix (op) g`, `prefix (op) g`, `syntax name g { … }` join a group; numeric
  precedence is gone (an error names the new form). Operators with no declared
  order never mix ("no declared order; parenthesise"); a form or operator in no
  group is weaker than every grouped one. The prelude's operators are in
  `disjunction < conjunction < comparison < additive < multiplicative < negation`,
  with `pipe` declared `stronger_than(comparison) weaker_than(additive)`: `|>` chains
  left-to-right and binds tighter than comparison (`x |> f == y` is `(x |> f) == y`).
- `assoc(none)`: members of a non-associative group do not chain ("do not chain;
  parenthesise"). `<-` is in the compiler-known group `assignment`, below
  `disjunction` and `assoc(none)`: `r <- x + 1` is `r <- (x + 1)`, `a <- b <- c`
  is an error. A group may be named through a unit: `stronger_than(O.g)`,
  `infix (op) O.g`, where `O` denotes an imported unit.
- A syntax form's hole extent is structural: the hole ending a use reads the
  form's operand at its order; a hole before `,`/`;` reads to it; any other hole
  is one term (a token or one bracket group). Captures are one parse per hole
  (`try_prefixes` deleted). See
  [brackets-decide-grouping](wayfinder/tickets/brackets-decide-grouping.md).

### Macro model M9, run 3 — M9 complete (2026-09-15)
- A macro parameter takes a kind: `macro m(n : Id, p : Pattern, b : Block, d : Decl)`.
  A call's arguments are read as the kinds of the macro its head resolves to,
  local or imported (the loader's caches carry the kinds); a `Decl` argument is a
  brace group of items, unread until spliced, and its value is `Decls`. A wrong
  kind or count is an `Expand_error`.
- A declaration hole `$d` in `quote { … }` takes `Decls` and splices them.
- A `$n` identifier token in `quote { … }` (a generated rule's head) is an `Id`
  hole.
- An import's roles bind in the region of the open or binder that imported it.

### Macro model M9, run 2 (2026-09-14)
- A syntax form is a macro: its rules are reflected data on its role, and a use
  is filled through `Expand.application` (`Instantiate`). The region rule and
  token-level re-enforestation are gone.
- The expander drives the enforester: `{ … }` bodies and unit/module items stay
  unread (`Block`, `Items`) until expansion reaches them, so syntax a form
  generates is usable by the next form. Quoted syntax is read where written.
- A unit's syntax exports come from expanding it.
- `Syntax.tokens(b)` reads a block's token tree; `Syntax.expand_block(b)` expands
  one inside a macro; `Syntax.expand_decls(d)` does the same for a `Decl`
  argument's items, in order (2026-09-15); expansion is idempotent. Resolved
  names are `x#n`.
- A `: Decl` form works as a block statement; binders a macro returns into a
  definition context lose the use-site scope.
- Test changes: units that define forms open `std` themselves (a replacement is
  read at its definition); import cycles are reported by the syntax load that
  reaches them first; the circular-syntax tests use real import cycles.

### Macro model M9, run 1 (2026-09-14)
- Hole kinds are reflection types: `$(x : Expr | Block | Id | Decl | Pattern)`,
  a bare `$v` is `Expr`; `binder`/`ident` are gone (`Id` binds or refers by
  position). A `Pattern` capture splices a use-site pattern.
- `syntax head : Decl { pat => { items } }`; a syntax form is used only in its
  kind's position. `multi` is deleted.
- `quote { items }` quotes declarations; a lone `$d` item is a `Decl` hole.
- The expansion position is the site's: a `Decl` macro works inside an
  expression-level `module { … }`.
- (Run 2 below lands templates as macros, the loop, `Block` and `expand_block`.)

### Checker evaluation budget (2026-09-14)
- Every evaluation the checker asks for spends from one call budget
  (`Eval_budget`, 1,000,000 calls per request, no surface syntax to raise it);
  running out is `ElabError EvaluationBudgetExceeded`, not a hang.
- A fixpoint unfolds at check time on any argument, open or closed (revised
  2026-09-15, see below): `double(n)` converts with `n + n`; a divergent
  unfolding such as `loop(n)` in a type is a budget error naming the call.
- The budget measures work: calls plus every conversion and unification step.
  Two calls of one known-pure fixpoint (closed empty effect row) on convertible
  arguments convert without unfolding: under the checker such a call is a
  deferred `VGlued`, unfolded when inspected (lazy delta).
- **One binder count per core form.** `Core.map_subterms` states how many
  environment entries each immediate subterm sits under (`None` where only
  evaluation knows: an `open`'s body, bindings after an `OpenBind`); the
  closed-term rule, `shift_term`, generalization's closedness check,
  `term_mentions_var` and the recursive-payload closer all read it. This fixed
  generalization under match branches and around `perform`
  ([core-traversals-count-binders-separately](wayfinder/tickets/core-traversals-count-binders-separately.md)).
- Running a program (`Ctx.run`, the REPL) is unbudgeted.
  See [checker-evaluation-budget](wayfinder/tickets/checker-evaluation-budget.md).
- **Macro applications are calls under the same budget** (M5). The depth fuel
  (256, reserve/release) is deleted. A macro application spends one call and
  opens a request that its body evaluation (fresh metas, shared budget,
  `Nbe.apply_macro`) and the expansion of its output spend from, so a nest of
  applications is bounded as a whole: breadth blowup at bounded depth is an
  `Expand_error` `BudgetExceeded` naming the innermost macro and, for a syntax
  operator, its site - the application installs its error on the budget, so
  the overrun is raised with its site and nothing re-catches it. The driver's
  own elaboration requests report overruns through `Elab_entry.reporting_budget`
  like every other checker request
  ([expansion-errors-reach-the-user-raw](wayfinder/tickets/expansion-errors-reach-the-user-raw.md)). A type-aware call's request also
  covers elaborating its output.
- **Expansion failures are error values** (M8): kind mismatch, non-syntax
  result, non-declaration result, self-expansion during definition and a
  missing callback are `Expand_error.Error { error; site }`, with the syntax
  operator's use and declaration spans as the site. No `failwith` remains in
  `expand.ml`, and the catch-all that re-wrapped a macro body's exceptions as
  strings is gone
  ([macro-fuel-is-the-evaluation-budget](wayfinder/tickets/macro-fuel-is-the-evaluation-budget.md)).
  Any evaluation failure inside a macro application (`panic`, division by zero,
  a runtime match failure) is `Expand_error.EvalFailed` with the application's
  site: the evaluator fails through one helper, `Nbe_support.fail`, which raises
  the error of the macro application running under the budget, or `EvalError`
  outside one. Primitive reducers return a failure instead of raising
  ([macro-body-eval-errors-lack-site](wayfinder/tickets/macro-body-eval-errors-lack-site.md)).

### Macro model enforcement and one IR (2026-09-14)
- **One IR.** `Surface.t` and lowering are deleted; the elaborator reads expanded
  `Syntax.t`, so ids, paths and spans reach it
  ([delete-surface-ir](wayfinder/tickets/delete-surface-ir.md)).
- **Hygiene.** Every macro application (untyped, type-aware, decl, operator) goes
  through `Expand.application`: use-site and intro scopes on what it receives,
  intro flipped on what it returns. `quote(…)` builds syntax with
  definition-site scopes and holes typed by position (`Expr`/`Pattern`/`Id`).
  Scope sets are opaque `Scopes` values. Macros no longer capture their
  arguments, and template literals resolve at the definition.
- **Reflection is total.** The prelude's `Syntax` ADTs have one constructor per
  form (one `and` chain), and the round trip is the identity on every field.
- **Name resolution.** Local binders always get fresh resolved names
  (`x__0`). A path's head is an id. A bare name resolves to a binder or to an
  **open choice** (the candidate opens by scope set, then the shadowed binder),
  settled by the elaborator against each open's members; no bare name is found
  by spelling among locals. A path's head (qualified pattern heads, record-pattern
  types, `perform E.op`, effect branches, `impl M.Trait`) resolves the same way,
  and traits and nominal types are located through the entry the head resolves
  to - by trait identity, or by applying a type former - not by a name-keyed
  table or an environment scan. `impl M.Trait(..)` and `[A : M.Trait]` now work
  ([names-resolve-without-spelling](wayfinder/tickets/names-resolve-without-spelling.md)).
  Still by spelling: a constructor label matched inside a scrutinee's known nominal.
- **Macro type binders are explicit.** `macro m[A](x) : Expr(A)` binds `A` (a
  reflected type, `Syntax.R`, unless annotated), so arity is syntactic. (Superseded
  2026-09-15 by macro signatures, above: any number of binders, `T` checked.) The uppercase rule, the
  `Macro_resolver` pass, the parse-time adapter and `: A` binders are deleted
  ([macro-type-binders-should-be-explicit](wayfinder/tickets/macro-type-binders-should-be-explicit.md)).
  `Elab_infer` no longer special-cases `EffectRow` or `stx_` names
  ([elaborator-matches-names-by-spelling](wayfinder/tickets/elaborator-matches-names-by-spelling.md)).
- **Macro bodies** elaborate inside the unit opens around their definition,
  nothing ambient (M3). Units that write macros open the prelude themselves.
- **Types.** `type A = … and B = …` chains are mutually recursive nominals. Nested
  patterns through recursive positions work (they read constructors by nominal id).
- **Syntactic roles (M7).** Raw tokens carry scope sets and every id takes its
  token's. A syntax form or operator resolves by scope set (largest subset,
  ambiguity loud); units, modules, structs and blocks scope their tokens, so
  syntax shadows syntax lexically and a template's replacement sees roles as of
  its definition. Template intro scopes go on replacement tokens: syntax a
  template names itself is invisible to user code, and a hole may name a
  generated declaration (`syntax $n { $n $x => … }`, `infix ($op) …`). A role
  never mixes with another binder of its name: syntax declarations survive as
  `SyntaxBinding` / `SyntaxDef`, imported roles are seeded into the expander,
  and the binder funnel raises `RoleConflict` in either order (application-written
  binders and a fixity attached to its value excepted); an open supplying a
  visible role's name is `OpenSuppliesRole`
  ([template-heads-resolve-by-scope-set](wayfinder/tickets/template-heads-resolve-by-scope-set.md)).
  Enforestation still precedes expansion; the expander-driven loop rides on M9.
- M9 (templates desugar to macros) is complete; see "Macro model M9, run 3".
  The macro model's remaining distances are in the design map.

### Macro system — Stages 0–10
- Stages 0 through 10 are complete: substrate, hygiene, expansion, phase-aware imports,
  enforestation, syntax templates, computed ADT-based syntax API, kind-tagged macros,
  Decl/Pattern ADTs, type-aware macros. See [macro status](wayfinder/macro-system/STATUS.md).
- The Stage 10 annotation-name disambiguation limitation is resolved on the
  semantic driver path: annotations are resolved against the current prior
  type namespace (builtins, user types, value aliases, and qualified
  imported types via `Expr(M.T)`). See
  [type-aware interleaving](wayfinder/macro-system/TYPE_AWARE_INTERLEAVING.md).
- Type-aware interleaving migration Stages 1–9 are done: AST split, static
  list removal, `Macro_driver` skeleton, prelude-type constraint resolution,
  canonical per-binding kind registration via injected callback,
  macro-generated declaration re-entry (generated `MacroBinding` nodes
  compile/register, generated siblings thread scopes), scoped per-binding
  semantic advancement (top-level source-order prior user type/record
  declarations now constrain later macro annotations), recursive-macro
  safety infrastructure (top-level provisional macro registration/rollback plus
  macro expansion guarded by the evaluation budget, shared across copied contexts),
  driver-based import loading (`Macro_driver.visit_macros` compiles imported
  public macros through a full driver run, so their annotations resolve in
  the imported module's own context), and retirement of the old
  `Core_loader.visit_macros` parser-heuristic path. Same-Decl generated
  type→macro interleaving and transformer-level self-recursive macro
  bodies are deferred.

---

## Mostly complete / in progress

### Traits and trait stdlib/pub semantics
- Trait declarations, `impl` declarations, structural dictionary evidence,
  trait-bound implicit parameters, qualified method calls, public module/struct
  impl evidence all implemented.
- Remaining: explicit deriving/fallback behavior as library-level type-case code;
  more protocol-style operations. See [traits](wayfinder/topics/traits.md) and
  [trait module/stdlib](wayfinder/topics/trait-module-stdlib.md).

---

## Active / deferred

### The .NET backend's remaining slices
- 1c-ii: `Struct`/record/tuple, `Proj`/`Dot` on a record, `Module`/`Imported`, `open` of a module;
  1d: refs and effect state machines; 1e: traits/dictionaries and type-case; 1f: typed emission
  from the annotation pass; 2: `quill build`. The interpreter stays the reference until parity.
  Staging, acceptance and measurements: [.NET backend](wayfinder/topics/dotnet-backend.md). Slices
  1a′, 1b and 1c-i (the nominal tier) are closed. `Sequential` (21 refusals) and `TypeSwitch` (3)
  are **1e's, not 1c's** — measured 2026-10-20: every `Sequential` case is `match (T)` on a `Type`
  with `NominalHead`/`AtomType`/`StructType` arms, i.e. type-case.

### Macro Stages 11–12
- Stage 11 (macro-powered language features) and Stage 12 (macro diagnostics &
  expansion UX) have no specification yet. See [macro status](wayfinder/macro-system/STATUS.md)
  and [macro implementation plan](wayfinder/macro-system/IMPLEMENTATION_PLAN.md).

### Annotation scope disambiguation / type-aware interleaving
- Migration Stages 1–9 are done; semantic annotation resolution is active for
  driver-based module compilation and for imported macro modules. Remaining
  deferred items: same-Decl generated type→macro interleaving,
  transformer-level self-recursive macro bodies, mutually recursive macro
  groups, semantic `resolved_type_ref` constraint identity (constraints are
  still recorded by name and resolved at the use site), and resolved-export
  cache fingerprinting (the macro cache is still keyed by module path). See
  [macro interleaving design](wayfinder/topics/macro-interleaving-design.md),
  [type-aware interleaving](wayfinder/macro-system/TYPE_AWARE_INTERLEAVING.md),
  and the [direction map](wayfinder/quill-design-map.md).

### Private type visibility
- Design-only task using the OCaml/SML model (private types become abstract outside
  their defining module). See [private type visibility](wayfinder/topics/private-type-visibility.md)
  and the [direction map](wayfinder/quill-design-map.md).

### Generated symbol cleanup
- Trait declaration markers, struct nominal hashes, and other compiler-internal
  generated symbols should be replaced with explicit structural representations.
  See [generated symbol cleanup](wayfinder/topics/generated-symbol-cleanup.md).

### Enforester improvements
- Structured errors with spans, fault-tolerant parsing, spec-oriented combinators.
  See [enforester improvements](wayfinder/topics/enforester-improvements.md).
