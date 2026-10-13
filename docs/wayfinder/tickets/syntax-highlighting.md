---
title: Syntax highlighting for quill
parent: ../quill-design-map.md
labels:
  - wayfinder:fog
status: closed
closed_date: 2026-10-04
resolution: Superseded 2026-10-04 by `topics/syntax-highlighting-server.md` (`afea2f2`), which answers this ticket's two "what would sharpen it" questions — consumer = neovim via a compiler server over msgpack-RPC, and "good enough static grammar" explicitly rejected in favour of elaborator-driven spans. A best-effort tree-sitter grammar and nvim queries exist meanwhile (`editor/tree-sitter-quill/`, `520ea20`) and are allowed to be wrong about operators and macros. What is still fog is the first-class compiler API (`topics/first-class-elaborator-api.md`), designed and unbuilt — that is the live item, not this ticket.
assignee:
blocked_by: []
---

# Syntax highlighting for quill

## Desire

The user wants to see proper syntax highlighting for `quill` source soon. This is a
visibility/ergonomics goal, not a language-semantics one — but the surface makes it
unusually hard.

## Why treesitter is a doubt

Tree-sitter is the default choice for editor highlighting, and it is a **context-free
parser**. `quill`'s surface is not context-free in the ways that matter for
highlighting:

1. **Enforestation.** Parsing is interleaved with macro expansion. The token stream
   is not the syntax tree — a macro application produces syntax that was never
   written. A CF parser over the raw text sees only the macro call, not its output.
2. **Operator demotion.** Operators are not keywords; they are `pub infix` /
   `pub prefix` declarations in `std`. Whether `+` is an operator or a variable
   depends on whether `std` is opened at that point. A static grammar cannot know.
3. **Scope-aware binding.** A name's role (operator, variable, type, constructor)
   is resolved through the binding table, not by position or spelling. The same
   identifier can be an operator in one unit and a variable in another.
4. **Hygiene.** Macro-generated syntax carries scope sets that affect resolution but
   are invisible in the source text.

The result: a hand-written or generated CF grammar will **always** mis-colour some
programs, and the mis-colourings will be exactly the interesting ones (operators,
macro uses, generated code).

## What the existing analysis says

`docs/ideas/tooling-and-diagnostics.md` §"The highlighting grammar is a second
artefact, and for an enforested surface it must be a query" covers this in detail.
Its conclusion: a correct highlighter has to *elaborate bindings per unit*, which
makes it a client of the first-class compiler API — not a standalone grammar.

## Why this is fog, not a ticket

- The first-class compiler API is itself a fog item on the map. A correct
  highlighter depends on it.
- The surface syntax may still change (the "surface syntax after the macro model
  settles" fog item). A grammar written today may need rewriting.
- No editor integration *consumed* a highlighter when this was written; that is no longer true —
  `editor/tree-sitter-quill/` and `editor/nvim/` exist (`520ea20`), and
  [`topics/syntax-highlighting-server.md`](../topics/syntax-highlighting-server.md) (`afea2f2`)
  rules the consumer (neovim) and the shape (a long-running compiler server, not a grammar).
- The cheapest useful thing (a TextMate grammar for the common case, accepting
  that operators and macro uses will be wrong) is a few hours of work and drifts
  immediately. Whether that trade-off is worth it is a user decision, not a
  technical one.

## What would sharpen it — answered 2026-10-04

- **"Good enough now" vs wait for the API** — *answered: ship both, but only the grammar is
  allowed to be wrong.* The static grammar landed as a best-effort tree-sitter grammar
  (`editor/tree-sitter-quill/`, `520ea20`) for keywords, delimiters and comments, explicitly
  accepting that operators and macro uses mis-colour; the precise path is the server.
- **The consumer** — *answered: neovim, through the server.*
  [`topics/syntax-highlighting-server.md`](../topics/syntax-highlighting-server.md) `afea2f2`:
  msgpack-RPC over stdio, tree-sitter's *pattern* rather than its API, a ~50-line Lua client with no
  language logic, and the first slice of the first-class compiler API as the server's tree
  interface.
- **The first-class API moving to a ticket** — *moved, as a topic*:
  [`topics/first-class-elaborator-api.md`](../topics/first-class-elaborator-api.md) (`docs/ideas/`
  before that). This ticket is therefore closed rather than open: it has no question left of its own,
  and what gates the implementation is the API, which the map tracks as fog.
