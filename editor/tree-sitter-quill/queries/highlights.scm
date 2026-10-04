; Highlights for quill.
;
; The grammar models the *reader*, not the enforester: a name's role (type,
; binding, definition head) lives in a binding table and is not recoverable
; from a context-free parse, so this file highlights by *shape*, not by role.

; ---- literals -----------------------------------------------------------

(string) @string
(char) @character
(integer) @number

; ---- comments -----------------------------------------------------------
; `#` plain, `##` doc, `#|` block (nesting handled by the external scanner),
; `#_` datum -- which is a real token in the language, never trivia.

[(line_comment) (doc_comment) (block_comment)] @comment
(doc_comment) @comment.documentation
(datum_comment) @comment.special

; ---- keywords -----------------------------------------------------------
; Exactly the 26 in src/Quill.Kernel/TokenTree.cs. `then`, `with`, `end`,
; `else`, `Unit`, `if` and `and` are deliberately absent -- they are ordinary
; identifiers, and the corpus really does use them that way.

[
  "let" "fun" "sig" "fn" "do" "match" "effect" "module" "struct" "enum"
  "impl" "trait" "pub" "import" "open" "export" "macro" "pattern" "self"
  "Self" "ref" "deref" "rec" "perform" "resume" "method"
] @keyword

(operator) @operator
(sigil) @special

; ---- identifiers --------------------------------------------------------
; Types and constructors are BOTH capitalised in quill, so `(identifier)` can
; only ever be `@type`. A name used as a term at all -- `map`, `Some`, `x` --
; is left to the editor default. This conflating is the accepted best-effort
; loss.

; Neovim applies captures in query order, later wins for the same range, so
; the catch-all comes FIRST and the specific patterns override it.

((identifier) @variable)

; a capitalised name is a type or a constructor; nothing distinguishes them
((identifier) @type
  (#match? @type "^[A-Z]"))

((identifier) @variable.builtin
  (#eq? @variable.builtin "_"))

((identifier) @constant
  (#any-of? @constant "True" "False" "Nil"))

; `Some`, `Cons` and `None` are constructors, but `None` is above already.
((identifier) @constructor
  (#any-of? @constructor "Some" "Cons"))

; ---- punctuation --------------------------------------------------------

[
  "," "." ":" ";" "=" "|" "->"
] @punctuation.delimiter

(paren_group ["(" ")"]) @punctuation.bracket
(bracket_group ["[" "]"]) @punctuation.bracket
(brace_group ["{" "}"]) @punctuation.bracket