using Quill.Compiler;
using Quill.Kernel;

namespace Quill.Tests;

/// <summary>
/// Evidence resolution: the exact failure, so a conformance case expecting
/// `error` cannot pass through the wrong one.
/// </summary>
public class TraitTests
{
    private const string Size = "trait Size(a) = sig { size : a -> I64 }; ";
    private const string Bounded = "f : [A : Size] -> A -> I64 = fn[A : Type](x) { Size.size(x) }; f(3) }";

    private static string Elaborate(string source) =>
        Assert.ThrowsAny<FunException>(() => Driver.Elaborate(source, new Dictionary<string, string>())).Message;

    private static string Run(string source) =>
        Driver.Describe(Driver.Run(Driver.Elaborate(source, new Dictionary<string, string>())));

    [Fact]
    public void AnImplInsideAModuleIsNotEvidenceUntilOpened() =>
        Assert.Equal("missing implementation of `Size` while inferring the form at <unknown>:1:173-1:177",
            Elaborate("{ " + Size + "M = module { pub impl Size(I64) = module { size = fn(x) { 4 } } }; " + Bounded));

    [Fact]
    public void TwoDifferentImplsForOneArgumentAreAmbiguous() =>
        Assert.Equal("ambiguous implementation of `Size` while inferring the form at <unknown>:1:256-1:260",
            Elaborate("{ " + Size
                + "M = module { pub impl Size(I64) = module { size = fn(x) { 1 } } }; "
                + "N = module { pub impl Size(I64) = module { size = fn(x) { 2 } } }; open M; open N; " + Bounded));

    /// <summary>Opening one module twice brings its impl once: the same impl is not an ambiguity.</summary>
    [Fact]
    public void OpeningAModuleTwiceIsNotAnAmbiguity() =>
        Assert.Equal("4",
            Run("{ " + Size + "M = module { pub impl Size(I64) = module { size = fn(x) { 4 } } }; open M; open M; " + Bounded));

    /// <summary>A dictionary is resolved by its argument: an impl for another type is not evidence.</summary>
    [Fact]
    public void AnImplForAnotherArgumentIsNotEvidence() =>
        Assert.Equal("missing implementation of `Size` while inferring the form at <unknown>:1:155-1:159",
            Elaborate("{ " + Size + "impl Size(Char) = module { size = fn(x) { 1 } }; " + Bounded));

    [Fact]
    public void AnImplMustGiveEveryOperation() =>
        Assert.Equal("missing trait field `size` while inferring the form at <unknown>:1:43-1:73", Elaborate("{ " + Size + "impl Size(I64) = module { }; 0 }"));

    /// <summary>A trait's parameter binds, so it must be lowercase.</summary>
    [Fact]
    public void ATraitParameterMustBeLowercase() =>
        Assert.Equal("a trait parameter `A` must be lowercase; write `a` to bind while inferring the form at <unknown>:1:0-1:46",
            Elaborate("{ trait Size(A) = sig { size : A -> I64 }; 0 }"));

    /// <summary>
    /// An uppercase free name in an impl head is a reference, not a binder, so an
    /// unresolved one is an error rather than a silent fresh variable.
    /// </summary>
    [Fact]
    public void AnImplHeadBinderMustBeLowercase() =>
        Assert.Equal("`A` in an impl head is a reference, not a binder; an impl head binder must be lowercase; write `a` to bind while inferring the form at <unknown>:1:43-1:98",
            Elaborate("{ trait Size(a) = sig { size : a -> I64 }; impl Size(Option(A)) = module { size = fn(o) { 0 } }; 0 }"));

    private const string TwoImpls = "impl Size(I64) = module { size = fn(n) { 1 } }; impl Size(Char) = module { size = fn(c) { 2 } }; ";

    /// <summary><c>Trait.op</c> chooses by the argument's type, not by which impl is nearest.</summary>
    [Fact]
    public void TraitOpChoosesByArgumentType() =>
        Assert.Equal("1", Run("{ " + Size + TwoImpls + "Size.size(5) }"));

    /// <summary>Nearness never breaks a tie: <c>Trait.op</c> meets the same ambiguity a bound does.</summary>
    [Fact]
    public void NearnessDoesNotBreakATie() =>
        Assert.Equal("ambiguous implementation of `Size` while inferring the form at <unknown>:1:166-1:178",
            Elaborate("{ " + Size + "impl Size(I64) = module { size = fn(n) { 1 } }; "
                + "M = module { pub impl Size(I64) = module { size = fn(n) { 2 } } }; open M; Size.size(5) }"));

    /// <summary>A choice waits for an argument type the rest of the unit solves.</summary>
    [Fact]
    public void AChoiceWaitsForItsArgumentType() =>
        Assert.Equal("2", Run("{ " + Size + TwoImpls + "(fn(x) { Size.size(x) })('c') }"));

    /// <summary>An argument type nothing ever solves is an error at the end of the unit, never a guess.</summary>
    [Fact]
    public void AnArgumentTypeNeverKnownIsAnError() =>
        Assert.Equal("cannot choose an implementation of `Size`: its argument type is never known",
            Elaborate("{ " + Size + TwoImpls + "(fn(x) { Size.size(x) }) }"));

    /// <summary>A generic impl's own variables order the candidates: neither of two incomparable heads is chosen.</summary>
    [Fact]
    public void IncomparableGenericImplsAreAmbiguous() =>
        Assert.Equal("ambiguous implementation of `Conv` while inferring the form at <unknown>:1:150-1:181",
            Elaborate("{ trait Conv(a) = sig { conv : a -> I64 }; "
                + "impl Conv(I64 -> a) = module { conv = fn(p) { 1 } }; "
                + "impl Conv(b -> Bool) = module { conv = fn(p) { 2 } }; "
                + "Conv.conv(fn(x : I64) { True }) }"));

    /// <summary>
    /// Resolution's match ignores a record's non-field members; equality does not. A
    /// head naming `a` serves a record with a public method, but that record does not
    /// take a plain-struct annotation, and the refusal is the member-count mismatch.
    /// </summary>
    [Fact]
    public void MatchingIgnoresNonFieldMembersWhereEqualityDoesNot()
    {
        Assert.Equal("1", Run("{ " + Size
            + "U = struct { a : I64; pub method m() : I64 { self.a } }; "
            + "impl Size(struct { a : I64 }) = module { size = fn(x) { 1 } }; "
            + "impl Size(_) = module { size = fn(x) { 0 } }; Size.size(U{a = 1}) }"));
        Assert.Equal("type mismatch: structs with different members while checking the form at <unknown>:1:91-1:99",
            Elaborate("{ P = struct { a : I64 }; U = struct { a : I64; pub method m() : I64 { self.a } }; "
                + "x : P = U{a = 1}; x.a }"));
    }

    /// <summary>
    /// A generic impl's own variable is a parameter: evidence the body uses for it is a
    /// hidden dictionary, and evidence the head does not bind fails at the definition -
    /// never through the use site whose argument type is unrelated.
    /// </summary>
    [Fact]
    public void AnImplCannotUseEvidenceItsHeadDoesNotBind() =>
        Assert.Equal("missing implementation of `Size` while inferring the form at <unknown>:1:138-1:150",
            Elaborate("{ " + Size + "impl Size(I64) = module { size = fn(n) { 1 } }; "
                + "impl Size(Option(a)) = module { size = fn(o) { Size.size(o) } }; 0 }"));

    /// <summary>
    /// A refinement/evidence desync is the implementation's break, not the user's: the
    /// invariant names both sides (evidence-desync-is-an-invariant). Only a context no
    /// program produces reaches it - the refinement marker set while an evidence entry
    /// still reads the matched variable - so it is built by hand here.
    /// </summary>
    [Fact]
    public void AStaleEvidenceEntryIsAnInvariantNotAMissingImplementation()
    {
        var ctx = Desynced(out var size, over: new Value.VAtomTy(AtomTy.Char));
        var failure = Elaborator.MissingImplementation(ctx, size, [new Value.VAtomTy(AtomTy.Char)]);
        var invariant = Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal("evidence for `Size` was not refined: the branch asks for Size(Char), "
            + "the entry still says Size(A). RefineContext's Evidence channel missed it.", invariant.Message);
    }

    /// <summary>
    /// The desync branch keys on "refinement would have answered the demand", not on the
    /// entry merely reading the matched variable: a demand no refined entry answers is
    /// still the user's missing implementation.
    /// </summary>
    [Fact]
    public void AnEntryRefinementWouldNotAnswerIsStillAGenuineAbsence()
    {
        var ctx = Desynced(out var size, over: new Value.VAtomTy(AtomTy.Char));
        var failure = Elaborator.MissingImplementation(ctx, size, [new Value.VAtomTy(AtomTy.I64)]);
        Assert.IsType<FunException>(failure);
        Assert.Equal("missing implementation of `Size`", failure.Message);
    }

    /// <summary>No dictionary in scope at all: the user-facing error is not swallowed.</summary>
    [Fact]
    public void NoDictionaryInScopeIsAMissingImplementation() =>
        Assert.Equal("missing implementation of `Size` while inferring the form at <unknown>:1:43-1:57",
            Elaborate("{ " + Size + "Size.size('c') }"));

    /// <summary>A branch context refined to <paramref name="over"/> whose one evidence entry still reads the matched variable <c>A</c>.</summary>
    private static Context Desynced(out TraitDecl size, Value over)
    {
        size = new TraitDecl("Size", [], Value.VU.Instance);
        var ctx = Context.Empty(new MetaContext()).Bind("A", Value.VU.Instance);
        var matched = new Value.VVar(0, []);
        (ctx, var dict) = ctx.BindAnonymous(new Value.VTraitDict(size, [matched], []));
        return ctx.AddEvidence(new TraitEvidence(size, [matched], dict.Level, dict.Type))
            with { Refinement = (0, over) };
    }
}
