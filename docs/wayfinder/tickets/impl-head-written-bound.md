---
title: A written bound on an impl head
parent: ../quill-design-map.md
labels:
  - wayfinder:grilling
status: closed
closed_date: 2026-10-04
resolution: Closed 2026-10-04. The semantics question is answered and implemented (`418f28b`): a written bound on an impl head is authoritative, the three edges the ruling did not name are handled in `Elaborator.Traits.Contribute`, and the binder error is live (`Elaborator.Traits.cs:274`). The surface *spelling* was never this ticket's — `design-std-library-surface.md` §14 sent new syntax to the Stage 11 spec, which is where a written-bound form is spelled if it is ever wanted.
assignee:
blocked_by: []
---

# A written bound on an impl head

Split off 2026-09-27 when
[a generic impl's head variable carries no bound](generic-impl-head-has-no-bound.md)
was implemented. That ticket settled *that* an impl's head variable takes evidence;
this one asks whether the source can **say so**.

## Where it stands

The shipped contract is **inference**: the evidence an impl demands for its own head
variable is the evidence its method bodies use. The impl is elaborated as a function of
those dictionaries and its type records them, so a named impl's type reads

```
{A : Type} -> {Eq(A)} -> Eq(List(A))
```

even though no source line writes `Eq(A)`. Ruling on 2026-09-27: keep the inference,
and decide the written form in its own ticket.

## What the inference costs, measured

Two impls with the same head and the same declared type, differing only in the method
body, and a program comparing lists of a type with no `Eq` of its own
(`rec Foo = enum { F }; open Foo;` then `Cons[Foo](F, Nil[Foo]) == Cons[Foo](F, Nil[Foo])`):

```quill
# body never mentions the element
pub impl probe : Eq(List(A)) = module { fn eq(xs, ys) { True } };
#   -> VALUE True        Foo's lack of Eq is irrelevant; the impl needs no dictionary

# body compares the elements
pub impl probe : Eq(List(A)) = module { fn eq(xs, ys) {
  match (xs) { Nil => True, Cons(h, t) => match (ys) { Nil => False, Cons(h2, t2) => Eq.eq(h, h2) } }
} };
#   -> ELAB missing implementation of `Eq`
```

So whether a type gets equality is decided by a line inside a method: an impl that
forgets to use its element is indistinguishable, in the declaration, from one that
cannot work without it — and it silently claims equality for element types that have
none.

## The proposed form, which does not exist yet

```quill
pub impl probe[A : Eq] : Eq(List(A)) = module { fn eq(xs, ys) { … } };
```

The requirement moves into the declaration, matching the precedents this language
otherwise follows (Rust's `impl<T: Eq>`, Haskell's instance context), and the body
stops deciding who can use the impl.

## Open questions

1. **Where does the binder go?** `impl name[A : Eq] : …` before the colon, or after the
   head (`impl name : Eq(List(A)) with A : Eq`)? The head is a type, so the binder could
   also be read as belonging to it (`impl name : [A : Eq] -> Eq(List(A))`, which is what
   the impl's *type* already is).
2. **Must the written set and the body's demands agree?** Three answers: the written set
   must be exactly what the body uses; it must be a superset (an unused dictionary is
   simply unused); or a written bound is the whole truth and the body may only use what
   is written. Each needs a rule for the mismatch and an error at the definition.
3. **Does writing a bound change applicability?** Under the superset reading, `impl
   probe[A : Eq] : Eq(List(A))` refuses `List(Foo)` even when its body would not have
   needed `Eq(Foo)` — which is the point, and also a behaviour change for any impl that
   already exists.
4. **What happens to `Vars`?** The implementation already threads an impl's own type
   variables and now its bounds (`TraitEvidence.Bounds`, `ModuleEntry.Impl.Bounds`); a
   written bound has to be reconciled with the inferred set rather than becoming a second
   source of truth.

## Decided 2026-10-01: the written bound is authoritative

Ruled: **the declaration decides, not the body.** Written in the form the ruling was made against —

```quill
pub impl list_eq[a : Eq] : Eq(List(a)) = module { … };
```

whose type is therefore always `{a : Type} -> {Eq(a)} -> Eq(List(a))`, whether or not the body ever
uses the dictionary. That answers the four open questions:

1. **Where the binder goes** — in the head, bracketed after the impl's name, before the colon, as
   above. The *surface spelling* still belongs to [the Stage 11 spec](specify-stage-11-macro-powered-language-features.md)
   per "Not this ticket" below; what is decided here is that there is a written place for it and that
   it is the impl's binding form rather than a `with` clause after the head.
2. **Written set vs the body's demands** — the body may use **at most** what is written. A body that
   demands more than its declaration wrote is an error **at the definition**, naming the head: the
   declaration is not a hint to be widened by a method body.
3. **Applicability** — writing a bound *does* narrow the impl, deliberately: `Eq(List(a))` demands
   `Eq(a)` even for a body that never compares, which is the point of being able to say it.
4. **`Vars`/bounds** — the written set is the single source of truth for the impl's bounds; inference
   stops being one. (It was the shipped contract of
   [a generic impl's head variable carries no bound](generic-impl-head-var-has-no-bound.md), so that
   ticket's inference becomes the *fallback for an impl that writes nothing*, not the authority.)

**Why:** measured on this ticket — the same head produced two different impl types depending on one
line inside a method body (`{a} -> Eq(List(a))` versus `{a} -> {Eq(a)} -> Eq(List(a))`), so a refactor
inside a body silently changed who could use the impl, and an impl that ignores its element silently
claimed equality for element types that have none.

**What it costs, counted:** `std` has **7** impls, of which **2** are generic and need the annotation
(`list_eq` in `list.qll`, `option_eq` in `option.qll`); about **38** files in the suite use a generic
impl head and need it only where they rely on the bound.

## Edges the implementation rules (2026-10-04, landed)

Three edges the ruling did not name, each handled in `Elaborator.Traits.Contribute`:

1. **A written binder that does not occur in the head** is an error at the definition:
   ``impl `probe`: the binder `x` does not occur in its head``.
2. **Duplicate written bounds** dedupe — `[a : Eq, a : Eq]` contributes one dictionary argument.
3. **Binders in a signature's impl** are refused at parse: a signature's impl has no fields and
   no binders (`ParseSignatureImpl` writes `[]`).

A binder with no bound (`[a]`) is accepted and contributes nothing.

## Not this ticket

Surface syntax generally. [The library surface](design-std-library-surface.md) §14 sent
new syntax to [the Stage 11 spec](specify-stage-11-macro-powered-language-features.md),
which is where the spelling belongs; this ticket is the *semantics* question that would
have to be answered first.

## Reading

- [A generic impl's head variable carries no bound](generic-impl-head-var-has-no-bound.md)
  — the fix, its root cause, and the measurement table
- `src/Quill.Compiler/Elaborator.Traits.cs` — `ImplBound`, the promotion of pending
  evidence into dictionary arguments, and the impl's `Pi` type
- `src/Quill.Compiler/Elaborator.Export.cs` — how an impl's `Vars`/`Bounds` travel through
  `export`, which a written bound would also have to survive
- [trait library deriving and protocols](design-trait-library-deriving-and-protocols.md)
  — `derive` is the case that would most want to state a bound it can prove
