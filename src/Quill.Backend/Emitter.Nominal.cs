using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using Quill.Kernel;

namespace Quill.Backend;

/// <summary>
/// The nominal tier: an enum declaration, its constructors, and the type as a value.
///
/// A declaration is <see cref="Term.Nominal"/>, and the emitter keeps the
/// <see cref="NominalDecl"/> in scope rather than a value -- the constructors come off
/// the declaration, so nothing has to be evaluated to name one. A constructor value is
/// <see cref="Runtime.Con"/>: its name (the tag a <c>Destruct</c> branches on) and its
/// payloads, which are the arguments of the <see cref="Term.Con"/> at the end of the
/// lambda chain a payload-carrying constructor arrives as (<c>Nbe.Enum.cs</c>).
/// </summary>
internal sealed partial class Emitter
{
    private static readonly ConstructorInfo NominalCtor = typeof(Runtime.Nominal).GetConstructor([typeof(string)])!;

    private static readonly ConstructorInfo AtomTypeCtor = typeof(Runtime.AtomType).GetConstructor([typeof(string)])!;

    // ---- a type as a value --------------------------------------------------

    /// <summary>
    /// A nominal used where a value is demanded. The captures are dropped: nothing in
    /// emitted code reads them yet (type-case does; that is 1e), and dropping them keeps
    /// the value a constant the emitter can build without evaluating anything.
    /// </summary>
    private static Cty EmitNominalValue(NominalDecl decl, Cty expected, Ctx ctx)
    {
        ctx.Il.Emit(OpCodes.Ldstr, decl.Name);
        ctx.Il.Emit(OpCodes.Newobj, NominalCtor);
        return Convert(Cty.O, expected, ctx);
    }

    private static Cty EmitAtomTypeValue(AtomTy ty, Cty expected, Ctx ctx)
    {
        ctx.Il.Emit(OpCodes.Ldstr, ty.ToString());
        ctx.Il.Emit(OpCodes.Newobj, AtomTypeCtor);
        return Convert(Cty.O, expected, ctx);
    }

    // ---- constructors -------------------------------------------------------

    /// <summary>The nominal a term denotes, so its constructors can be read off it.</summary>
    private static NominalDecl? NominalOf(Term term, Scope scope) => term switch
    {
        Term.Nominal nominal => nominal.Decl,
        Term.Var v when v.Index < scope.Locals.Count => scope.Locals[v.Index] is Binding.Nominal n ? n.Decl : null,
        Term.Var v => AmbientNominal(scope.Ambient[v.Index - scope.Locals.Count]),
        // A type former applied to its captures, or the lambda a former was written as:
        // `Option(I64)` and `fn(A) { enum { … } }` both denote their declaration.
        Term.Ap ap => NominalOf(ap.Fn, scope),
        Term.Lam lam => NominalOf(lam.Body, scope),
        _ => null,
    };

    private static NominalDecl? AmbientNominal(Value value) => value switch
    {
        Value.VNominal nominal => nominal.Decl,
        // A former is a function whose body is the declaration it builds (`Option`, `List`).
        Value.VLam { Body.Body: Term.Nominal nominal } => nominal.Decl,
        _ => null,
    };

    /// <summary>The whole spine of a saturated constructor call, or null when the head is not one.</summary>
    private static (ConstructorDecl Constructor, ImmutableList<Term> Args)? CtorSpine(Term term, Scope scope)
    {
        var head = term;
        while (head is Term.Ap ap) head = ap.Fn;
        if (CtorOf(head, scope) is not { } constructor) return null;
        // Only a constructor's implicit arguments are skipped, and they are exactly its
        // type parameters: a former's `open` binds `fn(A) { T(A).C }`, so the erased `A`
        // is what the use site passes implicitly. Payloads are the explicit ones.
        var args = ImmutableList<Term>.Empty;
        for (var t = term; t is Term.Ap ap; t = ap.Fn)
            if (ap.Explicitness == Explicitness.Explicit)
                args = args.Insert(0, ap.Arg);
        return constructor.Payloads.Length == args.Count ? (constructor, args) : null;
    }

    private static ConstructorDecl? CtorOf(Term head, Scope scope) => head switch
    {
        Term.Var v when v.Index < scope.Locals.Count => (scope.Locals[v.Index] as Binding.Ctor)?.Constructor,
        Term.Var v => AmbientCtor(scope.Ambient[v.Index - scope.Locals.Count]),
        Term.Dot dot when NominalOf(dot.Of, scope) is { } decl => decl.Constructor(dot.Name),
        // A former written out where it is used: `Lam(Dot(former(A), C))`.
        Term.Lam lam => CtorOf(lam.Body, scope),
        _ => null,
    };

    /// <summary>
    /// A constructor an ambient value denotes. A prelude constructor of a type former is
    /// <c>fn(A) { T(A).C }</c> (<c>Nbe.OpenedConstructor</c>): one erased lambda, then a
    /// <c>Dot</c> over the applied declaration.
    /// </summary>
    private static ConstructorDecl? AmbientCtor(Value value) => value switch
    {
        // `Of` must be an application: the shape is a former applied to its parameter, not
        // a field read off a lambda's parameter (which would resolve in the wrong env).
        Value.VLam { Body: var closure } when LamChain(closure.Body).Body is Term.Dot { Of: Term.Ap } dot
            && NominalIn(closure.Environment, dot.Of) is { } decl => decl.Constructor(dot.Name),
        _ => null,
    };

    /// <summary>The nominal a term denotes inside a closure's own environment.</summary>
    private static NominalDecl? NominalIn(Environment env, Term term) => term switch
    {
        Term.Nominal nominal => nominal.Decl,
        Term.Var v => AmbientNominal(env[v.Index]),
        Term.Ap ap => NominalIn(env, ap.Fn),
        Term.Lam lam => NominalIn(env, lam.Body),
        _ => null,
    };

    private Cty EmitCtorCall((ConstructorDecl Constructor, ImmutableList<Term> Args) call,
        Cty expected, Scope scope, Ctx ctx)
    {
        EmitCon(call.Constructor.Name, call.Args.Count, i => Emit(call.Args[i], Cty.O, scope, ctx, tail: false), ctx);
        return Convert(Cty.O, expected, ctx);
    }

    /// <summary>
    /// A constructor used as a value rather than applied. A nullary one is the value; a
    /// payload-carrying one is the curried function, which needs a closure object for the
    /// payloads collected so far, so it is refused until a program demands one.
    /// </summary>
    private Cty EmitCtorValue(ConstructorDecl constructor, Cty expected, Ctx ctx)
    {
        if (!constructor.Payloads.IsEmpty)
            throw new NotImplementedException(
                $"not ported yet: backend constructor `{constructor.Name}` as a value");
        EmitCon(constructor.Name, 0, _ => { }, ctx);
        return Convert(Cty.O, expected, ctx);
    }

    /// <summary>The end of a constructor's lambda chain: the value its payloads built.</summary>
    private Cty EmitConTerm(Term.Con con, Cty expected, Scope scope, Ctx ctx)
    {
        EmitCon(con.Name, con.Args.Length, i => Emit(con.Args[i], Cty.O, scope, ctx, tail: false), ctx);
        return Convert(Cty.O, expected, ctx);
    }

    /// <summary>Leaves one <see cref="Runtime.Con"/> on the stack: its name, then its payloads.</summary>
    private static void EmitCon(string name, int arity, Action<int> emitArg, Ctx ctx)
    {
        ctx.Il.Emit(OpCodes.Ldstr, name);
        var args = ctx.Il.DeclareLocal(typeof(object[]));
        ctx.Il.Emit(OpCodes.Ldc_I4, arity);
        ctx.Il.Emit(OpCodes.Newarr, typeof(object));
        ctx.Il.Emit(OpCodes.Stloc, args);
        for (var i = 0; i < arity; i++)
        {
            ctx.Il.Emit(OpCodes.Ldloc, args);
            ctx.Il.Emit(OpCodes.Ldc_I4, i);
            emitArg(i);
            ctx.Il.Emit(OpCodes.Stelem_Ref);
        }
        ctx.Il.Emit(OpCodes.Ldloc, args);
        ctx.Il.Emit(OpCodes.Newobj, ConCtor);
    }
}
