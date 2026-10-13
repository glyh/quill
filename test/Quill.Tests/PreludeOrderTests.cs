using Quill.Compiler;

namespace Quill.Tests;

/// <summary>
/// The prelude's load order is derived, not written down: the DFS post-order of the
/// import graph rooted at the <c>std</c> unit, following each unit's import lines in
/// the order they are written, with the bootstrap pinned first.
/// </summary>
public class PreludeOrderTests
{
    /// <summary>A unit source with one import line per given path, in order.</summary>
    private static string Unit(params string[] imports) =>
        string.Join("\n", imports.Select((path, i) => $"u{i} = import \"{path}\";"));

    [Fact]
    public void TheOrderIsDfsPostOrderFollowingWrittenImports()
    {
        var sources = new Dictionary<string, string>
        {
            ["root"] = Unit("b", "a"),
            ["a"] = Unit("base"),
            ["b"] = Unit("base"),
            ["base"] = Unit(),
        };
        string[] expected = ["base", "b", "a", "root"];
        Assert.Equal(expected, Prelude.DeriveOrder(sources, "root"));
    }

    /// <summary>The seven std units as the sources actually stand.</summary>
    [Fact]
    public void TheRealStdOrderIsTheSevenUnitsBootstrapFirst()
    {
        string[] expected = ["std/bootstrap", "std/lib", "std/list", "std/type", "std/option", "std/functor", "std"];
        Assert.Equal(expected, Prelude.DeriveOrder(Prelude.UnitSources(), Prelude.Path, Prelude.BootstrapPath));
    }

    [Fact]
    public void ACycleIsRefusedNamingIt()
    {
        var sources = new Dictionary<string, string>
        {
            ["a"] = Unit("b"),
            ["b"] = Unit("a"),
        };
        var failure = Assert.Throws<InvalidOperationException>(() => Prelude.DeriveOrder(sources, "a"));
        Assert.Contains("\"a\" -> \"b\" -> \"a\"", failure.Message);
    }

    /// <summary>The point of the derivation: a new unit joins the order by being imported.</summary>
    [Fact]
    public void AUnitAddedToTheGraphIsFoundWithoutACompilerEdit()
    {
        var sources = new Dictionary<string, string>
        {
            ["root"] = Unit("extra", "b", "a"),
            ["extra"] = Unit(),
            ["a"] = Unit("base"),
            ["b"] = Unit("base"),
            ["base"] = Unit(),
        };
        string[] expected = ["extra", "base", "b", "a", "root"];
        Assert.Equal(expected, Prelude.DeriveOrder(sources, "root"));
    }
}
