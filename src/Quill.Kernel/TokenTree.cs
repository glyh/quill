using System.Collections.Immutable;

namespace Quill.Kernel;

public enum Delimiter { Paren, Bracket, Brace }

/// <summary>
/// A raw token. Keyword and punctuation kinds are singletons; the ones that
/// carry text or a literal are records.
/// </summary>
public abstract partial record TokenKind
{
    public sealed record Ident(string Name) : TokenKind;
    public sealed record Int(long Value) : TokenKind;
    public sealed record Char(char Value) : TokenKind;
    public sealed record Str(string Value) : TokenKind;
    public sealed record Operator(string Spelling) : TokenKind;

    /// <summary>A keyword or punctuation token, identified by its spelling.</summary>
    public sealed record Word(string Spelling) : TokenKind;

    // Keywords. `then`, `with`, `end`, `else`, `Unit` and `fun` are deliberately absent:
    // they are ordinary identifiers (STATUS.md, stage 11 increment 2; `fun` per
    // docs/wayfinder/tickets/vestigial-fun-keyword.md).
    public static readonly Word Let = new("let"), Sig = new("sig"),
        Fn = new("fn"), Do = new("do"), Match = new("match"), Effect = new("effect"),
        Module = new("module"), Struct = new("struct"), Enum = new("enum"),
        Impl = new("impl"), Trait = new("trait"), Pub = new("pub"), Import = new("import"),
        Open = new("open"), Export = new("export"), Macro = new("macro"),
        Pattern = new("pattern"), Self = new("self"), SelfType = new("Self"),
        Ref = new("ref"), Deref = new("deref"), Rec = new("rec"),
        Perform = new("perform"), Resume = new("resume"), Method = new("method");

    // Punctuation.
    public static readonly Word LParen = new("("), RParen = new(")"),
        LBracket = new("["), RBracket = new("]"), LBrace = new("{"), RBrace = new("}"),
        Comma = new(","), Dot = new("."), Colon = new(":"), Eq = new("="),
        Semi = new(";"), Bar = new("|"), ThinArrow = new("->"),
        DatumComment = new("#_"), Eof = new("EOF"),
        // `^` is a dedicated prefix token, never an operator: it pins a name in a
        // pattern (a pattern-binders-are-lowercase ruling), and structural
        // punctuation keeps dedicated tokens rather than joining the operator set.
        Caret = new("^");

    /// <summary>The keywords, by spelling. Punctuation is matched by the reader directly.</summary>
    public static readonly ImmutableDictionary<string, Word> Keywords =
        new[] { Let, Sig, Fn, Do, Match, Effect, Module, Struct, Enum, Impl, Trait,
                Pub, Import, Open, Export, Macro, Pattern, Self, SelfType, Ref, Deref,
                Rec, Perform, Resume, Method }
            .ToImmutableDictionary(w => w.Spelling);

    /// <summary>What this token looks like in source, for diagnostics.</summary>
    public string Text() => this switch
    {
        Word w => w.Spelling,
        Ident i => i.Name,
        Operator o => o.Spelling,
        Int => "integer",
        Char => "char",
        Str => "string",
        _ => throw new InvalidOperationException($"unhandled token {GetType().Name}"),
    };
}

/// <summary>
/// A token with its scope set. The reader gives every token the empty set;
/// enforestation adds the scopes around it before it is read as a form, so a
/// syntactic role resolves against the token's scopes (M7).
/// </summary>
public sealed record Token(TokenKind Kind, SourceSpan Span, ScopeSet Scope)
{
    public Token(TokenKind kind, SourceSpan span) : this(kind, span, ScopeSet.Empty) { }
}

/// <summary>
/// The reader's output: tokens and delimiter groups. In the kernel so unparsed
/// bodies can sit inside a <see cref="Syntax"/> node.
/// </summary>
public abstract partial record TokenTree(SourceSpan Span)
{
    public sealed record Leaf(Token Token) : TokenTree(Token.Span);

    public sealed record Group(Delimiter Delimiter, EquatableArray<TokenTree> Items, SourceSpan GroupSpan)
        : TokenTree(GroupSpan);

    /// <summary>Adds <paramref name="scope"/> to every token in the tree, inside groups too.</summary>
    public TokenTree AddScope(ScopeSet scope) => this switch
    {
        Leaf l => l with { Token = l.Token with { Scope = l.Token.Scope.Union(scope) } },
        Group g => g with { Items = [.. g.Items.Select(i => i.AddScope(scope))] },
        _ => throw new InvalidOperationException($"unhandled token tree {GetType().Name}"),
    };
}
