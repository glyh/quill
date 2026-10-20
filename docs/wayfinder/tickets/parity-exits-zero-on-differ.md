---
title: --parity exits 0 when it found differs
parent: ../quill-design-map.md
labels:
  - wayfinder:task
status: open
assignee:
blocked_by: []
# Found 2026-10-19 by the integrator, in the same session the harness's coverage fix
# (59894d6) paid for itself by catching a 17-differ regression. It reports that
# regression on stdout and still exits 0, so nothing but a human reading the text
# notices.
---

# `--parity` exits 0 when it found differs

`test/Quill.Conformance/Program.cs`'s `RunParity` ends unconditionally:

```csharp
Console.WriteLine($"parity: {match + differ + notPorted} cases, {match} match, {differ} differ, …");
return 0;
```

So the mode whose entire purpose is to catch a backend that disagrees with the interpreter
**reports success to everything that is not a human reading stdout.** Measured 2026-10-19:
a fork's branch had **17 differs** (emitted code calling `internal MatchOps`, and reading
`Value.VCon` where emitted code produces `Runtime.Con`) and every gate a script could run —
`dotnet build`, `dotnet test`, this runner's own default mode — was green.

## Why it matters more than a cosmetic exit code

Slice 1b's two halves were built in parallel, and **`--parity` is the only instrument that
could see their seam** — neither `build` nor xUnit nor the default runner mode failed on
either bug, because both live inside emitted code that only runs when a `Destruct` is
reached. A CI step or a fork's self-check that runs `--parity` and trusts its exit status
would have merged the regression silently. `differ` is defined to be the count of places the
backend disagrees with the reference; a non-zero one is a failure by the harness's own
stated semantics, not a statistic.

## What to do

1. **`return differ == 0 ? 0 : 1;`** — and decide the `notPorted` question explicitly rather
   than by accident: not-ported is *expected* while slices are unfinished, so it must **not**
   fail the run, or every fork mid-slice would be red. Only `differ` is a failure.
2. Check the sibling modes for the same shape. `RunFile` and the default mode both `return 0`
   on their success paths; confirm that is deliberate for each rather than a copy of this bug
   (they report a single case's outcome, so 0 is plausibly right — but say so in a comment,
   because the two readers who hit this ticket will otherwise have to re-derive it).
3. **State in the mode's header comment that a non-zero exit means a differ**, so the
   contract is legible where the code is, not only in this ticket.

## Gate

```
dotnet build -v q --nologo
dotnet test/Quill.Conformance/bin/Debug/net10.0/Quill.Conformance.dll --parity; echo "exit=$?"
#   today: exit=0 with differ>0 (reproduce by re-introducing either seam bug, or by
#          any change that makes the backend disagree)
#   want:  exit=0 when differ==0 (green at b416d65), exit=1 the moment differ>0
```

Suite must stay **967 cases, 0 failed** and xUnit **217**; this changes a runner's exit
status, not any language behaviour.

## Related

The per-mode accounting this runner prints (`real coverage: N value-match (+… both-errored,
+… never elaborated)`) was added the same day (`59894d6`), because the bare "352 match"
overstated coverage ~3× — a case whose elaboration fails was counted as a match with neither
path running. Both this ticket and that fix come from the same lesson: **the harness's
summary numbers are read as verdicts, so each one has to mean what it says.**
