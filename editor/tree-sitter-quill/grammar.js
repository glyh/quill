// Tree-sitter grammar for `quill`, modelled on the *reader*
// (src/Quill.Expand/Reader.cs), not the enforester.
//
// The reader turns source into a sequence of tokens and delimiter groups:
// that is the context-free layer of the language. Everything above it -- what
// role a name plays, what an operator means, whether a keyword is in head
// position -- comes from macro expansion and a binding table, and is
// deliberately not modelled here. So the grammar is a permissive sequence of
// items, where an item is a token or a bracketed group of items.
//
// The lexical rules below are transcribed from Reader.Scanner:
//   * identifiers start [A-Za-z_], continue [A-Za-z0-9_?!]  (so `foo?`, `bar!`
//     are single identifiers);
//   * integers are [0-9]+ only;
//   * operators are a maximal munch over +-*/%=!<>@~&|, except that a lone
//     `=` and a lone `|` and `->` are punctuation -- which is exactly what
//     tree-sitter's "a string literal beats a regexp of equal length" rule
//     gives us for free;
//   * `$` and `^` are standalone tokens;
//   * `then`, `with`, `end`, `else`, `Unit`, `if`, `and` are *identifiers*.
//
// `_unexpected` is a total catch-all so that a character the reader would
// reject still parses without an ERROR node. Highlighting degrades; it does
// not break.

const KEYWORDS = [
  'let', 'fun', 'sig', 'fn', 'do', 'match', 'effect', 'module', 'struct',
  'enum', 'impl', 'trait', 'pub', 'import', 'open', 'export', 'macro',
  'pattern', 'self', 'Self', 'ref', 'deref', 'rec', 'perform', 'resume',
  'method',
];

// The group openers/closers are NOT listed here: `( ) [ ] { }` belong to the
// group rules below, and a token shared between two rules is an undeclared
// conflict. Stray closers are fine -- `_unexpected` catches them.
const PUNCT = [
  ',', '.', ':', ';', '=', '|', '->',
];

module.exports = grammar({
  name: 'quill',

  externals: $ => [
    // Nesting needs a real scanner; a regexp cannot count `#|`/`|#`.
    $.block_comment,
  ],

  extras: $ => [
    /[\s]/,
    $.line_comment,
    $.doc_comment,
    $.block_comment,
  ],


  word: $ => $.identifier,

  rules: {
    source_file: $ => repeat($._item),

    _item: $ => choice(
      $._group,
      $._token,
      $._unexpected,
    ),

    _group: $ => choice(
      $.paren_group,
      $.bracket_group,
      $.brace_group,
    ),

        // Group openers win over the same character read as punctuation.
    paren_group: $ => prec(1, seq('(', repeat($._item), ')')),
    bracket_group: $ => prec(1, seq('[', repeat($._item), ']')),
    brace_group: $ => prec(1, seq('{', repeat($._item), '}')),

    _token: $ => choice(
      $.keyword,
      $.identifier,
      $.integer,
      $.string,
      $.char,
      $.operator,
      $.datum_comment,
      $.sigil,
      $._punct,
    ),

    _punct: _ => choice(...PUNCT),

    keyword: _ => choice(...KEYWORDS),
    sigil: _ => choice('$', '^'),

    // `?` and `!` continue an identifier: `foo?`, `bar!` are one token.
    identifier: _ => /[A-Za-z_][A-Za-z0-9_?!]*/,
    integer: _ => /[0-9]+/,

    // Any `\x` is kept literally as a backslash plus the character.
    string: _ => token(seq('"', repeat(choice(/[^"\\]/, /\\./)), '"')),

    char: _ => token(seq('\'',
      choice(/[^'\\\n\r]/, /\\[ntr\\'"]/),
      '\'')),

    operator: _ => /[+\-*/%!=<>@~&|]+/,

    // `#_` is a datum comment: a real token, never trivia. It wins over
    // `line_comment` on length, and on precedence for the bare `#` case.
    datum_comment: _ => token(prec(2, '#_')),

    // `##` doc comment, distinct from a plain `#` line comment.
    doc_comment: _ => token(prec(2, /##[^\n]*/)),

    // `#` ... end of line, but never `#_` (see above) and never `#|`. The
    // reader does NOT stop at `|`: a line comment runs to the newline.
    line_comment: _ => token(prec(1, /#([^_\n|][^\n]*)?/)),

    // Whitespace is excluded: the extras pattern owns it, and a catch-all that
    // also matches spaces makes them part of the next token's range.
    _unexpected: _ => token(prec(-1, /[^ \t\r\n]/)),
  },
});