# test/lexical.qll -- hand-written full-surface lexical test.
#
# The 1043-file corpus under std/ and test/conformance/ contains ZERO block
# comments, ZERO datum comments and ZERO `?`/`!`-suffixed identifiers, so the
# corpus alone cannot catch a regression in any of them. Everything the corpus
# misses is exercised here. Not a real program: it is a lexical torture file.

## <summary>A `##` doc comment, distinct from a plain `#` line comment.</summary>
# a plain line comment with a backtick `Std.Options.f` and an em dash — ok.

#| a block comment |# is trivia; but it NESTS:
#| outer #| inner |# still outer |#
#| outer
   #| deeper #| deepest |# deeper |#
   |# outer |#

#_ a datum comment discards the next datum, token or group:
#_ (pub hidden = 1)
#_ struct Hidden { field: I64 }
#_ hidden_token
let datum_discards_group_but_keeps_the_rest = fn(x) { x };

# identifier continuations: `?` and `!` are part of the name, not operators.
pub foo? = 1;
pub bar! = 2;
pub is_empty? = fn(xs) { xs };
pub check! = fn(x) { x };
# ...but a `?` or `!` after a *space* or at the head of an operator is an operator.
pub spaced? = 3;

# integers only: no floats, no hex, no underscores, no leading `-`.
pub n0 = 0;
pub n42 = 42;
pub big = 1043;
pub neg = fn(x) { 0 - x };

# strings, with the escapes the reader accepts and a literal `\q`.
pub s_plain = "hello";
pub s_escapes = "line\n tab\t ret\r backslash\\ quote\" apostrophe\'";
pub s_unknown_escape = "kept \q literally";
pub s_empty = "";
pub s_brackets = "[{()}] # not a comment #_ not a datum comment";

# chars: one char, or one of the six escapes.
pub c_a = 'a';
pub c_space = ' ';
pub c_brace = '}';
pub c_esc = '\n';
pub c_esc_tab = '\t';
pub c_esc_ret = '\r';
pub c_esc_bs = '\\';
pub c_esc_sq = '\'';
pub c_esc_dq = '\"';
pub c_unicode = 'λ';

# `$` is a single token, never part of operator munching.
pub dollar = $;
pub dollar_name = $foo;
pub dollars = fn(a, b) { $ };

# `^` is a dedicated token: a name-pinning sigil in a pattern.
pub pinned = fn(xs) { match (xs) { ^x => x, [] => 0 } };

# maximal munch: these are ONE operator token each, not two.
pub eq2 = fn(a, b) { a == b };
pub ne = fn(a, b) { a != b };
pub oror = fn(a, b) { a || b };
pub andand = fn(a, b) { a && b };
pub arrow2 = fn(a, b) { a ->> b };
pub fatarrow = fn(a, b) { a => b };
pub plusplus = fn(a, b) { a + b };
pub star = fn(a, b) { a * b };
pub pct = fn(a, b) { a % b };
pub lt_gt = fn(a, b) { a < b && a > b };
pub at = fn(a) { a @ 1 };
pub tilde = fn(a) { ~a };
pub amp = fn(a, b) { a & b };
pub mix = fn(a, b) { a <= b && b >= a && a == b };

# a lone `=`, a lone `|`, and `->` are punctuation; `|` also opens a match arm.
pub lone_eq = 1;
pub lone_arrow = fn(f: Type, x: Type) { f : Type -> Type = fn(y) { x } };
pub bars = fn(cond, a, b) { match (cond) { true => a, | false => b } };

# all 26 keywords, plus the words that are deliberately NOT keywords.
let a = 1;
fun b() { a };
sig c() { a: I64 };
fn d() { a };
do e() { a };
match f(x) { x => x };
effect g = sig { hit: I64 -> I64 };
module h { pub i = 1 };
struct j { k: I64 };
enum l { m, n };
impl o { pub p() { 1 } };
trait q { pub r() { 1 } };
pub s = 1;
import t;
open u;
export v;
macro w(x) { x };
pattern x2 = fn(y) { y };
pub self1 = fn(self) { self };
pub Self2 = fn(self: Type) { self };
pub ref1 = fn(r) { r };
pub deref1 = fn(r) { r };
rec rec1 = fn(n) { n };
pub perform1 = fn(x) { perform g.hit(x) };
pub resume1 = fn(k) { resume(k) };
pub method1 = fn(self) { self };

# NOT keywords in quill -- these are ordinary identifiers, and the corpus
# really does use them that way:
pub then = fn(cond, a, b) { cond then a else b };
pub with = 1;
pub end = 2;
pub else_ = 3;
pub else = 4;
pub Unit = 5;
pub if_ = fn(cond, a, b) { if (cond) { a } else { b } };
pub and = 1;
pub unless = fn(xs) { xs };

# `_` is the wildcard identifier.
pub wild = fn(xs) { match (xs) { _ => 0 } };

# deliberately weird but lexable: punctuation everywhere, groups nested deep.
pub deep = (((((1))))));
pub mixed = { a: [1, 2], b: (3, 4), c: { d: { e: 5 } } };
pub semis = struct { a: I64; b: I64; c: I64; };
pub commas = match (1) { 1 => 2, 2 => 3, _ => 4 };
pub dots = 1.2.3;
pub row = fn(x) { x ->{g} x };