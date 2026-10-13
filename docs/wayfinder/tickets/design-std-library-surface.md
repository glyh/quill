---
title: Design the user-facing library surface of std
parent: ../quill-design-map.md
labels:
  - wayfinder:grilling
status: closed
closed_date: 2026-10-04
resolution: Closed 2026-10-04 — the surface question is answered and shipped. 19 decisions and 31 bindings landed (`d70567d`), and the last unshipped piece, [Eq for List and Option](std-eq-for-list-and-option.md), closed 2026-09-28; both impls are in the library today (`std/list.qll:297` `pub impl list_eq`, `std/option.qll:46` `pub impl option_eq`, re-exported by `std/stage2.qll`). New syntax this ticket deferred belongs to the Stage 11 spec; a written-bound form is [impl head's](impl-head-written-bound.md) spelling question, also settled.
assignee:
blocked_by: []
decided: 2026-09-27
# unblocked 2026-09-27: restructure-std-into-bootstrap-and-library.md closed (16f9948)
---

# Design the user-facing library surface of std

## Question

After [the restructure](restructure-std-into-bootstrap-and-library.md) gives `std/`
a bootstrap layer and a library layer, the library holds a first cut of the List
and Option API. **What should the library's public surface actually be** — names,
signatures, argument order, what is in versus out, and how it is documented?

This is a grilling ticket: the answers are decisions, so it is worked by interview,
one branch at a time.

## Context

- **Blocked by** [Restructure std into a bootstrap layer and a library layer](restructure-std-into-bootstrap-and-library.md),
  which decides the seam and lands the first cut. Design against the real
  `std/bootstrap.qll` plus library units, not against a plan.
- **The seam is already fixed**: the bootstrap layer is exactly what C#
  names by string (`Syntax` + `Bool`/`Option`/`List`). Their *names* and
  constructors cannot move, so the design must work around them rather than
  through them.
- **Two neighbouring tickets own parts of this, deliberately not repeated here:**
  [Design trait library deriving and protocols](design-trait-library-deriving-and-protocols.md)
  owns deriving and protocol-style ops; [Specify stage 11 macro-powered language features](specify-stage-11-macro-powered-language-features.md)
  owns surface features that would arrive as macros.
- **The map's standing caution** is the fog item *"Library-level features vs
  compiler machinery"*: prefer library-level macros and type-case over new
  compiler machinery, and do not let library work drive the compiler agenda.
- **Docs on the shape of the language** the library must use:
  [`CONTEXT.md`](../../CONTEXT.md) for vocabulary, and
  [`docs/wayfinder/topics/trait-module-stdlib.md`](../topics/trait-module-stdlib.md).

## What the library does not have today (measured 2026-09-26)

`std/lib.qll` after the restructure will hold `if`, `i64_to_bool`, the
comparison/arithmetic operators and their fixity, `Eq` + five impls, and nothing
else. Missing:

- **List beyond the first cut**: no `length`, `head`/`tail`, `concat`,
  `filter`, `zip`, `any`/`all`, `nth`, `take`/`drop`, `range`.
- **Option**: only `Some`/`None`; no `map`, `bind`, `unwrap`/`get_or`, `is_some`.
- **String**: nothing but the primitives `eq_string`/`neq_string`
  (`Primitives.cs:59-60`) — no `length`, `concat`, `split`, `chars`.
- **Bool**: `not` only; no `and`/`or` as values (the operators are syntax forms).
- **I64**: operators only — no `min`/`max`, `abs`, `to_string`, and no
  float/`U64`/`U8` types at all (check `Primitives.cs` for the real floor).
- **Traits**: `Eq` only. No `Show`, `Ord`, `Semigroup`, `Monoid`.
- **No docs convention** on any `pub` binding, and no test tree of its own.

## Open branches (to be grilled, one at a time)

1. **List/Option API naming and shape.** There is no precedent to copy, so this
   first cut sets one: positional versus half-open indices, `fold`'s argument
   order and accumulator position, `map`'s function-first convention, whether
   functions are curried or take all arguments, and whether the names come from
   OCaml/ML (`rev`, `fold_left`) or from the surface the language already uses.
2. **`head`/`nth`/`tail` on the empty list**: `Option`, `panic`, or an effect.
   This is a genuine design fork, not a style question, and it decides the shape
   of everything downstream.
3. **Traits the library defines versus the compiler keeps.** `Eq` currently lives
   in `lib`. Should `Show`/`Ord`/`Semigroup` follow, and does a library trait need
   a library-level `impl` story (including how impls are named and opened)?
4. **Documentation**: does the surface get a doc convention, and is there a
   surface-syntax slot for it? (Check whether a doc-comment form exists before
   designing one.)
5. **Importability**: may a program write `import "std/list"` directly, or only
   reach the library through `std`? The restructure leaves `Prelude.Of` able to
   answer this either way, so it is a policy decision.
6. **What is deliberately out**, so the ticket ends: UFCS, FFI and protocol/
   deriving machinery are named as "not now" in the map's fog; confirm the rest.

## Answer — decided 2026-09-27 (grilled to an empty frontier)

Nineteen decisions: sixteen from the first two rounds, and §17–§19 closing the loose
ends the first round left inside option text and the one inconsistency it created. **This section
supersedes the "what the library does not have today" list above**, which was
measured on 2026-09-26 and is wrong in two places: `test/conformance/cases/std/`
now exists (7 pairs), and the String/I64 halves of that list are unwritable for a
reason the list did not know (§4).

### 1. Scope — batteries-included, bounded

`std`'s library layer is the standard library a program just uses, bounded by the
compiler staying small: no new compiler machinery, and UFCS / FFI /
deriving-protocol machinery stay in
[design-trait-library-deriving-and-protocols](design-trait-library-deriving-and-protocols.md)
and the Stage 11 spec.

### 2. Namespace — per-type modules plus bare globals

After `import "std"`, a type's API is reached by qualification (`Std.Lists.map`),
and a bare `map`/`length` exists **only after the user's own `open`**. The two are
mutually exclusive because a bare name would be resolved by open *order*: measured,
two modules both exporting `map` and both opened give the last one, silently —
`{ A = module { pub map = fn(x) { x + 1 } }; B = module { pub map = fn(x) { x + 2 } };
open A; open B; map(1) }` is `VALUE 3`. `open Std.Lists;` on its own is legal
(measured), so the qualified style costs the user nothing it cannot undo.

### 3. Reach — member selection is the surface; strings stay loader-level

Any unit resolves, `std/bootstrap` included (policy unchanged, the loader keeps
`import "std"` as a *spelling*, not a privilege). `import` is not generalized to
path expressions: that would give the unit tree and the value tree one spelling
(`import std.List` vs `std.List.map`) resolving in two different systems.

### 4. The primitive floor is the boundary — String and `Show` wait

`src/Quill.Compiler/Primitives.cs:44-73` is the whole floor: I64 arithmetic and six
comparisons, `eq/neq` for Char/Unit/String, `panic`, `expand_block`,
`expand_decls`, `Tuple`/`tuple_arity`. There is **no** `print` and `src/Quill.Cli` is a
stub, so a program communicates only by its return value. Therefore `I64.to_string`,
`String.length/concat/split`, `Char`↔`String`, `Show`, and `Ord` beyond I64 are all
out, and the out-list is honest rather than lazy: nothing could observe them yet.

### 5. Names — spelled-out ML-familiar verbs

`length`, `reverse` (renamed from `rev`), `append` (two lists), `concat` (list of
lists), `map`, `filter`, `fold` (left fold), `find`, `head`, `tail`, `nth`,
`get_or`, `is_some`. The four universal short verbs stay short; the renames follow
the house style (`i64_to_bool`, `pat_wild`, `RawPatWild`) rather than ML's (`rev`,
`hd`, `tl`).

### 6. Argument order — policy first, subject last, uniformly

`map(f, xs)`, `fold(f, z, xs)`, `nth(i, xs)`, `take(n, xs)`, `find(p, xs)`. Keeps
the six shipped bindings. Currying is free (`f(a, b)` is `Ap(Ap(f,a),b)`), so this is
the order that makes partial application useful: `take(2)`, `nth(0)`.

**Its one hole, closed by §7**: a default value goes *first*, not last.

### 7. Defaults — default first

`Options.get_or(d, o)`, `Lists.head_or(d, xs)`, `Lists.nth_or(d, i, xs)`. Haskell's
`fromMaybe d o` is the precedent, and it is the form that curries into the useful
thing: `get_or(0)` is `Option(I64) -> I64`.

### 8. Empty list — `Option`, with saturating `take`/`drop`

`head : List(A) -> Option(A)`, `tail : List(A) -> Option(List(A))`,
`nth : (I64, List(A)) -> Option(A)`, `find : (A -> Bool, List(A)) -> Option(A)`.
`take`/`drop` take counts and saturate. `Option` is already the ABI bootstrap's
(`mk_option`, `Some`/`None`), so it costs the interface nothing, and an aborting
`nth` next to an Option-returning `head` is the inconsistency this avoids.

### 9. Indices — 0-based; counts for `take`/`drop`; `range(n)` only

0-based matches the language's own `.0` projection and `Tuple(n, …)`. `take`/`drop`
take counts, so they need no clamping decision beyond saturating. `range(n)` is
`[0, n)`, `Nil` for `n <= 0`. `slice`/`range(lo, hi)` are out until something needs
them; **half-open is the recorded principle** if they land.

### 10. Module names — plural, because the singulars are frozen

`Lists`, `Options`, `Strings`. `List`/`Option`/`Bool`/`String` are the ABI's type
names and cannot be reused: measured, `List.map` fails with `ELAB no constructor
map` —
`List` resolves to the type former. `std/stage2.qll` already pilots this privately
(`Types`, `Lists`). This decides the *names*; which of them are declared is §16 and
§19 — the first cut ships `Lists` and `Options` only.

### 11. Bundle name — `Std`

`Prelude.Binding` becomes `Std`, so the unit, the path and the handle agree:
`Std.Lists.map(f, xs)`. Capitalized, matching `Syntax` — modules capitalized,
functions snake_case, types singular.

### 12. Unit layout — one unit per module; `std` re-exports only the impls

`std/list.qll` **is** the `Lists` module (functions plus
`pub impl list_eq : Eq(List(A))` at its top); a new `std/option.qll` is `Options`
(with `option_map`/`option_bind` moved out of list.qll and renamed `map`/`bind`);
`std/stage2.qll` publishes them (`pub Lists = import "std/list"`) and re-exports
only the impls — `export Lists.{list_eq}` — so nothing flattens and
`Std.Lists.map` is the only spelling of `map`.

This rests on four measurements, and the distinction between the first two is the
whole reason the design works:

| probe | result |
| --- | --- |
| `N = module { export M; … }; N.x` | `VALUE 1` — `export` **flattens** into N |
| `N = module { open M; … }; N.x` | `no public member `x`` — `open` only **scopes** |
| stage2 with `open Lists` deleted, `export Lists` kept | bare `rev`/`map` still work — **`open` does not propagate to importers, `export` does** |
| `export M.{Color, color_eq}` then `Color.R == Color.G`; and `other` | `VALUE True`; `unbound variable: other` — selective export **carries an impl** and drops everything else |

### 13. Impls — with their type, rule unchanged

`Eq` stays the library's only trait (it is not in `PreludeAbi.cs` — the compiler
does not name it), impls are named and public, and the five primitive-backed ones
stay at `stdlib`'s top level because `I64`/`Char`/`Unit`/`String`/`Bool` have no
module to live in. **Impl visibility is not flipped to option B.**
[impl-visibility](../topics/impl-visibility.md) already weighed A against B and
recommended A plus named impls, and B costs a kernel change: a nominal carries an
id, a name, parameters and constructors but **no back-pointer to its defining
module**. B also does not fix coherence (that follows from scoped resolution,
settled in [traits](../topics/traits.md)) and spends legibility the way Scala's
implicit scope does.

The gap B would have closed — an impl reaching a use site only through a wholesale
`open`, with no selective open and silent shadowing — is real and is now
[selective open](selective-open.md)'s ticket.

### 14. Library-defined syntax — none added here

`std/lib.qll` already declares `pub syntax if`, `pub infix (&&) conjunction` and
five order groups, and `std/type.qll` declares `pub syntax type`. That right is
untouched; the candidates that change how a user *writes* a program (a pipeline,
list literals, an `Option` postfix) are
[the Stage 11 spec](specify-stage-11-macro-powered-language-features.md)'s.

### 15. Documentation — pin the shape now

A fixed comment above every `pub` binding: one line with what it returns and what
it does on the empty / out-of-range case (the §8 decisions a signature cannot
show), plus one line per module naming what belongs in it. Ordinary `#` comments —
the reader has no doc construct, and **no `pub` binding in `std/` carries prose
today**. A real doc syntax is deferred with a trigger: **adopt C#'s form once the
language self-hosts on .NET.**

### 16. Contents — everything the floor allows, in one pass

| module | bindings |
| --- | --- |
| `Lists` | length, reverse, append, concat, map, filter, fold, find, head, tail, nth, head_or, nth_or, take, drop, zip, zip_with, any, all, range, `pub impl list_eq` |
| `Options` | map, bind, get_or, or_else, filter, is_some, `pub impl option_eq` |
| `Std` top level | and, or, min, max, abs (beside the existing not, `if`, operators, `Eq`, five impls) |

Generic impls are what make the two `Eq` impls expressible at all; they landed
2026-09-27 ([trait-op-takes-innermost-impl](trait-op-takes-innermost-impl.md),
`790 cases, 0 failed`), and `Tuple(2, I64, Bool)` is a legal signature type
(measured), so `zip` returning pairs is too.

**Deliberately out, so the ticket ends**: String beyond equality, `Show`, `Ord`,
`fold_right`, `slice` and `range(lo, hi)`, `Options.map2` (see §18), a `Strings`
module (see §19), every new
surface syntax, and every change to impl reach.

### 17. `zip` truncates to the shorter list

`zip(xs, ys) : List(Tuple(2, A, B))` stops at the shorter input; the count is
`min(length(xs), length(ys))` and there is no failure path. This was decided inside
§16's option text rather than asked, and is now on the record: total, and the
convention of the languages this family borrows from. The cost accepted is that a
length mismatch is silent — `zip([1, 2, 3], [a, b])` is `[(1, a), (2, b)]` with no
complaint. `zip_with(f, xs, ys)` follows the same rule.

### 18. The extras that ship, and the one that does not

Ship `Lists.zip_with(f, xs, ys)`, `Options.or_else(d, o)` (default-first, per §7)
and `Options.filter(p, o)`. Held back: **`Options.map2`** — in a language with no
applicative or `?`-style syntax yet it is a workaround for missing surface sugar,
which §14 sent to the Stage 11 spec; shipping it commits a name that Stage 11 may
make redundant and starts the `map2`/`map3`/`map4` road. Also not added: any
`expect`/`unwrap`-style panicking helper, which would reopen §8 — argue it as such
if it is wanted, rather than letting it in as a convenience.

### 19. `Strings` is named, not declared

`Std.Strings` does not exist in the first cut. §10 decided the plural *names*, so
that the frozen singulars are never re-used; it did not promise that every module is
declared on day one. Nothing a `Strings` module could hold is writable inside §4's
floor — `length`, `concat`, `split`, `chars` each need a primitive that does not
exist — while `==`/`!=` on strings already work through the bootstrap impl. An empty
public module is a name with nothing behind it and a standing invitation to add a
function just to justify it, so `Std.Strings` appears with its first real binding,
which is when the primitives land.

### Consequences and deliverables

- **Breaking, and intended**: today's bare `rev`/`map`/`fold`/`append`/
  `option_map`/`option_bind` stop existing, so the existing
  `test/conformance/cases/std/` pairs are rewritten to the qualified spelling, and
  `std/README.md` gains the surface and the module table.
- **New ticket opened by this one**: [selective open](selective-open.md) —
  `open M.{a, b}`, the mirror of the working `export M.{a, b}`.
- **Explicitly not to be reopened as-is**: `M.(e)`
  ([local-open-expression](local-open-expression.md), ruled B on 2026-09-18) is a
  second spelling of `{ open M; e }` and buys nothing selective-open does not. If
  the double-brace idiom becomes an irritant, the evidence that ticket asked for is
  call sites, which this ticket's implementation will produce.
- **Three things an implementing fork must verify rather than assume**: that
  `export Lists.{list_eq}` puts the impl in a *program's* base scope (measured only
  with local modules so far); that a program can write `open Std.Lists;`; and that
  nothing outside `std/` referenced the old bare names.

### As landed (2026-09-27, merged `d70567d`)

Implemented as `e64b900` (the surface, with the 7 existing `cases/std/` pairs rewritten
and 51 added) and `d70567d` (README + STATUS). **31 bindings** ship exactly as §16
lists them; `conformance: 857 cases, 0 failed`, xUnit 187/187. Three deviations and
results to record:

1. **The two impls did not ship.** §12's and §16's `list_eq`/`option_eq` are blocked by
   two compiler gaps, each found by the fork and reproduced by the integrator:
   [a generic impl's head variable carries no bound](generic-impl-head-var-has-no-bound.md)
   (a generic `Eq(List(A))` head *does* register — a body-trivial one serves `List(I64)`
   — but `Eq.eq(h, h)` on its own variable has no dictionary, and the failing shape
   takes the whole **prelude load** down, so it cannot be stubbed) and
   [a recursive helper matching two lists cores](recursive-match-on-two-lists-cores.md)
   (the recursive two-list body a real equality needs dumps core; the same match without
   the recursive call is `VALUE 2`, and `Lists.length` recurses over one list fine).
   The work is [Eq for List and Option](std-eq-for-list-and-option.md), blocked on both.
   So the library ships without `Std.Lists`/`Std.Options` equality, and stage2
   re-exports no impl — correct, since there is none to re-export.
2. **§12's mechanism is verified but unexercised by real code.** With a body-trivial
   probe impl, `export Lists.{probe}` does put `Eq(List(I64))` in a *program's* base
   scope and leaves every other member out (no-import program, `xs == xs` →
   `VALUE True`). The first real use is the blocked ticket.
3. **The prelude order changed, which the design did not anticipate.** `Prelude.Order`
   is now bootstrap, `std/lib`, `std/list`, `std/option`, `std/type`, `std` — `std/lib`
   had to move *below* the two new units because a list binding uses the operators (and
   would need `Eq`). §4's floor is untouched; this is the library depending on the
   language surface rather than the other way round, and it is the one structural
   addition to §12.

Of the three things the fork was told to verify: (3) held clean
(`\b(rev|option_map|option_bind)\b` outside `std/` → empty), (2) held
(`open Std.Lists; length(…)` → `VALUE 2`, case `open-lists-brings-bare-names`), and (1)
held only via the substitute probe above, since the real impl is the blocked one.
