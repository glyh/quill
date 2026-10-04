#!/usr/bin/env bash
# Parse every quill source in the repo and fail on any ERROR or MISSING node.
#
#   ./check-corpus.sh [--verbose]
#
# Scope: std/*.qll, test/conformance/**/*.qll, plus this grammar's own
# hand-written lexical torture file (test/lexical.qll). The corpus contains no
# block comments, no datum comments and no `?`/`!` identifiers, so that file is
# the only thing covering them.

set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
cd "$here"

verbose=0
[[ "${1:-}" == "--verbose" ]] && verbose=1

command="tree-sitter parse --quiet"

mapfile -t files < <(
  find "$repo/std" "$repo/test/conformance" -name '*.qll' -type f | sort
  echo "$here/test/lexical.qll"
)

total=${#files[@]}
echo "checking with: $command <$total .qll files>"
errors=0
missing=0
bad_files=0

out="$(tree-sitter parse --quiet "${files[@]}" 2>/dev/null)"
errors=$(grep -c 'ERROR' <<<"$out")
missing=$(grep -c 'MISSING' <<<"$out")
if [[ -n "$out" ]]; then
  # one line per offending file; the tree is not printed with --quiet
  bad_files=$(grep -c 'ERROR\|MISSING' <<<"$out")
  echo "$out"
else
  bad_files=0
fi

echo
echo "files parsed: $total"
echo "ERROR nodes:  $errors"
echo "MISSING nodes: $missing"
echo "bad files:     $bad_files"

if (( errors > 0 || missing > 0 || bad_files > 0 )); then
  echo "RESULT: FAIL"
  exit 1
fi
echo "RESULT: PASS"