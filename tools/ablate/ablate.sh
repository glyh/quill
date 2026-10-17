#!/usr/bin/env bash
# Ablation driver: neutralise one RefineContext channel at a time and watch which
# conformance cases flip. See ablate.py for why, and --list for the channels.
#
#   tools/ablate/ablate.sh              # every channel
#   tools/ablate/ablate.sh names opened # just these
#
# The source file is restored after each run. Refuses to start on a dirty tree,
# because the restore is `git checkout --` and that would take uncommitted work
# with it.
set -u

SRC=src/Quill.Compiler/Elaborator.Patterns.cs
RUNNER=test/Quill.Conformance
LOG="${ABLATE_LOG:-/tmp/ablate-logs}"

cd "$(git rev-parse --show-toplevel)" || exit 1

if [ -n "$(git status --porcelain -- "$SRC")" ]; then
  echo "refusing to run: $SRC has uncommitted changes (the restore would discard them)" >&2
  exit 1
fi

ALL_CHANNELS="names self-entry resume-entry opened self-methods constructor-entries evidence"
CHANNELS="${*:-$ALL_CHANNELS}"

mkdir -p "$LOG"
echo "logs: $LOG"

run_one() {
  local name="$1"
  if ! python3 tools/ablate/ablate.py "$name"; then
    echo "$name: PATCH FAILED — skipping"
    return
  fi

  if ! dotnet build -v q --nologo > "$LOG/$name.build.log" 2>&1; then
    echo "$name: BUILD FAILED (see $LOG/$name.build.log)"
    git checkout -- "$SRC"
    return
  fi

  # The runner walks up to the repo root to find test/conformance/cases, so it
  # needs no working directory of its own.
  dotnet run --project "$RUNNER" --no-build > "$LOG/$name.suite.log" 2>&1
  printf '%-22s %s\n' "$name:" "$(grep -E '^conformance:' "$LOG/$name.suite.log" | tail -1)"
  grep -E 'expect|got|FAIL|failed' "$LOG/$name.suite.log" | head -20
  git checkout -- "$SRC"
}

for ch in $CHANNELS; do
  run_one "$ch"
done

# Belt and braces: a crash between the patch and the restore must not leave the
# tree ablated.
git checkout -- "$SRC" 2>/dev/null
echo "ALL DONE — $SRC restored"
