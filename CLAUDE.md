# CLAUDE.md

Guidance for working in this repository.

## Project overview

`quill` is a programming language compiler/interpreter. The implementation is **C# (.NET 10)**: the
compiler tree is `src/`, the prelude source is `std/`, and the tests are `test/`. The earlier
OCaml prototype was **removed on 2026-09-25** — see *History* at the end,
which keeps the knowledge that was learned while it was the reference.

The core (`core_tt`) is dependently typed with bidirectional elaboration, normalization by
evaluation, nominal ADTs, structural records/modules, traits, algebraic effects, mutable
references, and a hygienic enforestation-based macro system.

Design philosophy: **Consistency > Flexibility > Correctness** — one construct for many roles
(`struct` = record/module/namespace), types are values, type-case on open `Type` is acceptable.

### Build & test

```sh
dotnet build                                         # everything
dotnet test test/Quill.Tests                           # xUnit (internals)
dotnet run --project test/Quill.Conformance            # the shared language suite
```

There is **no REPL**: `src/Quill.Cli` has one command, `quill run <file>`, which compiles through
the IL backend (`Backend.Run`). To run a program through the interpreter, use the suite's
single-file mode (below); `--parity` is the only mode that compares the two paths.

A single conformance program, the way the suite judges it:

```sh
dotnet test/Quill.Conformance/bin/Debug/net10.0/Quill.Conformance.dll --file /tmp/probe.qll
```

**The three-project split is load-bearing**: `Quill.Kernel` (atoms, terms, values, patterns,
decision trees), `Quill.Expand` (reader, enforestation, macros — it **cannot reference the
elaborator**), `Quill.Compiler` (elaboration, unification, the evaluator). `Quill.Cli` is the
executable. Do not add a reference that crosses the first boundary.

### Documentation hierarchy

- `docs/STATUS.md` — **authoritative** snapshot of what is built; when any doc disagrees on
  completion status, STATUS wins.
- `docs/wayfinder/` — the direction map (decided / open tickets / fog); start at
  `docs/wayfinder/quill-design-map.md`. Design detail for decided directions lives in `topics/`,
  the macro-system reference in `macro-system/`.

### Pipeline

```text
source → reader → enforestation → expanded Syntax → elaboration → Core term → NbE → value
```

### Source layout

- `src/Quill.Kernel/` — `Atom`, `Core` (`Core.Shift`, `Core.Match`, `Core.Patterns`,
  `Core.Refs`), `Syntax`, the decision trees, `EquatableArray`
- `src/Quill.Expand/` — the reader, `Enforest` (+ `Enforest.Roles`, `Enforest.Match`),
  `Expander` (+ `.Macros`, `.Roles`, `.Imports`), `MacroRuntime`
- `src/Quill.Compiler/` — `Elaborator` split across partial files (`Elaborator.Traits`,
  `.Patterns`, `.RecTypes`, `.Generative`, `.Implicits`, `.Effects`, `.Match`, …), `Unify`,
  `Nbe` (+ `Nbe.Match`, `.StuckMatch`, `.Effects`, `.Generative`), `Reflection`, `Budget`,
  `Driver`, `Loader`
- `std/` — the prelude source, split into a bootstrap layer (`bootstrap.qll`) and the library
  (`lib.qll`, `list.qll`, `option.qll`, `type.qll`) with `stage2.qll` as the unit a program
  imports, embedded into `Quill.Compiler` as resources by its `.csproj`
- `test/conformance/cases/` — the language suite: `.qll` + `.expect` pairs, nothing to register
  (`cases/README.md`)
- `test/Quill.Tests/` — xUnit, internals only
- `test/Quill.Conformance/` — the suite's runner; `test/Quill.Cli` lives at `src/Quill.Cli`
- `Quill.slnx`, `Directory.Build.props` — the solution and the settings every project shares

### Where a test goes

A test that is only "source → value or error" belongs in `test/conformance/cases/` — **and only
there**, so language behaviour has one source of truth. Keep a test in xUnit when it inspects
internals: syntax shapes, reflection round trips, budget accounting, an exact error constructor,
or a type rather than a value.

## Rules that bite

- **Exceptions are not control flow.** `FunException` is a genuine *language* error;
  `NotImplementedException("not ported yet: …")` marks an **unported path** and is deliberately
  distinguishable, so a refusal can never satisfy a case expecting `error`. Do not use either for
  ordinary dispatch — use `Result`, `option`, or an explicit sum type.
- **The evaluator never recurses on the native stack per object-level call.** A term needing a
  sub-evaluation gets a `Kont` frame. Readback, unification and the elaborator may recurse over
  structure.
- **Slots.** A binding contributes entries only through `BindingTerm.Slots()`, which both the
  elaborator and the evaluator consume.
- **A feature's code goes in its own partial file** (`Elaborator.<Feature>.cs`,
  `Nbe.<Feature>.cs`, `Core.<Feature>.cs`, `Enforest.<Feature>.cs`, `Expander.<Feature>.cs`);
  a shared dispatch switch gets one case line that calls into it.
- **Name things after the domain model and `CONTEXT.md`** (`Context`, `Environment`, `Width`,
  `Entry`, `Locate`, `Binding`, `Value`, `Meta`), not after the deleted prototype's abbreviations.
- **No test-driven special cases.** Do not add logic whose only purpose is to make a test pass.
  Fix the code so it genuinely handles the input. When the intended semantics are ambiguous or
  under-specified, **ask** before committing to an interpretation.
- **Debug via instrumentation, not test-case exploration.** When tracking down a parser or
  elaboration bug, add logging or a reusable utility that exposes the intermediate
  representation, and capture the output:

  ```sh
  dotnet build 2>&1 && dotnet test test/Quill.Tests --nologo 2>/tmp/log
  ```

  The goal is one diagnostic that pins the root cause, not a matrix of modified inputs.
- **Git checkout is a last resort.** If you must jump to a historical commit or branch, first
  record where you are so you do not lose the starting point.

## Writing a fork's report, and reading one

Four times in one session a ticket's own prose was older than the commit that closed it, and
twice a "known" error message had never been run. So:

- **Measure a gap in the runner before briefing a fork**: one command settles what a paragraph
  cannot, and a probe written down is worth less than one executed.
- **Never soften an unprobed path into a verdict.** "Unsettled, here is the probe I would run
  next" is a useful report; "probably fine" is not.
- **Say what you did not do.** A fork that could not run half its verification (no `_build` in a
  worktree, no provider quota) and says so is more valuable than one that reports green.

---

## History: the OCaml prototype (removed 2026-09-25)

It was deleted once the port measured as a superset: `port-fails: 0` over every program in the
repo, with the 34 cases where the two disagreed being ones the prototype got wrong. Its code is
in `git log` (paths `lib/`, `bin/`, `dune`, `dune-project`, `fun.opam`, `test/{backend,semantic,
syntax}/`, `test/conformance/run_conformance.ml`, `scripts/differential.sh`), and
`test/conformance/prototype-divergences.txt` is now a historical record of the 34 places a second
implementation got the language wrong.

The notes below describe that prototype's structure. They are kept because the *lessons*
transfer: each one is a trap the port can still fall into, in its own idiom.

### Adding a new reflected Syntax ADT (e.g. `Pattern`, `TypeExpr`)

When adding a nominal type for macros to inspect and construct:

1. **Prelude**: declare it in `std/stage1.qll`'s `Syntax` module (stage 1 has no `type`
   macro — that is defined in stage 2) as `pub rec Foo = enum { … }; export Foo;`, and add
   builders (`pub foo_build = fn(args…) { … }`).
2. **Nominals**: add the field to the syntax-nominals registry the macro evaluator builds.
3. **Wrap/unwrap**: add the wrap/unwrap pair the reflection layer uses.
4. **Every construction site**: update all sites building that registry (the test helpers
   included) — the round trip must be the identity.
5. **Pattern synonyms**: a new ADT's `pub pattern` must be resolvable by constructor name as
   well, not only through `open`.

### Reflection and scope-addition: preserve ALL fields

When adding a field to a `Syntax` binding variant, update **every** constructor of that variant
across the codebase — reflection both ways, the one traversal that every scope, intro, rename and
syntax-form fill goes through, and a syntax form's rule templates. **Pattern**: search for the
variant name and check every match site preserves the new field or explicitly drops it with a
reason.

### Parser: `rest = []` is almost always wrong

When a function consumes tokens and returns the remaining tokens, never hardcode the remainder as
empty. Return the unconsumed tokens so the caller can check "nothing left" or keep parsing. The
classic bug was a `fn`-parts arrow case returning `(body, [], span)` and silently dropping the
rest — the same shape exists in the port's enforester.

### Constructor resolution phases

A pattern head resolves *like a bare name* — a binder or an open choice — and the nominal is read
off the entry it lands on, never by scanning or by spelling. Consequences: a new ADT declared
inside a module becomes visible only **after** its binding is processed, so pattern synonyms for
it must come later in the binding group; and a *function* reducing to a nominal is a valid head
(an alias), because types are values.

### Expander vs elaborator context

The expander processes macros during parsing (it needs `elaborate` and `eval_and_apply`
callbacks); the elaborator turns expanded `Syntax` into the core term. Macros are compiled by the
**expander** using the `elaborate` callback, and imported modules' macros are pre-compiled and
cached, then pre-registered in the expander. In the port these are `Expander`/`MacroRuntime` and
`Elaborator`; the same split, and the same reason `Quill.Expand` cannot reference `Quill.Compiler`.
