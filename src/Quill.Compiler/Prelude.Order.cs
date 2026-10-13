using Quill.Expand;
using Quill.Kernel;

namespace Quill.Compiler;

/// <summary>
/// The prelude's load order, derived instead of written: the DFS post-order of the
/// import graph rooted at the <c>std</c> unit, following each unit's import lines in
/// the order they are written, with the bootstrap pinned first. Post-order is what puts
/// every unit's imports below it; the bootstrap is pinned rather than placed because it
/// is the ABI root and imports nothing, so first is the only position that never makes
/// it wait on a unit still being elaborated.
/// </summary>
public static partial class Prelude
{
    /// <summary>Every embedded std unit's source, by unit path: what the derivation walks.</summary>
    public static IReadOnlyDictionary<string, string> UnitSources() =>
        typeof(Prelude).Assembly.GetManifestResourceNames()
            .Where(r => r.StartsWith("std/") && r.EndsWith(".qll"))
            .ToDictionary(UnitPath, r => Source(r["std/".Length..]));

    /// <summary>The inverse of <see cref="File"/>: the resource <c>std/x.qll</c> is the unit <c>std/x</c>.</summary>
    private static string UnitPath(string resource) =>
        resource == "std/stage2.qll" ? Path : "std/" + resource["std/".Length..^".qll".Length];

    /// <summary>
    /// The load order for the units <paramref name="sources"/> holds (unit path to source
    /// text), rooted at <paramref name="root"/>: the DFS post-order of following each
    /// unit's import lines in the order they are written, with <paramref name="pinned"/>,
    /// when the walk reaches it, moved to the front. Every unit's imports land before it.
    /// A cycle is refused, naming it. An import of a unit <paramref name="sources"/> does
    /// not hold is not a node of the graph: the loader reports it at load.
    /// </summary>
    public static string[] DeriveOrder(IReadOnlyDictionary<string, string> sources, string root, string? pinned = null)
    {
        var order = new List<string>();
        var done = new HashSet<string>();
        var walking = new List<string>();
        void Walk(string path)
        {
            if (done.Contains(path)) return;
            var cycle = walking.IndexOf(path);
            if (cycle >= 0)
                throw new InvalidOperationException(
                    $"circular import: {string.Join(" -> ", walking.Skip(cycle).Append(path).Select(p => $"\"{p}\""))}");
            walking.Add(path);
            foreach (var next in Imports(sources[path]))
                if (sources.ContainsKey(next)) Walk(next);
            walking.RemoveAt(walking.Count - 1);
            done.Add(path);
            order.Add(path);
        }

        Walk(root);
        if (pinned is not null && order.Remove(pinned)) order.Insert(0, pinned);
        return [.. order];
    }

    /// <summary>The units one source imports, in the order its import lines are written.</summary>
    private static IEnumerable<string> Imports(string source)
    {
        var leaves = new List<Token>();
        Leaves(Reader.Read(source), leaves);
        for (var i = 0; i + 1 < leaves.Count; i++)
            if (leaves[i].Kind == TokenKind.Import && leaves[i + 1].Kind is TokenKind.Str str)
                yield return str.Value;
    }

    /// <summary>Every token in the trees, in the order they are written, groups included.</summary>
    private static void Leaves(IEnumerable<TokenTree> trees, List<Token> into)
    {
        foreach (var tree in trees)
            switch (tree)
            {
                case TokenTree.Leaf leaf: into.Add(leaf.Token); break;
                case TokenTree.Group group: Leaves(group.Items, into); break;
                default: throw new InvalidOperationException($"unhandled token tree {tree.GetType().Name}");
            }
    }
}
