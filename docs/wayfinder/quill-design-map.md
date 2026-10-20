---
title: Quill Compiler Design Map
labels:
  - wayfinder:map
status: open
---

# Quill Compiler Design Map

> **Local-tracker note:** this repo has no `docs/agents/issue-tracker.md`, so the
> map uses the local-Markdown convention. Each ticket is a file under
> [`tickets/`](tickets/); a ticket's **title is its name** and links to its file.
> Blocking is expressed in ticket front-matter (`blocked_by:` lists ticket
> filenames) since there is no native tracker UI. The **frontier** is the open,
> unblocked, unclaimed tickets — see [Open questions](#open-questions).

This map is the single navigation hub for the **direction** of the `quill` compiler:
what has been decided, what is still open, and what is still fog. It is an
**index, not a store** — each decision is gisted here in one line and links to the
topic doc that holds its detail.

## Notes

- **Domain:** `quill`, an experimental dependently-typed language built on `core_tt`
  (bidirectional elaboration, NbE, implicit args, nominal ADTs, structural
  records/modules, traits, algebraic effects, references), with a hygienic
  enforestation-based macro system. The implementation is the C# port of `core_tt`
  at the repository root — `src/` (compiler), `std/` (prelude), `test/` (suite and
  runners). The OCaml prototype was deleted 2026-09-25 once the port measured as a
  superset of it; its code is in `git log`.
- **Design priority:** Consistency > Flexibility > Correctness. One construct for
  many roles (`struct` = record/module/namespace), types are values, the core
  carries the language model directly. See the root [`README.md`](../../README.md).
- **Source-of-truth split:**
  - **What is *built*** — [`docs/STATUS.md`](../STATUS.md) (overall) and
    [`macro-system/STATUS.md`](macro-system/STATUS.md) (macros). Canonical; when
    other docs disagree on completion status, STATUS wins. These track *state*,
    not direction, and live outside the decision flow on purpose.
  - **How to *work* in the repo** — root [`CLAUDE.md`](../../CLAUDE.md).
  - **What is *decided* / *open*** — this map and its tickets.
  - **Macro reference library** — [`macro-system/`](macro-system/) (design plans,
    papers, extracted Klister notes; its own [`INDEX.md`](macro-system/INDEX.md)).
- **Skills:** prefer `ocaml-alcotest` for test/debug sessions; use `/grilling` and
  `/domain-modeling` when resolving a fog-heavy ticket.
- Do not trust stale "not started / pending" prose in old plan docs when the
  STATUS files disagree — the STATUS files are authoritative.

## Decisions so far

Completed directions. Each line gists the decision; the linked topic doc holds the
detail. (Build-completion status lives in [`docs/STATUS.md`](../STATUS.md).)

### Core type theory & foundations
- [Struct-as-module unification](topics/struct-as-module.md) — one `struct`
  construct serves as record, module, and namespace. The refinement the topic body
  records: `module` and `struct` are separate constructs sharing member syntax, not
  interchangeable at the type level — a module is never a type, a record struct is not a
  module signature.
- [Dependent core (`core_tt`)](topics/dependent-types.md) — the old HM pipeline
  was replaced by a dependently-typed core with bidirectional elaboration + NbE.
- [Record types](topics/records.md) — structural record types over constructor fields.
- [No subtyping](topics/subtyping.md) — convertibility (NbE) is the only equality; records
  are structural and exact. The one width rule is module↔signature unification, already
  landed and pinned by `core-021`.
- [Qualified paths](topics/qualified-paths.md) — `M.x` path resolution in the core.
- [Type-specialized equality](topics/type-specialized-equality.md) — equality
  dispatched by type rather than a single generic operator.
- [Pipeline wiring checklist](topics/pipeline-wiring-checklist.md) — the how-to for
  threading a new feature through every pipeline stage (reference/checklist).
- [Impl visibility](topics/impl-visibility.md) — **decided:** impls arrive through
  `open`, matching OCaml's modular implicits, but only after **named impls** land
  as a compile-time handle (`same[A.C, A.eq_C](…)`). Ordering is the decision:
  scoped resolution with no way to name an impl is the one combination no
  precedent uses. Global coherence is unavailable to a language whose modules are
  values.
- [Domain model — elaborate ↔ evaluate](topics/core-tt-domain-model.md) — first
  pass of the port's specification: a context is one sequence seen through
  columns, and the evaluator holds one of them rather than a peer environment.
  *Scope* stays the hygiene word and is not a synonym for it. Vocabulary in the
  root [`CONTEXT.md`](../../CONTEXT.md).
- [Domain model — surface and enforestation](topics/core-tt-domain-model-surface.md)
  (closed) — second pass of the port's specification: the reader commits to
  nothing but grouping, enforestation is parsing interleaved with expansion,
  resolution is sets-of-scopes (largest subset wins, ambiguity loud), expansion
  alpha-renames values only, and lowering is safe only because expand already
  ran. The three macro paths measured against the model's one hygiene contract
  — every divergence a defect ticket; `Surface.t` named as an erasure plus one
  escape hatch, not a language level. Vocabulary in the root
  [`CONTEXT.md`](../../CONTEXT.md).
- Constructor lookup matches the type name
  (closed) — premise was stale; the symptom already passed. Pattern-head
  resolution tries the type name first and the constructor name second, and that
  order is now written down in one place instead of hand-inlined in two.
  Also fixed `type T = T I64`: dotted field lookup took the first matching field
  while `open` and `do` bindings take the last, and a type and its constructors
  were bound as scope-set siblings rather than nested. Last-wins is now one named
  helper, `Core.find_field_last`, instead of seven hand-written scans.
- [Core traversals ignore binder depth in binding lists](tickets/core-traversals-ignore-binding-list-depth.md)
  (closed) — instrumented all four cases and ran the suite: never reached. The
  condition that makes them safe is now named and enforced, rather than
  arithmetic being invented for a case that has never occurred.
- [The base context borrows the importer's mutable expander state](tickets/base-context-shared-state.md)
  (closed) — probed every field `unit_base` shares with the importer. The shared
  macro table does not collide (deferred calls carry unit keys, not written
  names), the shared metas are load-bearing, and strictness holds. The one real
  defect, the importer's `expand_ctx` being repointed at the last imported unit's
  expander, is deleted; it was never load-bearing.

### Language features
- [Regression coverage](topics/regression-coverage.md) — approach for locking in
  current behavior; **ongoing**, lowest-risk near-term work.
- [Type-case / generic programming](topics/type-case-generic-programming.md) —
  direct matching on `Type` values; **complete**.
- [Record type reflection](topics/record-type-reflection.md) — `struct … end` type
  patterns over constructor fields; **complete**.
- [Traits / ad-hoc polymorphism](topics/traits.md) and
  [trait module/stdlib semantics](topics/trait-module-stdlib.md) — nominal
  `trait`/`impl`, structural dictionary evidence, bound implicits; **mostly
  complete** — remaining deriving/protocol work is
  [Design trait library deriving and protocols](tickets/design-trait-library-deriving-and-protocols.md).
  A trait's parameter may now be a **type constructor**, not only a type
  (`trait Functor(f) = sig { map : … f(A) … }`, `51d79c8`), with the constructor
  inferred at a use by a bounded, default-off approximation (`MetaContext.FoApprox`,
  after Lean's) — `std/functor.qll` is the first consumer.
- [Algebraic effects](topics/algebraic-effects.md) — nominal effect families, open
  rows, `perform`/handlers/`resume`, deep handlers; **complete**.
- [References](topics/references.md) — `Ref(A)`, `ref`/`deref`/`<-`, cell-capturing
  closures; **complete**.
- [Domain model — effects](topics/core-tt-domain-model-effects.md) — fourth pass
  of the port's specification: the vocabulary and E-invariants behind the five
  effect tickets (bare arrow pure, checker budget, lexical handling + handler
  scope, three heap effects with discharge, applicative-by-purity), each marked
  enforced or decided-not-implemented. Vocabulary in the root
  [`CONTEXT.md`](../../CONTEXT.md).

### Backend and execution
- [A .NET backend](topics/dotnet-backend.md) — **decided 2026-10-13**, and re-decided the same day
  on its target: the backend **emits IL** (`System.Reflection.Emit` + `PersistedAssemblyBuilder`),
  not C# source, because F#'s coverage of mutual and higher-order tail calls rests on the `tail.`
  prefix and C# has no syntax for it — measured: with `--tailcalls-` both shapes die at 300k frames
  with exit 134, while self tail survives as a loop with zero prefixes. Value model **native .NET
  types**; effectful code **a state machine only where the effect row needs it** (accepted limit: a
  tail call inside a machine consumes CLR stack, because `tail.` is illegal there — F# turned tail
  emission off in its async path rather than solving it); slices accepted by diffing `Backend.Run`
  against `Driver.Run` over the value-producing cases. Baseline measured: interpreter `fib(30)`
  **13.6 s** against **3.4 ms** for the same program as C#. The interpreter is **not** deleted — it
  is the elaborator's normalizer, the import evaluator and the macro runtime. Alternatives and
  emitted shapes: [the two choices](topics/dotnet-backend-choices.md); what the term looks like to
  an emitter: [the term shapes](topics/dotnet-backend-term-shapes.md).
  **Slice 1a′ landed 2026-10-17** — IL emitter for atoms/primitives/`Var`/`Let`/`Lam`/`Ap`/`Fix`
  plus the one literal-`Switch` match shape, `quill run`, and a `--parity` mode; verified on the
  merged tree at xUnit 217, conformance 967/0, parity **352 match / 0 differ / 548 not ported**, and
  20M-deep self and mutual tail recursion both surviving with `tail.` really emitted (1 and 4
  prefixes byte-scanned out of the image). **That parity number overstates 1a′'s coverage ~3×** —
  of the 352, only **111** are the backend agreeing on a value (the rest never ran it); see the
  2026-10-19 frontier entry. Its successor is
  [slice 1b](tickets/backend-slice-1b.md), filed 2026-10-19.
- [Backend slice 1b](tickets/backend-slice-1b.md) — **landed 2026-10-19 (`926eb49` + `b416d65`) and
  closed 2026-10-20 (`cfc55e7`)**: compile the prelude **value graph** (prelude functions arrive as
  `VLam` over a `Closure`, so a `Var` needs closure conversion, not a primitive special case) and
  the rest of `Match` (`Destruct`, payload bindings, nominal keys). Gate `fib(30)` → `832040`
  through `Backend.Run`, against the interpreter's **22.5 s** on this machine — met by both the
  `match` and, from `cfc55e7`, the `if` spelling (`0.81 s`). Current parity numbers live in
  STATUS, not here.
- [The elaborator records a type for every term it builds](tickets/elaborator-records-term-types.md)
  — **open, and the typed tier's prerequisite**: `Lam(Term Body)` carries no domain and no
  annotation table exists, so a typed emitter only gets the signatures the surface wrote
  (`rec fib : I64 -> I64` arrives typed through `Let.Type`; `(fn(x) { x + 1 })(7)` does not).
  The keying decision (node identity, not structure, with the collision argument) is made and
  documented; the recording is still incomplete.

### Macro system
- [Design type-aware macro interleaving handshake](tickets/design-type-aware-macro-interleaving.md)
  (closed & **implemented**) — semantic module driver with an explicit ordered
  queue, split unresolved annotations vs resolved kinds, per-binding semantic
  advancement, provisional recursive-macro registration + fuel, and driver-based
  import loading. Design detail:
  [macro-interleaving-design](topics/macro-interleaving-design.md); the full
  nine-stage migration (Stages 1–9) is implemented — see
  [`macro-system/STATUS.md`](macro-system/STATUS.md).
  Its annotation-resolution rule is superseded by
  [Macro type binders should be explicit](tickets/macro-type-binders-should-be-explicit.md)
  — binders move to `macro m[A](x)`; names in `: Expr(…)` only refer.
- Macro Stages 0–10 (substrate, hygiene, enforestation, syntax templates,
  kind-tagged macros, Decl/Pattern ADTs, type-aware macros) are complete; the
  design library lives in [`macro-system/`](macro-system/).
- [Domain model — macros](topics/core-tt-domain-model-macros.md) (closed) —
  third pass of the port's specification: reflection is **total** and the
  round trip must be the identity (today lossy in both directions — every
  loss ticketed); one hygiene contract governs every application, template
  heads included; macro applications count under the **one evaluation
  budget**, retiring the depth fuel; a macro's name exists from its
  definition's start (provisional), and its body elaborates in its definition
  site's scope, nothing ambient. The dead `phase` field deleted.
- [Bool and `if` as library features](topics/bool-and-if-as-library.md) — Stage 11's
  first increment: `Bool` demoted from a primitive to a prelude nominal ADT
  (`False | True`), primitives return `I64`, and `if` desugared to `match` with
  `Core.If`/`FIf` removed (sound — `FMatch` subsumes them). Implemented, 778 tests
  green. Stage 11 (["macro-powered language features"](tickets/specify-stage-11-macro-powered-language-features.md))
  stays **open** as the umbrella for further demotion increments.
- [Unify procedural macro call syntax with function calls](tickets/unify-macro-call-syntax-with-functions.md)
  (closed & **implemented**, commit `0441c9a`) — `@` removed, macros invoked as
  `f(args)`; macros live in the scope-aware binding table with a Value/Macro kind
  tag (one namespace, innermost-lexical shadowing).
- [Add short-circuit && / || operators](tickets/add-short-circuit-and-or-operators.md)
  (closed & **implemented**) — Stage 11 increment: stdlib `pub infix` templates
  expanding to `match` over Bool (short-circuit for free), seeded like `if`; the
  lexer was simplified alongside: `&`/`|` join `operator_chars` (so `&&`/`||`
  lex by maximal munch, no per-operator rules), redundant rules and the dead
  `At` token removed. Settled line: operator space lexes uniformly, but
  structural punctuation (`|` Bar, `->`, `=`) keeps dedicated tokens.
- [Explicit prelude open for operator demotion](tickets/explicit-prelude-open-operator-demotion.md)
  (closed & **implemented**) — the arithmetic/comparison operators and prefix
  `not` are demoted out of the compiler into the prelude as `pub infix` /
  `pub prefix` declarations (`base_operators()` is just `<-`); the global mutable
  `builtin_syntax_hook` ref and the blanket operator seed are deleted. Prelude
  syntax is delivered under a **strict phase rule**: only where `std` is opened,
  resolved through `load_syntax` on the reserved `import "std"` path (in statement
  order). `Open` carries a module expression; the REPL and program-eval entry
  points open `std` by default. Two loose ends spun out as their own tickets
  (module-level `open`; retire `load_imports_in_terms`/`open_stdlib`).
- [Retire load_imports_in_terms and the open_stdlib survivor](tickets/retire-static-import-harvest-and-open-stdlib.md)
  (closed & **resolved**) — investigated both mechanisms the operator-demotion
  ticket bypassed; both are load-bearing, so both are **kept with documented
  rationale** (no behavior change, 792 tests green). `load_imports_in_terms` can't
  route through the in-order `parse_import` harvest — removing it breaks eager
  circular-syntax-visit detection for template bodies — so it stays as one
  documented use. `open_stdlib` is the persistent ctx-builder counterpart of
  `on_macro_body`'s per-expression `Open (import "std")`; both share
  `open_module_value`, and `Macro_driver`'s advancement hook needs the ctx-builder
  form, so it stays (its dead `ix` return dropped).
- [Module-level open form (strict imported modules)](tickets/module-level-open-strict-imported-modules.md)
  (closed & **implemented**) — `open <module-expr>` is now an item of a module or
  struct body (`Syntax.OpenBinding`, `Core.OpenBind`), scoping over the
  *subsequent* bindings only and exporting nothing. The operator harvest rides on
  the existing in-order `parse_import`; the runtime scope extension shares one
  helper with the expression-level `Open` so de Bruijn indices stay in lockstep
  with `open_module_value`. `Enforest.parse_module`'s `?open_prelude` parameter is
  **deleted**, along with the blanket harvest in `Core_loader` and
  `Macro_driver.visit_macros`: imported modules are now strict about prelude
  **syntax** exactly like expressions, and write `open (import "std")` themselves.
  `parse_expr` keeps the flag — a bare expression has nowhere to write the open.
  The prelude source stays special (`std` must not open itself). 809 tests green.
  One loose end spun out: prelude *values* still reach a module through the
  importer's context (see the ticket below).

## Fog

Dim directions — real, but not yet sharp enough to be tickets. Signposts, cleared
as the frontier reaches them.

- **The emitted backend's two value tiers, and the type mismatch that compiles clean.**
  Sharpened 2026-10-19 by the seam bug at slice 1b's merge, kept as fog because the reachable
  instances are fixed, the one remaining copy is latent (filed below), and the general form has no
  program behind it. `Quill.Kernel.Value` is
  the **evaluator's** tier; the emitted program runs in a separately loaded assembly and can hold
  only the **emitted** tier (`Quill.Backend.Runtime`'s types). The mismatch is silent by
  construction: passing `Value.VCon` where emitted code produces `Runtime.Con` *compiles*, passes
  xUnit and the suite, and fails only at run time inside emitted code — and only on the inputs
  that reach the code path at all. The same shape has a second form: a helper emitted code calls
  must be `public`, because the emitted assembly cannot call `internal` (`MethodAccessException`,
  again only at run time). **Cheap when it sharpens:** a convention check over
  `src/Quill.Backend/` — every type emitted code touches is public, and no member its helpers read
  is a `Quill.Kernel.Value` — would have caught both instances. Not written, because two known
  instances picked off by hand do not yet justify a check; the third one earns it. See
  [Payload still reads the wrong tier](tickets/backend-matchops-payload-wrong-tier.md) and
  [the term-shapes topic](topics/dotnet-backend-term-shapes.md)'s *"The seam this cost a merge"*,
  which records both failure modes with their exact exception names.
- **The closure-conversion assumption: is every reachable closure's `Environment` a context
  prefix?** Fog, from the same session. `Emitter.Prelude.cs` compiles a prelude `VLam` as a
  **static** method — no closure object, no capture list — which is only sound because every
  closure reachable from the ambient was evaluated over a prefix of the same context chain, so
  `Var i` can be read as `closure.Environment[i]` directly. It is evidenced for `i64_to_bool`
  (whose `Environment` holds exactly the `True`/`False` `VCon`s) and **not proven in general**. A
  non-prefix capture would read wrong ambient entries *silently* — no throw, just a wrong value —
  which makes it the worst failure class in this part of the backend. **Sharpens when** a closure
  is found whose environment is not a prefix — or, more cheaply, the first `--parity` **differ**
  appears that no refused node explains (a `differ` is *not* a `not ported`: the backend ran and
  produced the wrong answer, which is why `differ` must stay 0 while `not ported` falls). Deciding
  it needs a probe over `Context.Environment` at closure-creation sites (`Nbe.cs`'s `Term.Lam`
  case and the value-graph walks), not a design session. **2026-10-20, evidence bearing on it:** the
  first `differ`s since this entry was written (3 of them) appeared when half of the closure fix
  landed, i.e. the "sharpens when" condition was met — and they were **not** a non-prefix capture.
  They were the `EmitSwitch` tier bug, and the ambient read that produced the 1b `Nominal` residue
  was a *local count* one binder too high, not a wrong environment. So the fog stands as written,
  with one more way a wrong ambient read can look like a non-prefix capture.
- ~~**CLR / C# rewrite shape**~~ — the rewrite happened: promoted out of fog to
  [port `core_tt` to .NET (C#)](tickets/port-core-tt-to-dotnet.md), in progress from
  2026-09-16, **at parity 2026-09-25** (`port-fails: 0`, the prototype deleted the same
  day) and lifted from `dotnet/` to the repository root 2026-09-26. What remains is
  the open-ticket list, not a rewrite.
- **Formalized core semantics as cross-rewrite truth** — proposal to write
  `Core.term` / `Core.value` / `eval` (and optionally elaboration) as a Lean 4 (or
  Coq) spec used as an AI-checked structural-correspondence reference across the
  port. Detail: [formalized-semantics](topics/formalized-semantics.md). Needs the
  vocabulary from [domain model for `core_tt`](tickets/domain-model-core-tt.md)
  first.
- **Surface syntax after the macro model settles** — whether broad surface syntax
  (possibly less ML-flavored, Ruby/Elixir-style `do … end`) should change once the
  macro expansion model is finalized; the syntax redesign now enters through
  reader/enforestation rather than grammar growth.
- **Diagnostics polish boundary** — which diagnostics work belongs now vs later. The
  pre-rewrite/​post-rewrite split that framed this is gone with the prototype: there
  is one implementation again, so the boundary is now "what a reaching test needs"
  vs "broad polish once the surface is stable".
  Concrete input: ~~elaborator errors carry **no source location at all**. `Elab_error`
  has no span fields. Since delete-surface-ir the elaborator reads `Syntax.t`, so
  every form it sees has a span; attaching them to errors is the remaining work.~~
  **Re-measured 2026-10-01: that premise is wrong on all three counts.** (1) An elaborator
  error *does* carry a position on the path that reaches it — `Budget._site`
  (`Budget.cs:23`) is set by `Elaborator.At` (`Elaborator.cs:180`), `Budget.Where()`
  (`Budget.cs:50`) prints it, and `BudgetTests.cs:56` asserts the text. (2) The prototype's
  `elab_error` **did** carry `site : Eval_budget.site option` on exactly the two variants the
  port carries one on (`EvaluationBudgetExceeded`, `EvaluationFailed`), and `Elaborator.cs:184`
  reproduces its message shape verbatim — no regression. (3) "Every form it sees has a span"
  is false: `Pattern` (all fourteen variants, `Syntax.Match.cs:21`) and `EffectRow`
  (`Syntax.cs:24`) have none. The 130 `new FunException(` sites bucket 73 / 35 / 22
  (span already in scope / must be threaded from a caller / no source form behind it).
  **Decided 2026-10-01 — the consumer is `--file`**, which is how a probe is read: every elaborator
  error will name the form it was at. Work in
  [every elaborator error names the form it was at](tickets/elaborator-errors-carry-their-position.md).
  When someone reaches for it the cheap move is **one line** (append
  `Budget.Where()` to the catch already at `Elaborator.cs:183`) — and its real cost is not the
  line but the nine xUnit assertions that compare an exact `Message` (`PrimitivesTests.cs:46,69,89`,
  `EffectTests.cs:50`, `PreludeTests.cs:40`, `InterleavingTests.cs:19`, `RecTests.cs:57`,
  `ReaderTests.cs:65`, `ExpandTests.cs:104`), each of which would have to grow the suffix — not a
  field on the exception;
  `Quill.Expand`'s 159 message-only exception sites are the cheaper population if expansion-side
  positions are ever wanted, and `Driver.cs:38-46` is the funnel that discards those.
  **Ruled 2026-10-01:** error *recovery* is out of scope — counted, 20 of 238 `error` cases are refused
  inside `Quill.Expand` and every one is single-error, so nothing asks for a second message. The
  expansion-side *span* stays live in
  [enforester improvements](tickets/scope-enforester-improvements.md), where the starting point is that
  two `struct-field-*` cases already print a position.
- **Syntax highlighting** — *sharpened 2026-10-04: the fog is now the API, not the consumer.* The
  surface is still hostile to a context-free grammar (enforestation, operator demotion, scope-aware
  binding), but the analysis has left `docs/ideas/` for
  [the syntax highlighting server](topics/syntax-highlighting-server.md) (`afea2f2`): a long-running
  compiler server over msgpack-RPC, tree-sitter's *pattern* rather than its API, the first slice of
  [the first-class compiler API](topics/first-class-elaborator-api.md) as the server's tree
  interface, and "good enough static grammar" explicitly rejected. A best-effort tree-sitter grammar
  and nvim queries exist meanwhile (`editor/tree-sitter-quill/`, `520ea20`) and are allowed to be
  wrong about operators and macros. What stays fog: the first-class API is designed and unbuilt, and
  the surface may still move. The old ticket is closed as superseded.
- **Keywords are reserved; the syntax spec says they are not.** `SYNTAX_SPEC.md:40` presents its list
  as "(not reserved; can be shadowed)" while `Keywords` (`TokenTree.cs:44`) *is* the reserved table —
  and the claim is observably false, not merely undocumented: probed 2026-10-04, `{ do = 1; do }` and
  `{ let = 1; let }` die in the reader's default arm, `{ type = 1; type }` and `{ match = 1; match }`
  at rules that consume the spelling first. The precedent points the other way (Stage 11 increment 2
  and `fun` freed spellings by *deleting* them), so either the spec's parenthetical is wrong or
  shadowing needs a phase that can resolve a name — fog until one of those is ruled. Ticket:
  [the keyword reservation mismatch](tickets/keywords-are-reserved-but-the-spec-says-shadowable.md).
- ~~**`Self` names two things**~~ — decided 2026-09-15: `Self` is the struct being defined; the recursion placeholder is an unwritable "recursive occurrence" (see mutually-recursive-record-types). — the record-recursion placeholder
  (`VSelfType args`, set by record declarations) and the partial struct type seen
  by struct method bodies (`elab_infer.ml`, `Struct` case). Both share
  `ctx.self_type`. Giving the record one an identity
  ([self-type-has-no-identity](tickets/self-type-has-no-identity.md)) may force
  them apart; whether they should be one concept remains a naming question —
  the
  [surface domain-model pass](tickets/domain-model-surface-enforestation.md)
  closed without taking it.
- **Library-level features vs compiler machinery** — how much future feature work
  (deriving/fallback, protocol-style ops, UFCS, FFI/native bindings) should be
  library-level macros / type-case rather than new compiler machinery. UFCS and FFI
  are desirable but should not drive the prototype agenda now.
- ~~**Too many IR layers — can one be removed?**~~ — researched in
  [Syntax.t vs Surface.t](tickets/syntax-vs-surface-ir-layer.md). `Surface.t` is
  `Syntax.t` with information thrown away, and it is now deleted (ticket below).
  The other layers (`Raw_syntax`, `Core.term`, values) were not examined.
- ~~**Primitive ↔ symbolic-name wiring is stringly-typed and fragile**~~ — promoted
  out of fog to [one declaration per primitive](tickets/unify-primitive-declaration.md);
  it blocks the port.
- **First-class compiler API** — expose as much of the compiler as is honest to
  expose to downstream users, at compile time and out of process: inference,
  unification, goal inspection, normalization, the budget, reflection. The
  elaborator and unifier are the core, but the ambition is that a tool, an LSP, a
  REPL and a macro all sit on one surface instead of re-implementing the
  elaborator — Unison's UCM over a compiler-as-library is the precedent. Today the
  only thing expansion may ask of elaboration is the fixed `IMacroRuntime`
  adapter, and `Quill.Expand` cannot reference `Quill.Compiler` by design, so this is an
  interface-widening decision (`Driver.Elaborate` already admits its callers are
  just the runner and the CLI).
  Detail: [first-class-elaborator-api](topics/first-class-elaborator-api.md).
  Sharpens when the first real downstream consumer appears and "can this be a
  library over reflection instead?" has a measured no.
- **Universes and level polymorphism** — replace `Type : Type` with `Type(i)` over
  first-class level expressions (`max(i, j)`, `i + 1`), so meta-libraries and
  category-theoretic abstractions are written once instead of duplicated per tier.
  The singleton `Core.U`/`VU` and the deliberate simplification recorded in
  [dependent-types](topics/dependent-types.md) are the starting point; the cost is
  level unification, cumulativity and every `Type` occurrence in the pipeline.
  Detail: [universe-levels](topics/universe-levels.md). Sharpens when the first
  abstraction that must quantify over arbitrary types is written and cannot be
  duplicated per tier.
  **Probed 2026-10-03 — the sharpening was attempted and did not hold.** The first
  consumer the topic named, a `Functor` trait, is blocked by a **kind**, not a tier:
  `ElaborateTrait` typed every trait parameter as `Type`, so `f(A)` was `applying
  non-function` while `fn(f : Type -> Type, a : Type) : Type { f(a) }` evaluated to
  `VNominal`. Fixing the kind is small and landed — trait 965/0, xUnit 210/210 — and
  the real distance is named in
  [a trait parameter may be a type constructor](tickets/trait-parameter-type-constructor.md).
  So under `Type : Type` there is **one** universe: nothing is "duplicated per tier"
  when there are no tiers, and the only thing a hierarchy buys is soundness —
  Girard's paradox — which the topic already records as a deliberate trade. Fog stays
  fog, and the trap to avoid is re-reading this item's *stated* motivation as a
  consumer: the arc that reached it was a trait-system defect, not a universe level.
- **Content-addressed codebase database** — the Unison architecture
  (<https://www.unison-lang.org/>): definitions identified by the hash of their
  AST, names as separately stored metadata, with expansion / elaboration /
  unification results cached under that hash permanently and across runs instead of
  keyed by file path in the `Loader`'s per-process dictionaries — which is where
  Unison's "no builds" comes from. Two obstacles are specific to `quill`: scope sets
  are per-run integer sets (`Quill.Kernel/ScopeSet.cs`) so a hash needs a
  scope-normal form, and the interleaved driver makes a cache key a *(definition,
  context)* pair rather than one hash. This is orthogonal to language semantics and
  pays off only with a persistent store; the codebase-layout decisions already open
  ([the `std` restructure](tickets/restructure-std-into-bootstrap-and-library.md),
  [the bootstrap↔compiler interface](tickets/declare-bootstrap-compiler-interface-once.md))
  come first and would have to be hash-shaped. Detail:
  [content-addressed-codebase](topics/content-addressed-codebase.md). Sharpens when
  compile time is measured and attributed, or a hash-addressed library story is
  wanted.

## Open questions

### Frontier (2026-09-26 → 2026-10-20)

**2026-10-20 — 18 tickets open** (`status: open` under `tickets/`), suite **967 cases, 0 failed**, xUnit **217/217**, `--parity` **900 cases,
381 match, 0 differ, 519 not ported** (`real coverage: 140 value-match`). Slice **1b is closed**:
both halves landed 2026-10-19, and its one residue closed 2026-10-20 (`cfc55e7`) — measured to be
neither of the two things the parked probe was going to choose between. The tickets it left:

- **[Compiling a prelude closure reaches a `Nominal` it cannot emit](tickets/backend-prelude-closure-reaches-nominal.md)**
  — 1b's one residue, **closed 2026-10-20**. The guess in it (`Let.Type` / a `Pi` domain / a value
  in the closure's environment) and the split-out ticket's dichotomy ("skip a type position" vs
  "the first real demand for a nominal value") were both wrong in the same way: the `Nominal` was
  reachable only through a `Var` index that was **off by one**. `EnsureClosureMethod` bound one
  parameter per `Lam` in the closure's chain and then emitted the body with those `Lams` still on
  it, so each was bound twice and the body's ambient lookups all shifted one entry — `i64_to_bool`
  became `Option`, and `Option`'s value is a `VLam` over a closure whose body is the `Nominal`, so
  the emitter was compiling a type former as a function. One line, the `LamChain` helper that was
  already in the file unused. It exposed `EmitSwitch` comparing a boxed-`I64` object-tier
  scrutinee against `Ldc_I8` (`Bne_Un` across tiers — not IL, never equal, so always the default
  arm: `i64_to_bool(0)` was `True`), which is why the two fixes had to land together; the strip
  alone turned 3 suite cases into `differ`s. Lesson worth carrying: **a `not ported yet` message is
  a claim about the term, not a measurement of it** — here the term was right and the index was
  wrong.
- **[`MatchOps.Payload` still reads the interpreter's `Value.VCon`](tickets/backend-matchops-payload-wrong-tier.md)**
  — the second copy of the seam bug fixed in `b416d65`, filed rather than fixed because it is
  unreachable today (`Runtime.Con` has no payloads) and an unexercisable fix is the kind that looks
  verified and is not. It detonates at run time inside emitted code the moment 1c adds payloads.
- **[`--parity` exits 0 when it found differs](tickets/parity-exits-zero-on-differ.md)** —
  **closed 2026-10-20**, same session it was filed in. `RunParity` ended `return 0;`
  unconditionally, so the one mode that can see a backend/interpreter disagreement reported
  success to everything but a human reading stdout; it now ends `return differ == 0 ? 0 : 1;`,
  with `not ported` deliberately not failing the run. Both seam bugs were invisible to `build`,
  xUnit and the default runner mode; only a human-read `--parity` line found them. Half a day
  after the mode's coverage fix (`59894d6`) paid for itself, this was the other half of making
  it usable as a gate.

**Suite, xUnit and parity are the integrator's own run**, at the merge of both 1b halves. Slice
1b's own reconnaissance is in its ticket.

- **[Backend slice 1b](tickets/backend-slice-1b.md) was filed and landed the same day**, so the
  frontier's next step is no longer it. Slices 1a′–2 existed only as prose in
  [the backend topic](topics/dotnet-backend.md)'s staging table, so no fork could be briefed on
  one. It was filed with two measured corrections to that table, both of which held: **`Con` is a
  prerequisite of 1b, not 1c** (because `i64_to_bool` — `std/bootstrap.qll:6` — returns
  constructors, so `if (n < 2)` and every prelude predicate need one; the table now says so), and
  **`if` lowers to `Destruct`, never `Switch`** (1a′ carries one literal-`I64` `Switch` shape
  only). It stayed open on its one residue, the `Nominal` ticket above, until `cfc55e7`
  (2026-10-20).
- **The parity harness was overstating coverage ~3×, and now does not.** The 352 "match" split
  measured as **value-match 111, both-errored 6, both-elaboration-failed 235** — a case whose
  elaboration fails was counted as a match without either path running. Since there are 658
  value-producing cases, real coverage is **111/658 ≈ 17%**. `--parity` now prints
  `real coverage: N value-match (+… both-errored, +… never elaborated)` and a per-node
  `NOTPORTED` histogram, so a fork's report cannot inherit the inflated number. **The fix paid for
  itself the same day**: both halves of slice 1b reported clean builds, and only `--parity` showed
  the 17-differ regression at their seam.
- **The histogram is the shape of 1b.** `Module` 112, `decision tree` 106, `implicit
  application` 61, `EffectDecl` 45, `Nominal` 43, `Struct` 41, `Imported` 37, `TraitRef` 34. Those
  are **first**-failure counts: instrumenting the elaborated term per unported case shows a median
  of **11** distinct node kinds still missing, and **zero** cases needing exactly one — so no
  single node kind completes any case, and the unit of progress is a slice.
- **Two constructs no slice owns.** `Term.Module` and `Term.Imported` have **no emitting case** in
  `Emitter.cs` and are named by no slice of the staging table, yet `Module` is the largest
  first-failure bucket — and since the whole prelude arrives as one `Module`, every later slice
  meets it.
- **Slice 1b landed in both halves** (1b-ii `926eb49`, 1b-i `b416d65`) — the `Match` walk over
  `DecisionTree`s (`Emitter.Match.cs`) and the prelude value graph (`Emitter.Prelude.cs`:
  `Runtime.Con`, `EmitVar`'s ambient fallthrough, closure conversion over `Closure(Environment, Term)`).
  `decision tree` refusals **106 → 17** (Sequential 14, TypeSwitch 3 — both deliberate). `Backend.Run`
  now handles `{ True }`, `{ match (True) { … } }`, `{ if (True) { … } }` and fib(30) in its
  `match` spelling (`832040`); fib's `if` spelling then hit the same residue as `{ 5 <= 6 }`,
  reported just above, which was the only reason 1b stayed open until 2026-10-20.
- **Two halving lessons from 1b, both paid for.** (1) **A `Destruct` cannot run without a
  constructor *value***, so the halves are coupled by their gates even though the code is
  severable — proven by ablation (6 cases land on `EmitDestruct`'s body with a `cases=2 at=Base`
  tree and re-block at `prelude value`); the coupling is hidden because `EmitMatch` validates the
  tree at `Emitter.cs:326` *before* emitting the scrutinee, so the same missing prerequisite
  reports two different messages. (2) **The seam between two parallel forks cost a merge, and only
  `--parity` could see it**: `MatchOps` was `internal` (emitted code is a separately loaded
  assembly → `MethodAccessException`) and read `Value.VCon` where emitted code produces
  `Runtime.Con` (→ `InvalidOperationException` on the 17 cases that reach a `Destruct`). Both
  compiled clean and passed xUnit; neither branch alone could fail on either. **Briefing rule this
  earns: a fork's unmeasured suite numbers are unmeasured, and the integrator runs parity before
  believing a green build.**
- **`elaborator-records-term-types` merges green and pays off nothing yet.** Verified by merging
  `fork/elaborator-records-term-types` into a scratch worktree and running it: 967/0, xUnit 217,
  parity unchanged. Its hole table reproduces (`Fix` 0/1 and 0/2, `Lam` 0/2, `Struct` 1/3). Its own
  ticket says the decisive thing — *nothing consumes the table yet* — so it is enabling work, not
  the work; its three owed items (delete the scratch `ProbeDumpTests.cs`, make `CoverageProbe` an
  assertion, record `Fix` first) belong to whichever fork next touches the elaborator.
- **`fib(30)` is the gate and the payoff is real, measured after 1b:** the `match` spelling returns
  **`832040` through `Backend.Run` in 0.59 s against 6.70 s through the interpreter** (`--file`, one
  process each, same machine); the `if` spelling was blocked by the `Nominal` residue above until
  `cfc55e7`, where it returns `832040` in **0.81 s**. The 0.9 s refusal at `backend decision tree`
  that the reconnaissance recorded is the pre-1b state.
- The 2026-10-13 entry below is left as written, with one correction: its fork bullets name two
  forks "running on the re-cut work", and both have since reported — 1a′ merged, the annotation
  finisher unfinished (above).

**2026-10-13 — 17 open** (`dotnet-backend` stays open as a container, like the three port
umbrellas; one ticket filed, none closed). Suite at HEAD unchanged by this session's docs work —
nothing under `src/`, `std/` or `test/` was edited, so the counts below are the map's own
2026-10-04 run, not a re-measurement. **Re-decided later the same day:** the backend's target is
**IL**, not C# source — see the Backend and execution entry under *Decisions so far* for the
measurement that forced it (F# needs `tail.` for mutual and higher-order tail calls; C# cannot
write it; with `--tailcalls-` both shapes die at 300 k frames).

- **Three fork branches were verified by me, not believed.** `fork/backend-slice-1a` (a Roslyn
  slice 1a, now dead on its target but green): 967 cases 0 failed, xUnit 217, and its parity
  harness reports **900 cases, 359 match, 0 differ, 541 not ported** — a partial backend that never
  once disagreed where it covered, which is the bar the IL slice inherits. `fork/elaborator-records-term-types`:
  967 / 0 and xUnit **218**, but **incomplete** (only three partials have recording call sites; its
  last act was to announce starting the rest) and carrying a scratch probe promoted to a test that
  writes `/tmp/probe-dump.txt` and asserts nothing. The survey fork wrote no document at all.
- **Salvaged from the two failures rather than discarded:** `tools/Quill.DumpTerm` (511 lines,
  prints the elaborated term and `Context.Environment` as de Bruijn indices with types — the
  instrument the emitter forks were missing) joined the solution so it cannot rot, and the term
  shapes that were actually dumped are written down in
  [the term-shapes topic](topics/dotnet-backend-term-shapes.md), with the rest listed as open.
  From the dead Roslyn branch: `Runtime.cs`, the CLI and the parity harness.
- **Two forks are running on the re-cut work**: slice **1a′** (IL emission, with a 20M-deep mutual
  tail pair as the acceptance test that proves `tail.` actually works) and the finisher for the
  annotation pass. Both use a different agent type than the one whose provider hit its usage limit.

- **[A .NET backend](tickets/dotnet-backend.md) is ruled.** Four answers, recorded on the ticket
  and detailed in [its topic doc](topics/dotnet-backend.md): emit **C#** compiled in-process with
  **Roslyn**; value model **native .NET types**; acceptance for slice 1 is **`Backend.Run`
  diffed against `Driver.Run`** over the 651 value-producing cases; recursion **direct style with
  a depth guard**. The ticket's "compile time is unmeasured" premise is now measured: `fib(30)`
  takes **13.6 s** through the interpreter and **3.4 ms** as C#, against a 0.54 s whole-program
  floor (0.19 s of it `dotnet` startup).
- **Two probes decided more than the ruling did, and both went against the convenient answer.**
  (1) **C# cannot emit tail calls** — the CLR's `tail.` prefix is real but Roslyn never emits it,
  and the JIT did not loop-convert even the textbook self-tail shape (ok at 250 k frames,
  stack overflow at 280 k, exit 134, uncatchable) — so "hand optimization to dotnet" buys speed
  but not unbounded recursion, which is a **language-semantics change** the topic doc states
  plainly. (2) **`Core.term` is untyped**: `Lam(Term Body)` has no domain and no annotation table
  exists, so the typed tier has a prerequisite, filed as
  [the elaborator records a type for every term](tickets/elaborator-records-term-types.md) —
  slice 1 emits typed signatures only where the term carries one.
- **The emitter's input is a (term, value-environment) pair, not a term** — dumped, not assumed:
  the program arrives as `Let{ Def = Module{… the whole prelude …}, Body = … }`, its members are
  reached by de Bruijn index, and indices past it land on primitives in the base context. So
  prelude functions, trait dictionaries and structured records reach the emitter as *values* with
  terms inside them, which is where the closure conversion happens and why the prelude needs no
  special case.

**2026-10-04 — 16 open**, suite **967 cases, 0 failed, xUnit 217/217** (the integrator's own run on the
merged `f63047d`). Five tickets the paragraph below counts as open were **stale rather than open**, one
new ticket was filed, and both tickets this session's forks built are **closed** — see the first two
bullets:

- `record-the-absence-of-subtyping` (closed) — ruled and recorded in `351369d`; `topics/subtyping.md`.
  Only the frontmatter lagged.
- `impl-head-written-bound` (closed) — the semantics landed `418f28b`; the binder error is live at
  `Elaborator.Traits.cs:274`. Its surface *spelling* was always the Stage 11 spec's.
- `negative-tuple-arity-and-crash-class-mutants` (closed) — all three items under *Remains* measured
  (`b3fa0fc`, `ddd138d`), and `--isolated` is in the runner. The surviving note is a process rule: the
  sweep runs isolated.
- `design-std-library-surface` (closed) — 19 decisions `d70567d`; its last unshipped piece closed
  2026-09-28 and both impls are in the library today (`std/list.qll:297`, `std/option.qll:46`).
- `syntax-highlighting` (closed) — superseded by `topics/syntax-highlighting-server.md`; see the fog
  item above.
- **[Load std units in any order](tickets/load-std-units-in-any-order.md) is ruled, not open** — the
  hand-written `Prelude.Order` is replaced by a DFS post-order walk from `std/stage2`, and the derived
  order (`bootstrap, lib, list, type, option, functor, stage2`) is *accepted* against today's
  (`… option, functor, type …`): the two incomparable halves make "what is below me" a tie-break, not a
  fact. `Std` inside a std unit and metas seeding follow the derived order. Option 1, demand-loading, is
  filed separately as [demand-load the std units](tickets/demand-load-std-units.md).
- **[Load std units in any order](tickets/load-std-units-in-any-order.md) is ruled *and built*.** The
  hand-written `Prelude.Order` is gone: `DeriveOrder` (`src/Quill.Compiler/Prelude.Order.cs`) walks the
  import graph from `std/stage2`, and the derived order — `bootstrap, lib, list, type, option, functor,
  std` — is asserted against the real sources rather than assumed. The two incomparable halves make
  "what is below me" a tie-break, not a fact, and `Std` inside a std unit follows the derived order
  (accepted in the ruling; the suite is the check). Option 1 is filed as
  [demand-load the std units](tickets/demand-load-std-units.md), unblocked by this landing.
- **[A refinement/evidence desync is an invariant](tickets/evidence-desync-is-an-invariant.md) is built
  too.** `Context.Refinement` carries the branch's target, and a failure that the Evidence rewrite
  would have answered now throws `InvalidOperationException` instead of blaming the user with
  `missing implementation of …`. Proven by an ablation that printed its build's exit code first — the
  rule this map's last harness note earned.
- **`fun` was freed, and one more mismatch filed** — `vestigial-fun-keyword` closed. The spelling was
  reserved with no rule behind it, and what rejected it was the enforester's *default* arm
  (`Enforest.cs:302`), which is the ticket's finding made executable; `{ fun = 4; … }` is now
  `values/freed-keywords.qll` (`VALUE 10`), red before the change. Its open question — the spec says
  keywords are *not* reserved, `Keywords` reserves them — was **probed rather than argued**: `do`/`let`
  die in the reader's table, `type`/`match` at rules that consume them, so not one spelling in that
  list is shadowable. Filed as fog rather than fixed, at the user's ruling. The count stays 16: one
  closed, one filed.
- **Harness lesson, repeated and paid for.** Both forks were spawned without
  `isolation: "worktree"`, so they ran in *this* checkout: the first one cut the branch
  `evidence-desync-invariant`, the second committed on top of it, and no `main` commit was made while
  they ran. Nothing was lost, and the integration was a fast-forward — but the worktrees were the
  whole point, so the parameter is part of the brief, not an assumption.
- The three port umbrellas **stay open as containers**: `port-parity-plan`'s own words are "the port's
  parity frontier is empty", but it holds two unowned residues (the rec-enum unnamed-capture throw, and
  the synonym collector's narrower-than-prototype walk) that are one probe away from being tickets, and
  `port-unported-path-audit` records "none is unowned". `specify-stage-11-…` keeps one live item:
  `ref`/`deref` as primitives.

**2026-10-03 — 22 open** (frontmatter count, the three port umbrellas among them),
**suite 961 cases, 0 failed, xUnit 210/210** — the 2026-10-01 session closed 17 open with six forks
plus one follow-up, every one merged and re-measured by the integrator rather than believed; three
more tickets have been filed since, and the elaborator-error position landed (`bff4bbe`).

- **[The coverage gaps are closed](tickets/coverage-gaps-from-the-mutation-sweep.md)**
  (`8eb3df6`+`799b04e`, merged `79ee1f1`/`7868b73`). All eight rows the sweep left are resolved:
  seven cases, each proven by neutralising the guard it names and watching the case flip **alone**
  (`958 cases, 1 failed` / `961 cases, 1 failed`), plus `ref-tuple-negative` closed as unflippable at
  both its candidate guards. Two findings about `src/` came with it: `Nbe.cs:586`'s guard is
  unreachable by any program (its own comment says so; its reachable sibling is cased instead), and
  `Primitives.cs:165`'s refusal is observable only as a **process-killing stack overflow** — the one
  failure a conformance case cannot pin, and the one the runner cannot classify. **Measured
  2026-10-03:** the refusal is a race between that overflow and the 60s elaboration timeout,
  `elaborate/tuple-negative-arity.qll` pins it, and `--isolated` classifies both — see
  [the negative-tuple-arity ticket](tickets/negative-tuple-arity-and-crash-class-mutants.md).
- **A mutation no longer aborts the suite**
  (`008720b`, merged `6053ea3`) — filed closed, because the sweep had recorded it without a ticket.
  Two mutations that killed the run at exit 134 now report per case: `hard failure (T): msg`, A →
  `954 cases, 9 failed`, B → `1 failed`, run completes, exit 1. The runner's two hand-kept exception
  lists had drifted, which is the real defect: a whitelist maintained twice stops being a whitelist.
  This fix is what made the coverage fork's evidence standard possible at all.
- **[`type-case-refinement`](tickets/type-case-refinement-walks-whole-context.md) lost its last
  code-shaped item** (`4623a3b`+`d16caa7`). Ablating each of the seven `Refined` channels one at a
  time shows **all seven pinned**, and answers the ticket's own item 3: `Opened` and
  `ConstructorEntries` are distinct paths — a *term* use reads the entry's type from `Opened`, a
  *pattern* head reads binder types from `ConstructorEntries`, and each flips only under its own
  ablation. What the ticket still owes is two rulings and a deferred index.
- **Two premises were wrong, and both were wrong in the direction that wastes work.** The fog item
  "diagnostics polish boundary" (`86b4166`) claims elaborator errors carry no source location, that
  `Elab_error` has no span fields, and that every form the elaborator sees has a span: **false on all
  three** — `Budget._site` already records the form and `Budget.Where()` prints it (`BudgetTests.cs:56`
  asserts the text), the deleted prototype's `elab_error` carried `site : Eval_budget.site option` on
  exactly the two variants that still have one, and `Pattern` (14 variants) and `EffectRow` have no
  span at all. It stays fog: no consumer needs a position today. And
  [`prelude-abi-remaining-spellings`](tickets/prelude-abi-remaining-spellings.md)'s "stale doc comment"
  item had already been fixed (`fdd7646`) — three tickets still asserted otherwise.
- **The enforester ticket is forkable at last** (`d334221`).
  [`scope-enforester-improvements`](tickets/scope-enforester-improvements.md)'s topic doc is pure
  `enforest*.ml`, so every claim was re-derived against `src/Quill.Expand` with counted numbers
  (`Enforest*.cs` 2804 lines, 173 throws, **0** carrying a span, 0 hits for spec/combinator/Pratt).
  Pratt, incremental parsing and the rest bug are **dropped**; span-carrying expansion errors is the
  one buildable item — and the framing fact is that spans already exist on every token while
  `Driver.cs:38-46` throws them away. Error recovery has **no workload** and stays unruled, which is
  the [type-case-refinement](tickets/type-case-refinement-walks-whole-context.md) failure mode caught
  before it was repeated rather than after.
- **[The suite ticket's two open calls](tickets/suite-redundancy-measured.md) are now cheap or dead**
  (`6c13f28`). The `elab-NNN` clusters are larger and differently shaped than the ticket said (two
  areas, 179+78 files) and **not one pair is byte- or whitespace-identical**, so ~19 semantic clusters
  each need a human read (four files deletable — one per pair in clusters 1–3, plus one of the
  249/250 pair); the xUnit → `cases/` migration is **8 of 123 methods
  and not worth doing**, because its payoff is `ok` cases and round two measured that **1 of 67**
  `ok` cases was caught by any of its 73 mutations.
- **Five doc corrections, all measured.** `CLAUDE.md` listed `Reflection` under `Quill.Expand` (it is in
  `Quill.Compiler`), described `std/` as `stage1.qll`+`stage2.qll` (gone since `16f9948`), and both it
  and `README.md` advertised a **REPL that is a stub** — `src/Quill.Cli` prints "no entry point yet" and
  exits 1. Also corrected: the `Prelude.Stage1Path` claim, the sweep's "something escapes it"
  paragraph (now naming what escaped), and the coverage ticket's own "none of them attempted" index.
- **[The negative-tuple-arity refusal, and crash-class mutants](tickets/negative-tuple-arity-and-crash-class-mutants.md)**
  (filed 2026-10-03) — the guard at `Primitives.cs:165` is the assertion that `Tuple(n, …)`'s first
  argument is positive, and it stays; `elaborate/tuple-negative-arity.qll` pins it (the mutant fails
  by the 60s timeout or by a stack overflow in `Unify.Mentions` — a race), and the runner's
  `--isolated` mode makes both one case's failure. Remains: the crash-class shape has no in-process
  case, and the sweep should run with `--isolated`.
- **[A .NET backend](tickets/dotnet-backend.md)** (filed 2026-10-03) — the first direction ticket that is not a
  language or macro question: lower `Core.term` to something runnable on .NET, the runtime the
  compiler already sits on. The analysis lives in `docs/ideas/compiler-architecture.md` (verdict:
  *undecided and unscheduled* — emit C, a native dependency, or a code generator plus collector
  over the host); what a backend would touch is a new pipeline stage after `Core.term`, the .NET IL
  handler constraint (no stack to save — evidence passing), and the FFI fog item, which the ideas
  doc says must be settled first. Not scheduled: no consumer, compile time unmeasured, and the
  second-backend cost is recorded (Skew shipped four and died of a small standard library).
- **[A trait parameter may be a type constructor](tickets/trait-parameter-type-constructor.md)**
  (closed 2026-10-03, `51d79c8`) — the measured end of the universes fog: the `Functor`
  trait that item named as its sharpening consumer is blocked by a trait-parameter
  *kind*, not a universe *tier*, so tiers keep no consumer and stay fog. Both halves
  landed: the parameter's type is a meta the sig solves, and the constructor is inferred
  from the argument by a first-order approximation shaped as Lean's `foApprox` — a named
  flag defaulting to off, bounded to a spine matching the head's *trailing* captures, and
  enabled at one argument check. Instrumenting the failing unification is what settled
  that rule; an equal-arity match would have declined every real case. 965/0, xUnit
  210/210, and with the flag at its default the refusal still stands.
- **[Load std units in any order, pinning only the bootstrap](tickets/load-std-units-in-any-order.md)** (filed 2026-10-03) —
  adding `std/functor` needed an edit to `Prelude.Order`, a hand-maintained topological
  order for the whole standard library; without it the import is `not found` **even
  though the source is embedded and the build is clean**. Four readers depend on that
  array (path→stage, the units below an importer, the unit bound as `Std` while a std
  unit elaborates, and metas seeding), and the bootstrap is genuinely special and stays
  pinned. Wanted: demand-load, or the cheaper option of deriving the same list from the
  import graph. Five open questions, among them what `Std` means inside a std unit
  (today, whatever the array put directly below — arbitrary) and what replaces
  `prelude[^1]` for metas seeding once the graph branches.

**Rulings still owed, all the user's:** the `d`/`e` branch-local-type question and the `Evidence`
mechanism ([type-case-refinement](tickets/type-case-refinement-walks-whole-context.md));
[the typed operator macro](tickets/port-typed-operator-macro.md)'s shape;
[a written bound on an impl head](tickets/impl-head-written-bound.md); `Id` as type vs hole-kind and
`Block`'s placement ([prelude-abi](tickets/prelude-abi-remaining-spellings.md)); whether error
recovery has a workload at all ([enforester improvements](tickets/scope-enforester-improvements.md));
and two this session surfaced — ~~delete the `elab-NNN` twins~~ (**settled 2026-10-01**: 093/094/095
were deleted, `961 → 958`, and `249/250` is not a pair — `deref(1)` reads a non-reference while
`1 <- 2` writes to one, two different refusals, nothing deleted; the suite ticket's own closing
section has the readings) — and whether one line of
`Budget.Where()` is worth the nine xUnit message assertions it breaks.

**Harness note (not a project artifact).** All three original forks died at once on
`zai-coding-cn`'s weekly cap, at ~21 s with `0 tokens, 0 tool uses` — nothing was lost, and the
lesson is that one provider per writer *and* reader is a single point of failure: `wright` now runs
on `opencode-go/mimo-v2.6-pro`, and the read-only agents are on a separate bucket. Two forks were
lost to procedural slips rather than to model error — a run against a **stale mutated DLL**, and my
own ablation whose `dotnet build` exited 1 — so the rule this session earned is: **an ablation must
print its build's exit code before its observation counts.**

**Session history (2026-09-26 → 09-29).** The port's parity work closed: every item the
2026-09-25 re-sweep listed landed, the OCaml prototype was deleted, and `dotnet/` was lifted to
the repository root. The suite grew 857 → 961 cases, 0 failed. Four rulings from this period:

- **The pattern grammar** (2026-09-27): [a pattern binder is lowercase; naming an existing term
  takes `^`](tickets/pattern-binders-are-lowercase-and-references-are-pinned.md) — case decides a
  name's role in every pattern position. Two stages, additive first (no existing expected value
  changed), then the rule and its call-site migration.
- **The library surface** (2026-09-27): [design the user-facing library
  surface](tickets/design-std-library-surface.md) — 19 decisions, 31 bindings, `d70567d`. Its two
  `Eq` impls are parked as [Eq for List and Option](tickets/std-eq-for-list-and-option.md), blocked
  on two compiler gaps.
- **Type-case refinement** (2026-09-29): [the seven entry
  channels](tickets/type-case-refinement-walks-whole-context.md) rewritten, `63df1df` — the 37.8 s
  refinement case at 1.02 s. The ticket stays open on the `d`/`e` ruling and the `Evidence`
  mechanism.
- **The last live unported path** (2026-09-28): closed with the struct-former refusal and its
  role-separation fix. The frontier is no longer a port wedge.

Umbrellas keep their own children: [the port](tickets/port-core-tt-to-dotnet.md),
[the parity recipe](tickets/port-parity-plan.md), and the
[unported-path audit](tickets/port-unported-path-audit.md).

### Language and macro work

- [A local open expression, `M.(expr)`](tickets/local-open-expression.md)
  (closed) — **not adopted**: a block already opens a module for one expression
  (`{ open M; e }`) and `M.(e)` expands to the same `Open`, so it is a second
  spelling for one construct. If the double-bracketed enum-match idiom grows into
  an irritant, reopen with call sites as evidence.
- [A type-aware operator macro](tickets/port-typed-operator-macro.md) — **after the
  port** (user ruling, 2026-09-24): an operator declared as a macro with a typed
  signature. Neither implementation has a working one — the prototype hangs, the port
  refuses — so this is post-port work and not a port gap, and it needs a shape decision
  (one argument for the whole use, or one per operand) before it is built.

- [Specify Stage 11 macro-powered language features](tickets/specify-stage-11-macro-powered-language-features.md)
  — umbrella for demoting built-in constructs to library. Direction decided;
  increment 1 (Bool + `if`) landed; increment 2 (2026-09-16) deleted five unused
  keyword tokens (`then`, `with`, `end`, `else`, `Unit`) after finding `if`,
  `&&`/`||`, the operators and `type` already live in the prelude. **Stays open**:
  `ref`/`deref` as primitives needs a decision (fresh heap per call).
- [Mutually-recursive nominal type declarations](tickets/mutually-recursive-nominal-types.md)
  (closed) — `type A = … and B = …` chains, elaborated by one three-phase knot.
  Nested patterns through recursive positions (broken for every recursive type,
  `List` included) now read a placeholder's constructors by nominal id.
- [Mutually-recursive record type declarations](tickets/mutually-recursive-record-types.md)
  (closed & **implemented**) — identity at the knot (Go-style): a `rec` struct
  binding mints an identity, its body sees a recursive occurrence that unfolds on
  demand and compares by identity; same-shape recursive records are distinct.
  `rec A = … and B = …` groups hold struct types.
- [Recursive records cannot hold a record](tickets/recursive-records-cannot-hold-a-record.md)
  (closed) — an occurrence unfolds where a shape is needed, so `Some(l1)` fits.
- [Self type has no identity, so unrelated recursive records unify](tickets/self-type-has-no-identity.md)
  (closed) — occurrences compare by identity.
- Mutual type chains in scoped do-heads
  — deferred remainder of the nominal ticket: `and` chains rejected in scoped
  `do type … ; body` heads until expression-position group knots are designed.
- [Reflect Match in the Expr macro ADT](tickets/reflect-match-in-expr-macro-adt.md)
  — enables a true prelude-macro `if`, `matches?`, pattern DSLs, `derive`. Design
  decided via grilling; unblocked (mutual nominal types landed).
- [Specify Stage 12 macro diagnostics and expansion UX](tickets/specify-stage-12-macro-diagnostics-and-expansion-ux.md)
  — diagnostics scope pre- vs post-rewrite.
- [Design trait library deriving and protocols](tickets/design-trait-library-deriving-and-protocols.md)
  — library-level deriving/protocol ops for traits. Now carries two inputs from
  [impl-visibility](topics/impl-visibility.md): deriving inherits the
  mandatory-open tax, and scoped impl resolution gives up coherence, which any
  ordered or hashed collection would inherit as a soundness hazard.
- [Design private type visibility model](tickets/design-private-type-visibility-model.md)
  — decision on private/opaque type visibility.
- [Scope generated symbol cleanup](tickets/scope-generated-symbol-cleanup.md)
  — when to replace generated compiler symbols with structural forms.
- [Scope enforester improvements](tickets/scope-enforester-improvements.md)
  — which enforester improvements to do pre-rewrite.

### IR layers (from [Syntax.t vs Surface.t](tickets/syntax-vs-surface-ir-layer.md))

- Type-aware macro output is not expanded
  (closed) — both type-aware call sites share one helper that expands the output
  before lowering; a non-syntax result is an error naming the macro, not a hole.
- [Procedural macros capture use-site variables](tickets/procedural-macros-capture-use-site-variables.md)
  (closed — one application contract, use-site + intro scopes) — **diagnosed** (pass-2 model): the `Syntax.t` ↔ value round-trip drops scope
  sets (`value_to_id` hardcodes empty), so a spliced argument loses its
  occurrence scope and the macro's binder captures it. Templates' splices are
  clean — their written literals are the remaining hole:
- [The syntax round trip is lossy](tickets/syntax-round-trip-is-lossy.md)
  (closed — reflection total, round trip the identity) — **found by pass three**: the round trip loses scope (at both ends), type
  annotations, explicitness, `PatCon` paths and span positions, and degrades
  silently (`?` ids, garbage→`False`, skipped bindings). The general defect
  that the capture ticket is the soundness slice of.
- [Template literals resolve at the use site](tickets/template-literals-resolve-at-use-site.md)
  (closed — resolved names always fresh, path heads are ids, one template instantiation)
  — a use-site `False = 42` silently turns the prelude's own `&&` into a
  constant-42 machine; replacement ids are re-enforested at the use site and
  the declarer's scope never reaches them. Found by the pass-2 model.
- [Macros have no quoted syntax](tickets/macros-have-no-quoted-syntax.md)
  (closed — `quote(…)` with typed holes; bare names resolve to binders or open choices)
  — ids can only be built from strings with empty scope sets, so the
  implementation resolves them by spelling at elaboration. The model: quoted
  syntax resolves at the definition site; a context-less id is unbound.
- [Macro bodies implicitly open the prelude](tickets/macro-bodies-implicitly-open-the-prelude.md)
  (closed — a body is elaborated inside the unit opens around its definition)
  — `Macro_driver` elaborates every macro body prelude-opened; the model
  elaborates it in its definition site's scope, nothing ambient.
- [Macro type binders should be explicit](tickets/macro-type-binders-should-be-explicit.md)
  (closed) — `macro m[A](x) : Expr(A)`; names in an annotation only refer, and an
  unbound one is an error at the definition.
- [Delete Surface.t; elaborate expanded Syntax.t](tickets/delete-surface-ir.md)
  (closed) — one IR: the elaborator reads expanded `Syntax.t`, so spans and ids
  reach it.

### Macro model distances still open (from the 2026-09-14 implementation run)

The macro model's M1–M3, M5, M6, M8, M10–M12 and most of M2 are now enforced.
What remains, in the recommended order:

- [Path heads, traits and effects resolve without spelling](tickets/names-resolve-without-spelling.md)
  (closed) — path heads carry an open choice like bare names; traits and
  nominals are located through the entry a head resolves to, and the
  name-keyed trait table and environment scans are gone.
- [Macro type binders should be explicit](tickets/macro-type-binders-should-be-explicit.md)
  (closed, listed above).
- [Macro fuel is the evaluation budget](tickets/macro-fuel-is-the-evaluation-budget.md)
  (closed) — M5 + M8: a macro application is a call under the one budget, its
  body and output spending from the same request; expansion failures are
  `Expand_error` values.
- [Template and operator heads resolve by scope set](tickets/template-heads-resolve-by-scope-set.md)
  (closed & **implemented**) — M7: raw tokens carry scope sets, a role resolves
  by scope set (largest subset), definition contexts scope their tokens, and
  generated syntax is hygienic (a use-site name via a name-position hole). A
  role never mixes with another binder of its name — `RoleConflict` in either
  order, `OpenSuppliesRole` at an open — exempting application-written binders
  and fixity attached to its value. The expander-driven loop and unparsed block
  captures ride on M9; the region rule stays until then.
- [Surface syntax — brace bodies, `=>` arms, explicit semicolons](tickets/surface-syntax-braces.md)
  (closed & **implemented**) — decided with M7: `fn(x) { … }`, `if (c) { … }`, `match (v) { | p => e }`,
  `struct { x: I64 }` record types, `->` only for function types, newlines are
  whitespace, trailing `;` discards.
- [Templates desugar to macros](tickets/templates-desugar-to-macros.md) — M9
  (closed & **implemented**). **Run 3 (2026-09-15):** macro parameters take kinds
  — `(n : Id)`, `(p : Pattern)`, `(b : Block)`, `(d : Decl)` a brace group of
  items whose value is `Decls` — read by the enforester at the call and carried by
  the loader's caches; a token-position `$n` in `quote { … }` names a generated
  rule's head; a declaration hole splices `Decls`.
  **Run 2 (2026-09-14): implemented** — rules as reflected data, one
  instantiation path through `Expand.application`, bodies and items read form by
  form as expansion reaches them, exports from expansion, `Block` token trees,
  `expand_block`, idempotent expansion, unwritable resolved names (`x#n`).
  **Run 1 (2026-09-14):** kinds as types, `quote { … }`, `: Decl` templates and
  `multi`'s removal landed; stopped on five questions in the ticket (bodies inside
  quotes vs typed holes, template macros and the elaborator, where syntax exports
  come from, syntax declarations as data, idempotence). **Grilled 2026-09-14:** `quote { … }` for declarations, `$name` resolves to its
  nearest binder (no quote levels), kinds spelled `$(x : Decl|Id|Pattern|Block)`,
  templates take `: Decl` and `multi` is deleted. Unparsed bodies grilled: `Block` is a readable token tree placeable in any `{…}` slot, bodies stay raw until expansion reaches them, `expand_block` is local-expand, expansion is idempotent. Unblocked (M7 landed); now also
  carries M7's expander-driven loop and unparsed block captures. Retires the
  template region rule in `add_id_scope_if`.

### Found by the budget and path-heads run (2026-09-14)

- [Recursive definitions stuck on open arguments](tickets/recursive-definitions-stuck-on-open-arguments.md)
  — grilled 2026-09-15: recursive calls unfold on open arguments under the checker budget; exhaustion is an error (`HFix` stuck rule goes). (closed, **implemented**): the budget measures work; two calls of one known-pure fixpoint on convertible arguments convert without unfolding.
- [Pattern heads accept type formers](tickets/pattern-head-accepts-type-formers.md)
  — (closed) intended: a function reducing to a nominal works as an alias in a pattern head.
- [Elaborator matches `EffectRow` and `stx_` by spelling](tickets/elaborator-matches-names-by-spelling.md)
  — (closed) both dispatches deleted; a type named `EffectRow` now means itself.
- [The closed-term rule does not look inside closures](tickets/stuck-rule-ignores-closure-environments.md)
  (closed) — a closure is closed when the slots its body reads are; an `open` in the body is conservatively not.
- [A budget error names a core term](tickets/budget-error-names-no-source-call.md)
  (closed) — names the fixpoint, the demanding request and the form's span:
  `calling loop, in an evaluation while reading the type at <file>:1:57-1:64`.
- [Deep non-tail recursion is superlinear](tickets/deep-non-tail-recursion-is-superlinear.md)
  — pre-existing and quadratic: OCaml 5 minor GCs rescan the whole native stack. Architectural (the evaluator recurses natively per object-language call).
- [A qualified constructor nested in a pattern argument is unknown](tickets/nested-qualified-constructor-pattern.md)
  (closed) — root cause was `NomRef` finding a nominal by spelling; it now
  carries the nominal id.
- [A quoted constructor value is re-found by spelling](tickets/quoted-constructor-values-by-spelling.md)
  (closed) — `Con` deleted; a `VCon` quotes to the `Ctor` term carrying its nominal.
- [Expansion errors reach the user raw](tickets/expansion-errors-reach-the-user-raw.md)
  (closed) — an overrun inside an application is its `Expand_error` with the
  operator's site; evaluator errors (`panic`, …) in a body still carry no site.
- [An evaluation error inside a macro body carries no site](tickets/macro-body-eval-errors-lack-site.md)
  (closed) — the evaluator fails through one helper that raises the running
  application's error, so a `panic` in a macro body carries the operator span.
- [Each core-term traversal counts binders on its own](tickets/core-traversals-count-binders-separately.md)
  (closed) — live in generalization's closedness check; every traversal now reads `Core.map_subterms`.
- term_mentions_var ignores inserted metas
  (closed) — it now mentions each slot its mask binds; dependent application no
  longer applies such a codomain to an out-of-scope variable.
- [What a macro annotation constraint means](tickets/macro-annotation-constraints-mean-nothing.md)
  (closed & **implemented**) — a macro's signature (binders, `Expr(T)` parameters,
  `: Expr(T)` output) elaborates at its definition; a typed call waits for the
  elaborator, solves its binders before running and checks the output.

### Found by the M7 and M9 implementations (2026-09-14)

- [Brackets decide grouping](tickets/brackets-decide-grouping.md)
  — adopt Rhombus's structural extents without layout: `{}`/`[]`/`()` group, a
  hole never ends at a bare keyword; Rust-style arms (`A | B => e,`), `|` only
  union; relative precedence in named, transitive order groups; a non-trailing
  hole is one term. Grilled and implemented 2026-09-15 (arms; order groups and
  hole extents on `order-groups`, with `capture-extents` merged in; `assoc(none)`, `<-` group and dotted group references on `order-group-leftovers`; `weakest` groups on `weakest-group`). Open: tail-
  returning forms, dotted group references.
- [Role visibility gaps left by M7](tickets/role-visibility-gaps-after-m7.md)
  (closed) — an import's roles bind in the region of the open or binder that
  imported it; `Import` carries its written scope set, so every open is checked
  against the roles visible where it is written, driver-run opens included.
- [Capture extents are chosen by catching parse errors](tickets/capture-extents-chosen-by-exceptions.md)
  (closed) — a capture reads as far as its parser does, one parse per hole; the
  hole ending a use reads at the form's precedence (`inc 1 * 10` is
  `(inc 1) * 10`); whitespace never ends a capture.
- [Type-case refinement walks the whole context per branch](tickets/type-case-refinement-walks-whole-context.md)
  — the remaining elaboration hotspot after the M9 performance fix.
- Struct items are read together with a private copy of the roles, not form by
  form (M9 run 2 choice; their bodies still wait for expansion).

### Found by the domain-model audit (2026-09-15)

- [Multi-tail effect rows](tickets/multi-tail-effect-rows.md)
  (closed) — a row carries a set of row variables (`->{Log | e1, e2}`), so a `~>`
  result unites its callbacks' rows; a curried `~>` collects on its final arrow.
- [General set literals](tickets/general-set-literals.md)
  — later: `{a, b}` as one Set literal; rows and bounds pinned by position.
- [`export`](tickets/export-construct.md) (closed)
  — `export M` / `export M.{a, b}` re-exports members; `pub open` stays rejected.
- [Effects on the arrow](tickets/effect-arrow-syntax.md)
  (closed & **implemented**) — Unison `A ->{E} B`, open `{E | e}`; `~>` params mint,
  results collect; `can` deleted; pure definitions keep `: T`; bounds `[A : {Eq, Show}]`.
  Limit: a result uniting two row variables needs multi-tail rows (E2).
- [Effects follow-ups from tunneling](tickets/effects-followups-tunneling.md)
  (closed) — `resume` value branch, instance routing, E6 through refs and module
  members, the pure-branch shape, and a written `->{_}` nothing solves is an
  error naming what to write.
- Small follow-ups
  (closed) — every item done: a method's row is expanded in its parameters'
  scopes (`->{Mutate(r)}` names `r`), bounds are the set `{Eq, Show}`, checking-time
  evaluation errors are elaboration errors, refs name the ref and discharge at a
  `let`/block, `EffectRef` resolves by id.
- [A meta in a method's signature captures `self`](tickets/method-signature-metas-capture-self.md)
  — `s.keep(x)` with `keep(r : Ref(I64))` fails at the call; the hidden implicit
  is solved against a spine holding the struct value.
- Solving a meta applied to a spine fails on a dependent right-hand side
  (closed) — `rename` never lifts its renaming under a binder, so a solution
  mentioning its own binders raises `VarNotInSpine`. Fixed in the C# port only.
- [A bare constructor pattern resolves by name](tickets/bare-constructor-pattern-resolves-by-name.md)
  (closed) — `Red => …` finds the scrutinee's constructor with no `open`, even over
  a value named `Red`. Fixed in the C# port only.
- A recursive record whose field has the record's own type cannot be used
  (closed) — `next : L` in `rec L = struct { … }` fails once `L` is a parameter
  type. Fixed in the C# port only.
- A type-case former head takes its arity from the template
  (closed) — `Opt(x)` on a `fn(A) { enum … }` former is `PatternArityMismatch`.
  Fixed in the C# port only.
- [Pattern synonym arguments bind by position](tickets/pattern-synonym-arguments-bind-by-position.md)
  (closed) — `Flip(a, b) = Pt(b, a)` binds its use by position; a synonym through
  `open` is unknown. Fixed in the C# port only.
- Checking a module against a signature takes the first member of a name
  (closed) — `sig { x : I64 }` against `module { pub x = 'a'; pub x = 1 }` fails on
  the first `x`. Fixed in the C# port only.
- [Checking against an implicit function type inserts metas first](tickets/check-against-implicit-type-inserts-first.md)
  (closed) — `g : [A : Type] -> A -> A = id` was rejected; decided it must check,
  and the port does.
- [`panic` with an unknown message fails while checking](tickets/panic-with-unknown-message-fails-checking.md)
  (closed) — decided it stays unevaluated, like every primitive. The C# port does.
- [A reference operation on a value of unknown type is "not a reference"](tickets/deref-of-unknown-type-is-not-a-reference.md)
  (closed) — decided it infers `Ref(?h, ?A)`, like application infers an arrow.
  The C# port does.
- [A method cannot infer its row with `~>`](tickets/method-cannot-infer-row-with-poly-arrow.md)
  (closed) — decided it may. Fixed in the C# port only.
- [A pattern synonym cannot be declared in a block](tickets/pattern-synonym-not-a-block-declaration.md)
  (closed) — decided a block may declare one. The C# port does.
- [`Trait.op` takes the innermost impl whatever its argument](tickets/trait-op-takes-innermost-impl.md)
  — **still open**: it now resolves by the argument's type, but the grilled
  2026-09-18 ruling (a free name in an impl head binds) is implemented nowhere yet.
  Step 2 of [the parity recipe](tickets/port-parity-plan.md).
- Opening a handle on a unit is not an open of that unit
  (closed) — `V = import "v"; open V` does not deliver names `v`'s forms
  introduce. Fixed in the C# port only.

The thirteen above were still listed as open long after the port was correct; the
verification and closure is close the recorded divergences
(2026-09-20), which re-ran both runners against
`test/conformance/prototype-divergences.txt`'s 19 case lines.

### From the divergence review (2026-09-21)

- [Review the recorded divergences](tickets/review-recorded-divergences.md)
  (grilling, in progress) — the 20 lines of
  `test/conformance/prototype-divergences.txt` put in front of the user once, so a
  wrong claim cannot sit in the file unexamined. Settled so far: impl resolution by the
  use's argument types (never the last-declared impl), opening a *handle* on a unit is
  an open of that unit, and a module's public members are unique (below).
- [A module's public members are unique](tickets/public-members-are-unique.md)
  — decided 2026-09-21, **port-only**: a repeated `pub` name in one container and a
  repeated constructor name become errors in the C# port, while the prototype keeps the
  last-wins defect and `prototype-divergences.txt` records it.
- Port wave 2 (parallel forks): recursive types,
  patterns, effects,
  syntactic roles, [traits](tickets/port-traits.md),
  [export](tickets/port-export.md).
- Port wave 1 (parallel forks): [structs, records, signatures](tickets/port-structs-records-signatures.md),
  [match and enums](tickets/port-match-and-enums.md), implicit parameters,
  recursive definitions, imports.
- Checking a lambda ignores its written parameter type
  (closed) — `fn(x : Char) { x }` checked against `I64 -> I64`. Fixed in the C# port
  only; `elab-049` diverges there.
- [Methods follow the arrow rule](tickets/methods-follow-the-arrow-rule.md)
  (closed) — a method is pure unless it says `can`; trait signatures carry rows.
- [Annotation types use a separate grammar](tickets/one-grammar-for-types.md)
  — (closed) one grammar for types; tuple types are `Tuple(n, T1, …, Tn)`, `*` is only multiplication.
- A `List(I64)` parameter infers a wrong type
  — (closed) a NomRef took an applied instance (`Decls = List(Decl)`) for its template; `eval_nominal` now matches only the template.
- [`expand_decls`](tickets/expand-decls-reader.md)
  (closed) — read a `List(Decl)` argument's items, parsed in order (counterpart of `expand_block`).
- [A let-bound signature cannot be a parameter type](tickets/named-signatures-as-parameter-types.md)
  (closed) — `sig { … }` is its own value, a module is never a type; signatures are telescopes (`s.empty : s.T`); impls a signature requires are named.
- [`open` a module parameter](tickets/open-a-module-parameter.md)
  (closed) — `Open`/`OpenBind` carry the members the type lists; each is pushed as a projection.
- [A Decl macro's output is typed](tickets/decl-macro-output-type.md)
  (closed) — `: Decl` is one, `: List(Decl)` many, checked at the definition; a parameter means the same (`(d : Decl)` one, `(d : List(Decl))` a group).
- [Unhandled effects pass the checker](tickets/unhandled-effects-pass-the-checker.md)
  (closed) — effects are computed in one pass during inference; an effect left
  unhandled at a program's entry or a unit's top is an elaboration error.
- The unhandled-effect error says handlers are not implemented
  (closed) — the error names the operation with no handler in scope.
- [A match on a closure crashes the evaluator](tickets/match-on-a-closure-crashes-the-evaluator.md)
  (closed) — only a value with an unknown head makes a match stuck; every other
  value, a closure included, goes through the decision tree.
- [Effect collection rejects a deferred typed macro call](tickets/effect-collection-rejects-deferred-macro-calls.md)
  (closed) — a typed call's effects are read from the output it produced, and
  every effect read follows the elaboration it reads.
- [Records are declared only by let bindings](tickets/records-only-let-bindings.md)
  (closed & **implemented**) — `type X = struct { … }` deleted; a `rec` struct
  binding mints the recursive record's identity; `rec … and …` groups hold
  struct types or mutually recursive values (the nominal `type … and …` knot is not moved yet: that is
  adts-as-let-bindings).
- [ADTs are declared by let bindings](tickets/adts-as-let-bindings.md) (closed - staged prelude; `type` is a std macro)
  — `Option = fn(A : Type) { enum { Some(A), None } }`; `type` becomes a prelude macro.
  **Steps 1 and 2 implemented** (enums, constructor members, `Option.Some`, rec enum
  groups; `pub` before forms, `List(TokenTree)` holes, `type` as an identifier);
  step 3, the `type` macro and migration, remains.
Invariant distances the audit found with no ticket (re-verified on `fa2f32d`).

- [Declaration binders keep their written name](tickets/declaration-binders-keep-written-names.md)
  (closed) — every declaration binder is fresh (`Expand.bind_declaration`); a
  declaration exports its label (`Syntax.label`).
- [A resolved name can be forged](tickets/resolved-names-forgeable.md)
  (closed) — string-built ids are deleted; a reflected resolved name needs the
  certificate its scopes carry; the spelling fallbacks are gone.
- [Opening a module needs its value](tickets/module-open-width-depends-on-value.md)
  (closed) — I2: an open's entries come from its module type, a misaligned value
  is an invariant failure, and a non-module open is `NotAModule` everywhere.
- [Dotted paths found by first match](tickets/dotted-paths-first-match.md)
  (closed) — every member lookup takes the last match (`Core.find_map_last`);
  the prelude nominal names live in `Compiler_names`.
- [The elaborator's macro runtime is mutable](tickets/elaborator-macro-runtime-is-mutable.md)
  (closed) — the field is immutable; a context gets a runtime by construction
  (`Ctx.with_expander`), so an import cannot overwrite the importer's.
- [An under-applied Decl macro is fed a dummy Unit](tickets/decl-macro-fed-dummy-unit.md)
  (closed) — every macro application checks its argument count against the
  declared parameters (`ArgumentCount`) before running; no fabricated arguments.
  Open: a Decl macro's body has no output type, so a polymorphic result
  (`{ Nil }`) is instantiated with a type after the call.
- Refresh the domain-model docs' "today" sections
  (closed) — all four topics refreshed against main; distances name their tickets.

### Effects (from the [domain-model pass](topics/core-tt-domain-model-effects.md))

Decided by the effects domain-model pass; unimplemented, each ticket is the
distance from an E-invariant in the topic doc.

- [Refs belong in effect rows](tickets/refs-in-effect-rows.md) (closed) —
  `Mutate(r)` over a hidden heap (`Ref(A)` takes it implicitly); local heaps
  discharged at function boundaries; top-level refs through the runtime heap.
- [Nominal identity is applicative by purity](tickets/nominal-identity-applicative-by-purity.md)
  (closed & **implemented**) — captures from the enclosing scope; generative
  modules sealed at their binder by identity, with run-time stamps type-case
  separates; `rec` structs under a binder are instances. The last port blocker.
- [A bare arrow is pure](tickets/bare-arrow-is-pure.md) (closed & **implemented**) — `A -> B` is pure;
  `A -> B can _` / `A ~> B` infers the row. `~>` is read by the expression grammar only (type annotations use the hardcoded type grammar).
- [The checker evaluates under a budget](tickets/checker-evaluation-budget.md)
  (closed & **implemented**) — each checker request to the evaluator spends
  from one call budget, and exhaustion is an elaboration error; a fixpoint
  applied to an open argument stays stuck (`HFix`). Running a program is
  unbudgeted. `loop(0)` in a type is now a budget error, not a hang.
- [Handlers tunnel callback effects](tickets/handlers-tunnel-callback-effects.md)
  (closed) — a call with an open row tail tunnels past the handlers enclosing it
  in its function body (`Core.Tunnel`); a match's result may not carry a closure
  whose row names an effect it handles (`HandledEffectEscapes`).
