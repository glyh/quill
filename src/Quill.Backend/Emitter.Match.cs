using System.Reflection;
using System.Reflection.Emit;
using Quill.Kernel;

namespace Quill.Backend;

/// <summary>
/// The decision-tree walk: <see cref="Emitter.EmitMatch"/> evaluates the
/// scrutinee once and hands the tree here. <c>Leaf</c> binds the occurrence
/// positions it names into locals and runs the arm; <c>Destruct</c> reads the
/// constructor's <em>name</em> off the value and picks the case with that name;
/// <c>Switch</c> compares an atom. Every arm writes one local the whole tree
/// shares, so a nested tree chooses rather than returns; in tail position each
/// arm returns instead.
/// </summary>
/// <remarks>
/// The value tier is the interpreter's: a constructor is a
/// <see cref="Value.VCon"/>, read by name and payload. That is the interface this
/// walk needs from the constructor representation; the emitter does not build
/// one. See docs/wayfinder/topics/dotnet-backend-term-shapes.md.
/// </remarks>
internal sealed partial class Emitter
{
    /// <summary>A scrutinee local, root of every occurrence consumed below.</summary>
    private sealed record MatchValue(Cty Ty, LocalBuilder Slot);

    private void EmitMatchTree(Term.Match match, DecisionTree tree, MatchValue root, Cty expected,
        Scope scope, Ctx ctx, bool tail, LocalBuilder? result, Label end)
    {
        switch (tree)
        {
            case DecisionTree.Leaf leaf:
                EmitMatchArm(match, leaf, root, expected, scope, ctx, tail, result, end);
                break;
            case DecisionTree.Switch sw:
                EmitSwitch(match, sw, root, expected, scope, ctx, tail, result, end);
                break;
            case DecisionTree.Destruct destruct:
                EmitDestruct(match, destruct, root, expected, scope, ctx, tail, result, end);
                break;
            default:
                throw new NotImplementedException($"not ported yet: backend decision tree {tree.GetType().Name}");
        }
    }

    /// <summary>Choose an arm and run it.</summary>
    private void EmitMatchArm(Term.Match match, DecisionTree.Leaf leaf, MatchValue root, Cty expected,
        Scope scope, Ctx ctx, bool tail, LocalBuilder? result, Label end)
    {
        var bound = BindArm(leaf, root, scope, ctx);
        var produced = Emit(match.Bodies[leaf.Branch], expected, bound, ctx, tail);
        Convert(produced, expected, ctx);
        if (tail)
        {
            ctx.Il.Emit(OpCodes.Ret);
            return;
        }
        ctx.Il.Emit(OpCodes.Stloc, result!);
        ctx.Il.Emit(OpCodes.Br, end);
    }

    /// <summary>Each binder in source order, so arm index 0 sees the first binder first.</summary>
    private Scope BindArm(DecisionTree.Leaf leaf, MatchValue root, Scope scope, Ctx ctx)
    {
        foreach (var at in leaf.Bindings)
        {
            var ty = Loaded(at, root);
            var local = ctx.Il.DeclareLocal(ty.Clr());
            EmitAt(at, root, ctx);
            ctx.Il.Emit(OpCodes.Stloc, local);
            scope = scope.Push(new Binding.Local(ctx.Id, ctx.AddLocal(local), ty));
        }
        return scope;
    }

    /// <summary>Switch on an atom key; only an <c>I64</c> key has a comparison here.</summary>
    private void EmitSwitch(Term.Match match, DecisionTree.Switch sw, MatchValue root, Cty expected,
        Scope scope, Ctx ctx, bool tail, LocalBuilder? result, Label end)
    {
        var ty = EmitAt(sw.At, root, ctx);
        var slot = ctx.Il.DeclareLocal(ty.Clr());
        ctx.Il.Emit(OpCodes.Stloc, slot);

        foreach (var @case in sw.Cases)
        {
            if (@case.Key is not Atom.I64 key)
                throw new NotImplementedException($"not ported yet: backend switch key {@case.Key.GetType().Name}");
            var next = ctx.Il.DefineLabel();
            ctx.Il.Emit(OpCodes.Ldloc, slot);
            ctx.Il.Emit(OpCodes.Ldc_I8, key.Value);
            // An object-tier scrutinee is a boxed I64 (a prelude parameter), so the
            // key has to be boxed too: `Bne_Un` on an object and an int64 is not IL.
            if (ty is Cty.Obj)
            {
                ctx.Il.Emit(OpCodes.Box, typeof(long));
                ctx.Il.Emit(OpCodes.Callvirt, ObjectEquals);
                ctx.Il.Emit(OpCodes.Brfalse, next);
            }
            else ctx.Il.Emit(OpCodes.Bne_Un, next);
            EmitMatchTree(match, @case.Tree, root, expected, scope, ctx, tail, result, end);
            ctx.Il.MarkLabel(next);
        }
        EmitMatchTree(match, sw.Default, root, expected, scope, ctx, tail, result, end);
    }

    /// <summary>Read the constructor's name at the position and try each case against it.</summary>
    private void EmitDestruct(Term.Match match, DecisionTree.Destruct destruct, MatchValue root, Cty expected,
        Scope scope, Ctx ctx, bool tail, LocalBuilder? result, Label end)
    {
        var name = ctx.Il.DeclareLocal(typeof(string));
        Convert(EmitAt(destruct.At, root, ctx), Cty.O, ctx);
        ctx.Il.Emit(OpCodes.Call, MatchOpsMethod(nameof(MatchOps.ConstructorName)));
        ctx.Il.Emit(OpCodes.Stloc, name);

        foreach (var @case in destruct.Cases)
        {
            var next = ctx.Il.DefineLabel();
            ctx.Il.Emit(OpCodes.Ldloc, name);
            ctx.Il.Emit(OpCodes.Ldstr, @case.Name);
            ctx.Il.Emit(OpCodes.Call, StringEquals);
            ctx.Il.Emit(OpCodes.Brfalse, next);
            EmitMatchTree(match, @case.Tree, root, expected, scope, ctx, tail, result, end);
            ctx.Il.MarkLabel(next);
        }

        if (destruct.Default is { } fallback)
        {
            EmitMatchTree(match, fallback, root, expected, scope, ctx, tail, result, end);
            return;
        }
        // No default means the checker proved the cases exhaustive; reaching here is a
        // backend bug, so it throws rather than silently producing a value.
        ctx.Il.Emit(OpCodes.Ldstr, "a match reached a constructor no case covers");
        ctx.Il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
        ctx.Il.Emit(OpCodes.Throw);
    }

    /// <summary>The IL shape a binder at <paramref name="at"/> gets: the root's, or the object tier.</summary>
    private static Cty Loaded(Occurrence at, MatchValue root) => at is Occurrence.Base ? root.Ty : Cty.O;

    /// <summary>Leaves the value at an occurrence on the stack, and says its shape.</summary>
    private Cty EmitAt(Occurrence at, MatchValue root, Ctx ctx)
    {
        var parent = at switch
        {
            Occurrence.Child c => c.Parent,
            Occurrence.Payload p => p.Parent,
            Occurrence.Field f => f.Parent,
            _ => null,
        };
        if (parent is null)
        {
            ctx.Il.Emit(OpCodes.Ldloc, root.Slot);
            return root.Ty;
        }

        Convert(EmitAt(parent, root, ctx), Cty.O, ctx);
        switch (at)
        {
            case Occurrence.Child c:
                ctx.Il.Emit(OpCodes.Ldc_I4, c.Index);
                ctx.Il.Emit(OpCodes.Call, MatchOpsMethod(nameof(MatchOps.ProdItem)));
                break;
            case Occurrence.Payload p:
                ctx.Il.Emit(OpCodes.Ldc_I4, p.Index);
                ctx.Il.Emit(OpCodes.Call, MatchOpsMethod(nameof(MatchOps.Payload)));
                break;
            case Occurrence.Field f:
                ctx.Il.Emit(OpCodes.Ldstr, f.Name);
                ctx.Il.Emit(OpCodes.Call, MatchOpsMethod(nameof(MatchOps.Field)));
                break;
        }
        return Cty.O;
    }

    private static MethodInfo MatchOpsMethod(string name) => typeof(MatchOps).GetMethod(name)!;

    private static readonly MethodInfo StringEquals =
        typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(string)])!;

    private static readonly MethodInfo ObjectEquals =
        typeof(object).GetMethod(nameof(object.Equals), [typeof(object)])!;
}

/// <summary>
/// The object-tier operations the match walk emits calls to: nothing here is a
/// value representation, only how a value is read. The interpreter
/// is the reference (<c>Nbe.Match.cs</c>'s <c>At</c> and <c>SelectArm</c>).
/// </summary>
/// <remarks>
/// <b>What it reads is the <em>emitted</em> tier, not the interpreter's.</b> A constructor in
/// emitted code is <see cref="Runtime.Con"/> (name only), never <see cref="Quill.Kernel.Value.VCon"/>:
/// <c>Value</c> belongs to the evaluator and never appears in a running program's heap. Reading
/// <c>VCon</c> here built and passed xUnit and failed only at run time, on the cases that
/// actually reach a <c>Destruct</c>.
/// </remarks>
/// <remarks>
/// <b>Must stay <c>public</c>, like <see cref="Runtime"/> and for the same reason:</b> the
/// emitted program is a separate, dynamically-loaded assembly, so it cannot call an
/// <c>internal</c> member here. <c>internal</c> compiled and built fine and failed only at
/// run time, as a <c>MethodAccessException</c> inside emitted code — and only on the cases
/// that actually reach a <c>Destruct</c>, which is why it survived both forks alone and
/// appeared the moment a constructor value could be materialised.
/// </remarks>
public static class MatchOps
{
    /// <summary>The constructor's name, which is the tag a <c>Destruct</c> branches on.</summary>
    public static string ConstructorName(object value) => value switch
    {
        Runtime.Con con => con.Name,
        _ => throw new InvalidOperationException($"a match reached a {value?.GetType().Name ?? "null"} its type rules out"),
    };

    /// <summary>The payload at an index.</summary>
    public static object Payload(object value, int index) => value switch
    {
        Value.VCon con => con.Args[index],
        _ => throw new InvalidOperationException($"a match reached a {value?.GetType().Name ?? "null"} its type rules out"),
    };

    /// <summary>The element at an index of a tuple or tuple type.</summary>
    public static object ProdItem(object value, int index) => value switch
    {
        Value.VProd prod => prod.Items[index],
        Value.VProdTy prodTy => prodTy.Items[index],
        _ => throw new InvalidOperationException($"a match reached a {value?.GetType().Name ?? "null"} its type rules out"),
    };

    /// <summary>The last field with a name, as the interpreter reads a record.</summary>
    public static object Field(object value, string name) => value switch
    {
        Value.VRecord record => record.Fields.Last(field => field.Name == name).Value,
        _ => throw new InvalidOperationException($"a match reached a {value?.GetType().Name ?? "null"} its type rules out"),
    };
}
