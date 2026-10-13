---
title: The `fun` keyword is reserved, unconsumed, and no longer names anything
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: closed
closed_date: 2026-10-04
resolution: Closed 2026-10-04 — **freed**, this ticket's option 1. `Word.Fun` is gone (`TokenTree.cs:24`), `fun` is no longer registered in `Keywords` (`TokenTree.cs:46`), it is gone from the nvim table (`editor/nvim/syntax/quill.vim:18`), and the comment above the Word list now names it beside the other freed spellings. The reservation was real and observable first: `{ fun = 4; end = 1; with = 2; then = 3; else_ = end + with + then + fun; else_ }` failed with `ELAB unsupported Phase 7A keyword: fun at <unknown>:1:2-1:5`, thrown by the enforester's *default* arm (`Enforest.cs:302`) — there was never a rule for `fun`, only the fallback that catches every `Word` with none, which is exactly the ticket's finding. That program is now `values/freed-keywords.qll` (red before the change, `VALUE 10` after), so the ruling is pinned rather than assumed. Integrator's own run: **967 cases, 0 failed**, xUnit **217/217**. Open question 2 — the spec claims keywords are *not* reserved, a weaker claim than `Keywords` implements — is not this ticket's: it is filed as [the keyword reservation mismatch](keywords-are-reserved-but-the-spec-says-shadowable.md).
assignee:
blocked_by: []
---

# The `fun` keyword is reserved, unconsumed, and no longer names anything

## The complaint, measured

`TokenTree.cs` declares a `fun` keyword and then reserves it:

```
src/Quill.Kernel/TokenTree.cs:24    public static readonly Word Let = new("let"), Fun = new("fun"), Sig = new("sig"),
src/Quill.Kernel/TokenTree.cs:46        new[] { Let, Fun, Sig, Fn, Do, Match, Effect, ...
```

Line 46 feeds `Keywords`, an `ImmutableDictionary<string, Word>` keyed by spelling. **That
dictionary is what reserves a spelling** — it is the reader's reserved table, not a list of
words some rule happens to match. So `fun` is reserved.

And nothing consumes it. Across the whole tree:

- **`Word.Fun` has exactly two C# references** — the declaration at line 24 and the
  registration at line 46. No parser rule, no enforest rule, no diagnostic compares it.
  Nothing matches the spelling `"fun"` anywhere else in C#.
- **`fun` appears 0 times in all 1043 `.qll` sources** and 0 times in the `.expect` files.
  `fn` — the real function keyword, one character away — appears 857 times.
- **No conformance case uses `fun` as an identifier**, so nothing is currently *relying* on
  it being reserved in order to fail.

So it is a reserved spelling that buys nothing, and the only thing reservation does is stop
a program naming something `fun`.

## Why the rename surfaced it

Before, the keyword and the language shared a spelling, so `Word.Fun` in `Quill.Kernel` at
least read as "related to the language." Now the language is `Quill` and the member is still
called `Fun`, which reads as though it refers to the language name rather than to a keyword.
The nvim highlighter makes the confusion concrete for users — `editor/nvim/syntax/quill.vim:18`
lists both spellings in one table:

```vim
  \ let fun sig fn do match effect module struct enum impl trait
```

A reader sees `fun` beside `fn` and reasonably concludes `fun` is a function form.

The syntax spec never agreed. `SYNTAX_SPEC.md:40` lists its keywords as

```
fn  do  end  if  else  match  with  effect  perform  resume
type  module  struct  impl  trait  pub  import  open  macro
self  Self  ref  deref  can  <-  _  true  false  Unit
```

— **`fun` is absent from it.** The keyword exists in the reader's reserved table and in the
editor's highlighter, and in no specification and no rule.

## What freeing it would cost, measured

Nothing that anything currently checks. The precedent is `STATUS.md:346`, Stage 11
increment 2, which removed keywords for exactly this reason:

> `then`, `with`, `end`, `else` and `Unit` are no longer keyword tokens: **no parser rule
> matched them** … They are ordinary identifiers.

with `test/conformance/cases/values/freed-keywords.qll` as the case that pins it. That is
the same shape as this: a reserved spelling with no rule behind it.

## Wanted

**A ruling, not a discovery.** Two answers are defensible and the ticket should record which
one is taken:

1. **Free it.** Drop `Fun` from line 24 and from the line 46 array — 26 reserved spellings
   become 25 — and drop `fun` from the nvim table. `fun` becomes an ordinary identifier, as
   `then`/`with`/`end`/`else`/`Unit` did. Add a case beside `freed-keywords.qll` that binds
   `fun`, so the ruling is pinned rather than assumed.
2. **Give it a rule.** If `fun` was meant to be a second function form, or a marker with a
   future reading, say which and make a rule match it. Keeping the reservation without a
   rule is the one option the evidence does not support.

## Open questions

1. **Was it deliberately reserved to keep the *name* out of user programs?** The keyword and
   the language shared a spelling until the rename, which would make the reservation a
   branding decision rather than a syntax one. Nothing in `TokenTree.cs`, `SYNTAX_SPEC.md`
   or `STATUS.md` says so, and `Word.Fun` is referenced nowhere, which is what a deliberate
   reservation would normally show.
2. **Is `Const`-style shadowing intended?** `SYNTAX_SPEC.md:40` labels its keywords "(not
   reserved; can be shadowed)", which is a *weaker* claim than what `Keywords` implements.
   Whether `fun` belongs to the reserved set or the shadowable set is downstream of that
   broader mismatch — out of scope here, but worth naming.
3. **`Sig` is the counter-example that makes this measurable.** It sits beside `Fun` on
   line 24 and is the shape a *live* keyword has: `sig` appears **208 times** in `.qll`
   sources, and `TokenKind.Sig` is consumed at `Enforest.Traits.cs:44`,
   `Enforest.Effects.cs:41`, `Enforest.cs:284` and `Enforest.Structs.cs:76`. Two spellings
   declared on one line, one read by four rules and one by none — the `fun` ruling should
   not be generalised to `sig`.

## Sharpens when

Anyone writes a program that names something `fun`, or documents the keyword surface. Today
the only place `fun` is presented as a keyword is the editor highlighter.
