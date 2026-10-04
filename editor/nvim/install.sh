#!/usr/bin/env bash
# Build the quill parser and install it (plus its queries) into neovim's
# site directory, so an ABI-15 parser is found at
#   ~/.local/share/nvim/site/parser/quill.so
# with queries at
#   ~/.local/share/nvim/site/queries/quill/highlights.scm
#
#   ./install.sh [site-dir]        # default: ${XDG_DATA_HOME:-$HOME/.local/share}/nvim/site
#
# Then put this repo's editor/nvim on your runtimepath:
#   --cmd 'set rtp+=/home/lyh/pullground/quill/editor/nvim'

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
grammar="$(cd "$here/../tree-sitter-quill" && pwd)"
site="${1:-${XDG_DATA_HOME:-$HOME/.local/share}/nvim/site}"

echo "grammar: $grammar"
echo "site:    $site"

# 1. regenerate from grammar.js + scanner.c (keeps the committed parser.c honest)
( cd "$grammar" && tree-sitter generate )

# 2. compile to a shared object. tree-sitter build --output is the supported
#    way; fall back to cc if the CLI is older than that flag.
mkdir -p "$site/parser" "$site/queries/quill"
so="$site/parser/quill.so"
if tree-sitter build --output "$so" "$grammar" 2>/dev/null; then
  echo "built: tree-sitter build --output $so $grammar"
else
  cc -O2 -fPIC -shared -I "$grammar/src" \
     -o "$so" "$grammar/src/parser.c" "$grammar/src/scanner.c"
  echo "built: cc -O2 -fPIC -shared -o $so parser.c scanner.c"
fi

# 3. queries: one source of truth, copied (the symlink in editor/nvim is to the
#    same file, so there is no second copy to drift)
install -m 644 "$grammar/queries/highlights.scm" "$site/queries/quill/highlights.scm"

echo
file "$so"
echo "installed: $so"
echo "installed: $site/queries/quill/highlights.scm"