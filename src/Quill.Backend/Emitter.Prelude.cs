using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using Quill.Kernel;

namespace Quill.Backend;

/// <summary>
/// The prelude half of the emitter: values reached through the ambient
/// environment, and closure conversion over the value graph.
///
/// A prelude function is not a primitive. <c>std/lib.qll:29</c> is
/// <c>pub (&lt;=) = fn(x, y) { … }</c>, which reaches the emitter as
/// <see cref="Value.VLam"/> over a <see cref="Closure"/>. A reference to one
/// compiles the closure's own <see cref="Closure.Body"/> in the closure's own
/// <see cref="Closure.Environment"/>: every closure reachable from the ambient was
/// evaluated over a prefix of the same context chain, so nothing has to be
/// captured and the generated method is static.
/// </summary>
internal sealed partial class Emitter
{
    /// <summary>The method a closure body compiled to, keyed by that closure's identity.</summary>
    // Reference equality, not `Closure`'s structural equality: two textually identical
    // lambdas are different functions, and Term records compare structurally.
    private readonly Dictionary<Closure, MethodBuilder> _closures = new(ReferenceEqualityComparer.Instance);

    private static readonly ConstructorInfo ConCtor = typeof(Runtime.Con).GetConstructor([typeof(string)])!;

    // ---- ambient values -----------------------------------------------------

    /// <summary>
    /// An ambient entry: an atom, a constructor, or a prelude lambda. A constructor
    /// becomes <see cref="Runtime.Con"/>, so <see cref="Runtime.Describe"/> renders
    /// it by name the way the interpreter's does.
    /// </summary>
    private Cty EmitAmbient(Value value, Cty expected, Ctx ctx) => value switch
    {
        Value.VAtom { Atom: Atom.I64 n } => EmitI64(n.Value, expected, ctx),
        Value.VCon con => EmitConValue(con.Name, expected, ctx),
        Value.VLam lam => EmitClosureValue(lam.Body, expected, ctx),
        _ => throw new NotImplementedException($"not ported yet: backend prelude value ({value.GetType().Name})"),
    };

    /// <summary>The one constructor shape this slice carries: no payloads, so the name is the whole value.</summary>
    private static Cty EmitCon(Term.Con con, Cty expected, Ctx ctx)
    {
        // shortcut: a payload-carrying constructor needs its arguments in the emitted
        // value, which is 1c's general nominal tier (`Value.VCon.Args`).
        if (con.Args.Length != 0)
            throw new NotImplementedException("not ported yet: backend constructor payload");
        return EmitConValue(con.Name, expected, ctx);
    }

    private static Cty EmitConValue(string name, Cty expected, Ctx ctx)
    {
        ctx.Il.Emit(OpCodes.Ldstr, name);
        ctx.Il.Emit(OpCodes.Newobj, ConCtor);
        return Convert(Cty.O, expected, ctx);
    }

    /// <summary>A prelude lambda used as a value: a delegate over its generated method.</summary>
    private Cty EmitClosureValue(Closure closure, Cty expected, Ctx ctx)
    {
        // shortcut: a one-parameter prelude function is the only delegate shape here;
        // a longer one used as a value is a partial application, which needs a closure
        // object, and that is 1c.
        if (Arity(closure) != 1)
            throw new NotImplementedException($"not ported yet: backend prelude value (arity {Arity(closure)} as a value)");
        ctx.Il.Emit(OpCodes.Ldnull);
        ctx.Il.Emit(OpCodes.Ldftn, EnsureClosureMethod(closure));
        ctx.Il.Emit(OpCodes.Newobj, DelegateCtor(Cty.ObjFn));
        return Convert(Cty.ObjFn, expected, ctx);
    }

    // ---- closure conversion -------------------------------------------------

    /// <summary>
    /// A <see cref="Value.VLam"/>'s <see cref="Closure.Body"/> has already had its own
    /// outermost <c>Lam</c> stripped by the evaluator, so the closure carries one
    /// parameter implicitly and every further leading <c>Lam</c> in that body is one
    /// more. This is how many parameters the emitted method takes.
    /// </summary>
    private static int Arity(Closure closure)
    {
        var arity = 1;
        for (var t = closure.Body; t is Term.Lam lam; t = lam.Body) arity++;
        return arity;
    }

    /// <summary>
    /// The whole spine of a call whose head is a prelude lambda of matching arity:
    /// the closure to compile and the arguments binding its parameter chain. Null
    /// when the head is not one, so the caller keeps its existing paths.
    /// </summary>
    private static (Closure Closure, ImmutableList<Term> Args)? ClosureSpine(Term term, Scope scope)
    {
        var args = ImmutableList<Term>.Empty;
        var head = term;
        while (head is Term.Ap ap)
        {
            // Only the explicit chain: an implicit argument's term is not one of the
            // method's parameters, and implicit application is 1e.
            if (ap.Explicitness != Explicitness.Explicit) return null;
            args = args.Insert(0, ap.Arg);
            head = ap.Fn;
        }
        if (head is not Term.Var v || v.Index < scope.Locals.Count) return null;
        if (scope.Ambient[v.Index - scope.Locals.Count] is not Value.VLam lam) return null;
        return Arity(lam.Body) == args.Count ? (lam.Body, args) : null;
    }

    /// <summary>A call of a prelude lambda: a direct call to one flattened method.</summary>
    private Cty EmitClosureCall((Closure Closure, ImmutableList<Term> Args) call, Cty expected, Scope scope, Ctx ctx, bool tail)
    {
        var method = EnsureClosureMethod(call.Closure);
        // The parameters are object-tier, so this is the object tier's application.
        foreach (var arg in call.Args) Emit(arg, Cty.O, scope, ctx, tail: false);
        if (tail && ctx.Ret == Cty.O)
        {
            ctx.Il.Emit(OpCodes.Tailcall);
            ctx.Il.Emit(OpCodes.Call, method);
            return ctx.Ret;
        }
        ctx.Il.Emit(OpCodes.Call, method);
        return Convert(Cty.O, expected, ctx);
    }

    /// <summary>
    /// Emits — or returns the already emitted — method for a closure: its whole
    /// parameter chain as one object-tier static method over the closure's own
    /// environment. Flattening the chain is what lets an inner binder reach the
    /// parameters above it, since a generated method cannot read another's locals.
    /// </summary>
    private MethodBuilder EnsureClosureMethod(Closure closure)
    {
        if (_closures.TryGetValue(closure, out var existing)) return existing;
        var arity = Arity(closure);
        var method = _type.DefineMethod($"Fn_{_nameSeq++}", MethodAttributes.Private | MethodAttributes.Static,
            typeof(object), [.. Enumerable.Repeat(typeof(object), arity)]);
        // Cached before the body, so a prelude function that calls itself or a
        // sibling later in the same group finds the method already defined.
        _closures[closure] = method;

        var ctx = new Ctx(method.GetILGenerator(), _methodSeq++, Cty.O);
        var scope = new Scope(closure.Environment);
        for (var i = 0; i < arity; i++) scope = scope.Push(Parameter(i, ctx));
        // The leading Lams are stripped, not emitted: the loop above already binds one
        // parameter per Lam, so emitting them again binds each one twice, and that extra
        // local shifts every ambient index in the body by one.
        var produced = Emit(LamChain(closure.Body).Body, Cty.O, scope, ctx, tail: true);
        Convert(produced, Cty.O, ctx);
        ctx.Il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>An object-tier parameter as a local of the method being emitted.</summary>
    private static Binding Parameter(int index, Ctx ctx)
    {
        if (index <= 3)
            ctx.Il.Emit(index switch { 0 => OpCodes.Ldarg_0, 1 => OpCodes.Ldarg_1, 2 => OpCodes.Ldarg_2, _ => OpCodes.Ldarg_3 });
        else
            ctx.Il.Emit(OpCodes.Ldarg, (short)index);
        var local = ctx.Il.DeclareLocal(typeof(object));
        ctx.Il.Emit(OpCodes.Stloc, local);
        return new Binding.Local(ctx.Id, ctx.AddLocal(local), Cty.O);
    }

    /// <summary>A term's leading <c>Lam</c>s, outermost first, and the body under all of them.</summary>
    private static (ImmutableList<Term> Lams, Term Body) LamChain(Term body)
    {
        var lams = ImmutableList<Term>.Empty;
        var term = body;
        while (term is Term.Lam lam)
        {
            lams = lams.Add(lam);
            term = lam.Body;
        }
        return (lams, term);
    }
}
