" Vim syntax file for Quill
" Language: Quill — a dependently-typed language with enforested macros
" Repository: /home/lyh/pullground/quill
"
" Static highlighter: handles keywords, comments, strings, numbers, operators.
" Known limits (see docs/wayfinder/tickets/syntax-highlighting.md):
"   - operators are coloured by character class, not by whether `pub infix`
"     declared them at that point — a variable named `+` will be mis-coloured
"   - macro uses and macro-generated syntax are not resolved
"   - a name's role (variable / type / constructor) is positional, not resolved

if exists("b:current_syntax")
  finish
endif

" Keywords — the fixed reserved set from TokenTree.cs
syntax keyword funKeyword
  \ let sig fn do match effect module struct enum impl trait
  \ pub import open export macro pattern self Self ref deref rec
  \ perform resume method

" Comments — line (# ...) and nested block (#| ... |#)
syntax match funComment "#.*$" contains=@Spell
syntax region funComment start="#|" end="|#" contains=funComment,@Spell

" Strings and character literals
syntax region funString start=+"+ skip=+\\\\\|\\"+ end=+"+ contains=@Spell
syntax match funChar "'\\?[^']'"

" Numbers — non-negative integers (the only numeric literal)
syntax match funNumber "\<\d\+\>"

" Operators — maximal munch over the operator character class.
" = | -> are punctuation in the reader but coloured as operators for readability.
syntax match funOperator "[-+*/%=!<>@~&|$]\+"

" Punctuation
syntax match funPunctuation "[()\[\]{},.;:]"

" Type / constructor names — capitalized identifiers
syntax match funType "\<[A-Z][A-Za-z0-9_?!]*\>"

let b:current_syntax = "quill"

" Highlight links
highlight default link funKeyword    Keyword
highlight default link funComment    Comment
highlight default link funString     String
highlight default link funChar       Character
highlight default link funNumber     Number
highlight default link funOperator   Operator
highlight default link funPunctuation Delimiter
highlight default link funType       Type
