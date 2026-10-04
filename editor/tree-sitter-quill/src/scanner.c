// External scanner for quill's nested block comments (`#|` ... `|#`).
// A regexp cannot count nesting, so `#| a #| b |# c |#` needs a real scanner.
// See src/Quill.Expand/Reader.cs (Reader.Scanner.SkipBlockComment).

#include "tree_sitter/parser.h"

#include <stdbool.h>
#include <stdint.h>

enum TokenType { BLOCK_COMMENT };

void *tree_sitter_quill_external_scanner_create(void) { return NULL; }

void tree_sitter_quill_external_scanner_destroy(void *payload) { (void)payload; }

unsigned tree_sitter_quill_external_scanner_serialize(void *payload, char *buffer) {
  (void)payload;
  (void)buffer;
  return 0;
}

void tree_sitter_quill_external_scanner_deserialize(void *payload, const char *buffer,
                                                   unsigned length) {
  (void)payload;
  (void)buffer;
  (void)length;
}

static inline bool is_space(int32_t c) {
  return c == ' ' || c == '\t' || c == '\r' || c == '\n';
}

static inline bool is_hash_pipe(TSLexer *lexer) {
  if (lexer->lookahead != '#') return false;
  lexer->advance(lexer, false);
  if (lexer->lookahead != '|') return false;
  return true;
}

static inline bool is_pipe_hash(TSLexer *lexer) {
  if (lexer->lookahead != '|') return false;
  lexer->advance(lexer, false);
  return lexer->lookahead == '#';
}

bool tree_sitter_quill_external_scanner_scan(void *payload, TSLexer *lexer,
                                            const bool *valid_symbols) {
  (void)payload;
  if (!valid_symbols[BLOCK_COMMENT]) return false;

  // The external scanner runs before the grammar's whitespace extras, so it has
  // to step over leading space itself. Skipped characters are passed with
  // `skip = true` and stay outside the token.
  while (is_space(lexer->lookahead)) lexer->advance(lexer, true);

  if (!is_hash_pipe(lexer)) return false;
  lexer->advance(lexer, false);  // the '#' of the opening "#|"
  lexer->advance(lexer, false);  // the '|'

  unsigned depth = 1;
  while (depth > 0) {
    if (lexer->eof(lexer)) return false;  // unterminated: let the grammar catch it
    if (is_hash_pipe(lexer)) {
      depth++;
      lexer->advance(lexer, false);
      lexer->advance(lexer, false);
    } else if (is_pipe_hash(lexer)) {
      depth--;
      lexer->advance(lexer, false);
      lexer->advance(lexer, false);
    } else {
      lexer->advance(lexer, false);
    }
  }
  lexer->result_symbol = BLOCK_COMMENT;
  return true;
}