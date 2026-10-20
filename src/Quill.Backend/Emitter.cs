using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using Quill.Compiler;
using Quill.Kernel;

namespace Quill.Backend;

/// <summary>
/// <see cref="Term"/> to IL, built with <see cref="ILGenerator"/> and saved with
/// <see cref="PersistedAssemblyBuilder"/>. Slices 1a′/1b/1c-i emit atoms, primitives,
/// <c>Var</c>/<c>Let</c>/<c>Lam</c>/<c>Ap</c>/<c>Fix</c>, the whole <c>Match</c> walk except the
/// type-case trees, the prelude value graph, and the nominal tier (<c>Nominal</c>/<c>Con</c>/
/// <c>Open</c>/<c>Dot</c> over a nominal). Everything else is refused with a
/// <see cref="NotImplementedException"/> naming the shape: an unported path must never satisfy a
/// case expecting an error. Which shapes those are, measured, is in
/// <c>docs/wayfinder/topics/dotnet-backend.md</c>'s staging table.
/// </summary>
/// <remarks>
/// The emitter compiles a <em>(term, value-environment)</em> pair: a de Bruijn
/// index either names a binder the emitter introduced or an entry of the ambient
/// environment the program runs in -- the base context, pushed to by the prelude's
/// <c>open</c>. Every quill function is unary, so a function value is a
/// <c>Func&lt;param, ret&gt;</c> delegate over a generated static method, and a
/// call is a direct <c>call</c> when the callee is a known method. A tail call is
/// written with the <c>tail.</c> prefix, which is the reason this emitter exists.
/// </remarks>
internal sealed partial class Emitter
{
    /// <summary>An IL shape: <c>I64</c> is <c>long</c>, <c>Obj</c> the untyped fallback, <c>Fn</c> a delegate.</summary>
    private abstract record Cty
    {
        public sealed record I64 : Cty;
        public sealed record Obj : Cty;
        public sealed record Fn(Cty Param, Cty Ret) : Cty;

        public static readonly Cty L = new I64();
        public static readonly Cty O = new Obj();
        public static readonly Fn ObjFn = new(new Obj(), new Obj());

        public Type Clr() => this switch
        {
            I64 => typeof(long),
            Obj => typeof(object),
            Fn f => typeof(Func<,>).MakeGenericType(f.Param.Clr(), f.Ret.Clr()),
            _ => throw new InvalidOperationException(),
        };
    }

    /// <summary>What a binder in scope names: an IL local, a generated static method, or a type.</summary>
    private abstract record Binding(Cty Ty)
    {
        public sealed record Local(int Owner, int Slot, Cty Ty) : Binding(Ty);
        public sealed record MethodRef(MethodBuilder Method, Cty Ty) : Binding(Ty);

        /// <summary>
        /// A nominal type the emitter knows statically (<see cref="Decl"/>), so its
        /// constructors are read off the declaration instead of off a value.
        /// <see cref="Former"/> is how a <em>former</em> was bound -- `enum` under a lambda
        /// chain -- and null for a bare declaration, which emits nothing until a value is
        /// demanded.
        /// </summary>
        public sealed record Nominal(NominalDecl Decl, Cty Ty, MethodRef? Former) : Binding(Ty);

        /// <summary>One constructor of a nominal, as `open` binds it.</summary>
        public sealed record Ctor(NominalDecl Decl, ConstructorDecl Constructor) : Binding(Cty.O);
    }

    /// <summary>The binders in scope, innermost first, over the ambient value environment.</summary>
    private sealed class Scope(Environment ambient)
    {
        public Environment Ambient { get; } = ambient;
        public ImmutableList<Binding> Locals { get; init; } = [];

        public Scope Push(Binding b) => new(Ambient) { Locals = Locals.Insert(0, b) };
    }

    /// <summary>One emitted method's state: its generator, its locals, and its return shape.</summary>
    private sealed class Ctx(ILGenerator il, int id, Cty ret)
    {
        public ILGenerator Il { get; } = il;
        public int Id { get; } = id;
        public Cty Ret { get; } = ret;
        public List<LocalBuilder> Locals { get; } = [];

        public int AddLocal(LocalBuilder lb)
        {
            Locals.Add(lb);
            return Locals.Count - 1;
        }
    }

    private TypeBuilder _type = null!;
    private int _nameSeq;
    private int _methodSeq;
    private readonly Dictionary<Term, MethodBuilder> _lambdas = new();
    private readonly Dictionary<FixMember, MethodBuilder> _fixMembers = new();

    /// <summary>Elaborates nothing; emits <paramref name="program"/> and returns its entry point.</summary>
    public static Func<object> EmitProgram(Elaborated program)
    {
        var env = program.Context.Environment;
        var term = program.Term;
        // The program is elaborated inside `open (import "std")`; that open is the
        // prelude, not the program, so it becomes the ambient environment rather
        // than emitted code.
        while (term is Term.Open { Of: Term.Imported imported } open)
        {
            env = Nbe.PushOpenMembers(env, imported.Value, open.Members);
            term = open.Body;
        }
        return new Emitter().Build(term, ShapeOfValue(program.Type), env);
    }

    private Func<object> Build(Term term, Cty type, Environment ambient)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName("Quill.Generated"), typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("quill");
        _type = module.DefineType("Quill.Generated.Program",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);

        var entry = _type.DefineMethod("Entry", MethodAttributes.Public | MethodAttributes.Static,
            typeof(object), Type.EmptyTypes);
        var ctx = new Ctx(entry.GetILGenerator(), _methodSeq++, Cty.O);
        var produced = Emit(term, type, new Scope(ambient), ctx, tail: false);
        Convert(produced, Cty.O, ctx);
        ctx.Il.Emit(OpCodes.Ret);

        _type.CreateType();
        using var stream = new MemoryStream();
        assembly.Save(stream);
        var image = stream.ToArray();
        // Debugging hook: QUILL_BACKEND_IL writes the emitted image out to disassemble.
        if (System.Environment.GetEnvironmentVariable("QUILL_BACKEND_IL") is { Length: > 0 } path)
            File.WriteAllBytes(path, image);
        var loaded = Assembly.Load(image);
        var method = loaded.GetType("Quill.Generated.Program")!.GetMethod("Entry")!;
        return (Func<object>)method.CreateDelegate(typeof(Func<object>));
    }

    // ---- dispatch -----------------------------------------------------------

    private Cty Emit(Term term, Cty expected, Scope scope, Ctx ctx, bool tail) => term switch
    {
        Term.Atom atom => EmitAtom(atom, expected, ctx),
        // A bare prelude function used as a value: a delegate over its closure's method.
        Term.Var { } v when v.Index >= scope.Locals.Count
            && scope.Ambient[v.Index - scope.Locals.Count] is Value.VLam lam => EmitClosureValue(lam.Body, expected, ctx),
        Term.Var v => EmitVar(v.Index, expected, scope, ctx),
        Term.Ap ap when PrimitiveSpine(ap, scope) is { } spine => EmitPrimitive(spine, expected, scope, ctx),
        // A saturated constructor: the payloads become the value's argument array.
        Term.Ap ap when CtorSpine(ap, scope) is { } call => EmitCtorCall(call, expected, scope, ctx),
        // A saturated call of a prelude function: the spine's head is a `VLam` in the
        // value graph, and the arguments bind its whole unary chain (Emitter.Prelude.cs).
        Term.Ap ap when ClosureSpine(ap, scope) is { } call => EmitClosureCall(call, expected, scope, ctx, tail),
        Term.Ap ap => EmitApply(ap, expected, scope, ctx, tail),
        Term.Con con => EmitConTerm(con, expected, scope, ctx),
        Term.Nominal nominal => EmitNominalValue(nominal.Decl, expected, ctx),
        Term.AtomTy atomTy => EmitAtomTypeValue(atomTy.Ty, expected, ctx),
        Term.Let let => EmitLet(let, expected, scope, ctx, tail),
        Term.Lam lam => EmitLam(lam, expected, scope, ctx),
        Term.Open open => EmitOpen(open, expected, scope, ctx, tail),
        Term.Dot dot => EmitDot(dot, expected, scope, ctx),
        Term.Match match => EmitMatch(match, expected, scope, ctx, tail),
        _ => throw new NotImplementedException($"not ported yet: backend {term.GetType().Name} :: {term}"),
    };

    private static Cty EmitAtom(Term.Atom atom, Cty expected, Ctx ctx)
    {
        if (atom.Value is not Atom.I64 n)
            throw new NotImplementedException($"not ported yet: backend atom {atom.Value.GetType().Name}");
        ctx.Il.Emit(OpCodes.Ldc_I8, n.Value);
        return Convert(Cty.L, expected, ctx);
    }

    private Cty EmitVar(int index, Cty expected, Scope scope, Ctx ctx)
    {
        if (index < scope.Locals.Count)
        {
            switch (scope.Locals[index])
            {
                case Binding.Local local when local.Owner == ctx.Id:
                    ctx.Il.Emit(OpCodes.Ldloc, ctx.Locals[local.Slot]);
                    return Convert(local.Ty, expected, ctx);
                // A local of another method would need a real closure; that is 1b's job.
                case Binding.Local:
                    throw new NotImplementedException("not ported yet: backend captured local");
                case Binding.MethodRef method:
                    return EmitMethodValue(method, expected, ctx);
                case Binding.Nominal nominal when nominal.Former is null:
                    return EmitNominalValue(nominal.Decl, expected, ctx);
                case Binding.Nominal { Former: { } former }:
                    return EmitMethodValue(former, expected, ctx);
                case Binding.Ctor ctor:
                    return EmitCtorValue(ctor.Constructor, expected, ctx);
                default:
                    throw new InvalidOperationException();
            }
        }
        var value = scope.Ambient[index - scope.Locals.Count];
        return EmitAmbient(value, expected, ctx);
    }

    // let x : A = def in body. A function-shaped definition is a known method, so
    // its body calls it directly and only a use as a value builds a delegate.
    private Cty EmitLet(Term.Let let, Cty expected, Scope scope, Ctx ctx, bool tail)
    {
        var ty = ShapeOfTerm(let.Type);
        // An enum declaration binds a type, and the emitter knows which one: the
        // constructors are read off the declaration, so nothing is evaluated and no
        // local is needed. A use in a value position materialises it on demand.
        if (let.Def is Term.Nominal nominal)
            return Emit(let.Body, expected, scope.Push(new Binding.Nominal(nominal.Decl, Cty.O, null)), ctx, tail);
        if (let.Def is Term.Fix fix)
        {
            var method = EnsureFixMember(fix, fix.Index, ty, scope);
            var value = new Binding.MethodRef(method, ty);
            return Emit(let.Body, expected, scope.Push(FormerOf(value, fix.Members[fix.Index].Body)), ctx, tail);
        }
        if (let.Def is Term.Lam lam && ty is Cty.Fn fn)
        {
            var method = EnsureLambda(lam, fn, scope);
            var value = new Binding.MethodRef(method, ty);
            return Emit(let.Body, expected, scope.Push(FormerOf(value, lam.Body)), ctx, tail);
        }
        var local = ctx.Il.DeclareLocal(ty.Clr());
        var produced = Emit(let.Def, ty, scope, ctx, tail: false);
        Convert(produced, ty, ctx);
        ctx.Il.Emit(OpCodes.Stloc, local);
        return Emit(let.Body, expected, scope.Push(new Binding.Local(ctx.Id, ctx.AddLocal(local), ty)), ctx, tail);
    }

    private Cty EmitLam(Term.Lam lam, Cty expected, Scope scope, Ctx ctx)
    {
        // shortcut: an unannotated lambda falls back to an object-shaped delegate, because
        // Core.term carries no domain for Lam and no annotation table exists yet. Upgrade when
        // docs/wayfinder/tickets/elaborator-records-term-types.md lands, at which point every
        // lambda here has a written or recovered signature.
        var fn = expected as Cty.Fn ?? Cty.ObjFn;
        var method = EnsureLambda(lam, fn, scope);
        ctx.Il.Emit(OpCodes.Ldnull);
        ctx.Il.Emit(OpCodes.Ldftn, method);
        ctx.Il.Emit(OpCodes.Newobj, DelegateCtor(fn));
        return Convert(fn, expected, ctx);
    }

    private Cty EmitApply(Term.Ap ap, Cty expected, Scope scope, Ctx ctx, bool tail)
    {
        if (ap.Explicitness != Explicitness.Explicit)
            throw new NotImplementedException("not ported yet: backend implicit application");

        // A callee the emitter named: a direct call, tail-prefixed when in tail position.
        if (ap.Fn is Term.Var v && v.Index < scope.Locals.Count && scope.Locals[v.Index] is Binding.MethodRef mr)
        {
            if (mr.Ty is not Cty.Fn fn)
                throw new NotImplementedException("not ported yet: backend applied non-function");
            Emit(ap.Arg, fn.Param, scope, ctx, tail: false);
            return EmitCall(mr.Method, fn.Ret, expected, ctx, tail);
        }

        // A lambda literal applied at once: a direct call to its method, no delegate.
        if (ap.Fn is Term.Lam lam)
        {
            var param = Natural(ap.Arg, scope);
            var fn = new Cty.Fn(param, expected);
            var method = EnsureLambda(lam, fn, scope);
            Emit(ap.Arg, param, scope, ctx, tail: false);
            return EmitCall(method, fn.Ret, expected, ctx, tail);
        }

        var callee = Natural(ap.Fn, scope);
        if (callee is Cty.Fn shape)
        {
            Emit(ap.Fn, shape, scope, ctx, tail: false);
            Emit(ap.Arg, shape.Param, scope, ctx, tail: false);
            var invoke = shape.Clr().GetMethod("Invoke")!;
            if (tail && shape.Ret == ctx.Ret)
            {
                ctx.Il.Emit(OpCodes.Tailcall);
                ctx.Il.Emit(OpCodes.Callvirt, invoke);
                return ctx.Ret;
            }
            ctx.Il.Emit(OpCodes.Callvirt, invoke);
            return Convert(shape.Ret, expected, ctx);
        }
        if (callee is Cty.Obj)
        {
            Emit(ap.Fn, Cty.O, scope, ctx, tail: false);
            Emit(ap.Arg, Cty.O, scope, ctx, tail: false);
            ctx.Il.Emit(OpCodes.Call, typeof(Runtime).GetMethod(nameof(Runtime.Apply))!);
            return Convert(Cty.O, expected, ctx);
        }
        throw new NotImplementedException("not ported yet: backend applied non-function");
    }

    private Cty EmitCall(MethodBuilder method, Cty ret, Cty expected, Ctx ctx, bool tail)
    {
        // `tail.` must be followed immediately by `ret` and the caller's return
        // shape must match the callee's exactly; a conversion afterwards would
        // make the prefix illegal, so an inexact match is a plain call.
        if (tail && ret == ctx.Ret)
        {
            ctx.Il.Emit(OpCodes.Tailcall);
            ctx.Il.Emit(OpCodes.Call, method);
            return ctx.Ret;
        }
        ctx.Il.Emit(OpCodes.Call, method);
        return Convert(ret, expected, ctx);
    }

    // ---- primitives ---------------------------------------------------------

    private Cty EmitPrimitive((string Name, ImmutableList<Term> Args) spine, Cty expected, Scope scope, Ctx ctx)
    {
        var method = typeof(Runtime).GetMethod(RuntimeCall(spine.Name)
            ?? throw new NotImplementedException($"not ported yet: backend primitive {spine.Name}"))!;
        Emit(spine.Args[0], Cty.L, scope, ctx, tail: false);
        Emit(spine.Args[1], Cty.L, scope, ctx, tail: false);
        ctx.Il.Emit(OpCodes.Call, method);
        return Convert(Cty.L, expected, ctx);
    }

    private static (string Name, ImmutableList<Term> Args)? PrimitiveSpine(Term term, Scope scope)
    {
        var args = ImmutableList<Term>.Empty;
        var head = term;
        while (head is Term.Ap ap)
        {
            args = args.Insert(0, ap.Arg);
            head = ap.Fn;
        }
        if (PrimitiveName(head, scope) is not { } name) return null;
        // A partially applied primitive is a function value, which is not ported.
        return args.Count == 2 ? (name, args) : null;
    }

    private static string? PrimitiveName(Term head, Scope scope) => head switch
    {
        Term.Prim p => p.Name,
        Term.Var v when v.Index >= scope.Locals.Count
            && scope.Ambient[v.Index - scope.Locals.Count] is Value.VNeutral { Head: Head.HPrim h, Frames.Length: 0 } =>
            h.Name,
        _ => null,
    };

    private static string? RuntimeCall(string primitive) => primitive switch
    {
        "+" => nameof(Runtime.Add), "-" => nameof(Runtime.Sub), "*" => nameof(Runtime.Mul),
        "/" => nameof(Runtime.Div), "%" => nameof(Runtime.Mod),
        "eq_i64" => nameof(Runtime.Eq), "neq_i64" => nameof(Runtime.Ne),
        "lt_i64" => nameof(Runtime.Lt), "gt_i64" => nameof(Runtime.Gt),
        "le_i64" => nameof(Runtime.Le), "ge_i64" => nameof(Runtime.Ge),
        _ => null,
    };

    // ---- match --------------------------------------------------------------

    /// <summary>
    /// The entry the dispatch calls: evaluate the scrutinee once into a root
    /// local, then let <c>Emitter.Match.cs</c> walk the tree over it. An arm's
    /// result is written into one local the whole tree shares (a nested tree
    /// chooses, it does not return); in tail position each arm returns instead.
    /// </summary>
    private Cty EmitMatch(Term.Match match, Cty expected, Scope scope, Ctx ctx, bool tail)
    {
        var shape = Natural(match.Scrutinee, scope);
        var scrutinee = Emit(match.Scrutinee, shape, scope, ctx, tail: false);
        Convert(scrutinee, shape, ctx);
        var root = ctx.Il.DeclareLocal(shape.Clr());
        ctx.Il.Emit(OpCodes.Stloc, root);
        var at = new MatchValue(shape, root);

        if (tail)
        {
            EmitMatchTree(match, match.Tree, at, expected, scope, ctx, tail: true, result: null, end: default);
            return ctx.Ret;
        }

        var result = ctx.Il.DeclareLocal(expected.Clr());
        var end = ctx.Il.DefineLabel();
        EmitMatchTree(match, match.Tree, at, expected, scope, ctx, tail: false, result, end);
        ctx.Il.MarkLabel(end);
        ctx.Il.Emit(OpCodes.Ldloc, result);
        return expected;
    }

    private static long I64Key(Atom key) =>
        key is Atom.I64 n ? n.Value : throw new NotImplementedException($"not ported yet: backend switch key {key.GetType().Name}");

    // ---- functions ----------------------------------------------------------

    private MethodBuilder EnsureLambda(Term.Lam lam, Cty.Fn fn, Scope enclosing)
    {
        if (_lambdas.TryGetValue(lam, out var existing)) return existing;
        var method = DefineFnMethod(fn);
        _lambdas[lam] = method;
        var ctx = new Ctx(method.GetILGenerator(), _methodSeq++, fn.Ret);
        var parameter = LoadParameter(fn, ctx);
        var body = Emit(lam.Body, fn.Ret, enclosing.Push(parameter), ctx, tail: true);
        Convert(body, fn.Ret, ctx);
        ctx.Il.Emit(OpCodes.Ret);
        return method;
    }

    private MethodBuilder EnsureFixMember(Term.Fix fix, int index, Cty ty, Scope enclosing)
    {
        foreach (var member in fix.Members) EnsureMember(fix, member, ty, enclosing);
        return _fixMembers[fix.Members[index]];
    }

    private MethodBuilder EnsureMember(Term.Fix fix, FixMember member, Cty ty, Scope enclosing)
    {
        if (_fixMembers.TryGetValue(member, out var existing)) return existing;
        if (ty is not Cty.Fn fn || member.Body is not Term.Lam lam)
            throw new NotImplementedException("not ported yet: backend recursive value");
        var method = DefineFnMethod(fn);
        // Cache before emitting the body: a sibling's body refers back to this one.
        _fixMembers[member] = method;

        // Every body sees one entry per member, the first member outermost.
        var scope = enclosing;
        foreach (var sibling in fix.Members)
            scope = scope.Push(new Binding.MethodRef(EnsureMember(fix, sibling, ty, enclosing), ty));

        var ctx = new Ctx(method.GetILGenerator(), _methodSeq++, fn.Ret);
        var parameter = LoadParameter(fn, ctx);
        var body = Emit(lam.Body, fn.Ret, scope.Push(parameter), ctx, tail: true);
        Convert(body, fn.Ret, ctx);
        ctx.Il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// A function binding, and the declaration too when its body is an enum: a former --
    /// `F = fn(A) { enum { … } }` -- is a value and a declaration at once, so `F(I64)` needs
    /// the method and `open F` / `F(I64).C` need the declaration.
    /// </summary>
    private static Binding FormerOf(Binding.MethodRef value, Term body) =>
        LamChain(body).Body is Term.Nominal declared ? new Binding.Nominal(declared.Decl, value.Ty, value) : value;

    private static Binding LoadParameter(Cty.Fn fn, Ctx ctx)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0);
        var local = ctx.Il.DeclareLocal(fn.Param.Clr());
        ctx.Il.Emit(OpCodes.Stloc, local);
        return new Binding.Local(ctx.Id, ctx.AddLocal(local), fn.Param);
    }

    private Cty EmitMethodValue(Binding.MethodRef method, Cty expected, Ctx ctx)
    {
        if (method.Ty is not Cty.Fn fn)
            throw new NotImplementedException("not ported yet: backend non-function value");
        if (expected is Cty.Obj && fn != Cty.ObjFn)
            throw new NotImplementedException("not ported yet: backend untyped function value");
        ctx.Il.Emit(OpCodes.Ldnull);
        ctx.Il.Emit(OpCodes.Ldftn, method.Method);
        ctx.Il.Emit(OpCodes.Newobj, DelegateCtor(fn));
        return Convert(fn, expected, ctx);
    }

    private MethodBuilder DefineFnMethod(Cty.Fn fn) =>
        _type.DefineMethod($"Fn_{_nameSeq++}", MethodAttributes.Private | MethodAttributes.Static,
            fn.Ret.Clr(), [fn.Param.Clr()]);

    private static ConstructorInfo DelegateCtor(Cty.Fn fn) =>
        fn.Clr().GetConstructor([typeof(object), typeof(IntPtr)])!;

    // ---- shapes and conversions ---------------------------------------------

    private static Cty Natural(Term term, Scope scope) => term switch
    {
        Term.Atom { Value: Atom.I64 } => Cty.L,
        Term.Var v when v.Index < scope.Locals.Count => scope.Locals[v.Index].Ty,
        Term.Var v => scope.Ambient[v.Index - scope.Locals.Count] is Value.VAtom { Atom: Atom.I64 } ? Cty.L : Cty.O,
        Term.Ap ap when IsPrimitive(ap, scope) => Cty.L,
        Term.Ap ap => Natural(ap.Fn, scope) is Cty.Fn fn ? fn.Ret : Cty.O,
        Term.Let let => ShapeOfTerm(let.Type),
        _ => Cty.O,
    };

    private static bool IsPrimitive(Term term, Scope scope)
    {
        var head = term;
        while (head is Term.Ap ap) head = ap.Fn;
        return PrimitiveName(head, scope) is not null;
    }

    /// <summary>The IL shape a written type term denotes; <c>object</c> when it is not concrete.</summary>
    private static Cty ShapeOfTerm(Term type) => type switch
    {
        Term.AtomTy { Ty: AtomTy.I64 } => Cty.L,
        Term.Pi pi => new Cty.Fn(ShapeOfTerm(pi.Domain), ShapeOfTerm(pi.Codomain)),
        _ => Cty.O,
    };

    /// <summary>The IL shape a value type denotes; <c>object</c> when it is not concrete.</summary>
    private static Cty ShapeOfValue(Value type) => type switch
    {
        Value.VAtomTy { Ty: AtomTy.I64 } => Cty.L,
        Value.VPi pi => new Cty.Fn(ShapeOfValue(pi.Domain), ShapeOfTerm(pi.Codomain.Body)),
        _ => Cty.O,
    };

    private static Cty Convert(Cty from, Cty to, Ctx ctx)
    {
        if (from == to) return to;
        if (to is Cty.Obj)
        {
            if (from is Cty.I64) ctx.Il.Emit(OpCodes.Box, typeof(long));
            else if (from is Cty.Fn fn && fn != Cty.ObjFn)
                throw new NotImplementedException("not ported yet: backend untyped function value");
            return to;
        }
        if (from is Cty.Obj)
        {
            if (to is Cty.I64) ctx.Il.Emit(OpCodes.Unbox_Any, typeof(long));
            else if (to is Cty.Fn objFn && objFn == Cty.ObjFn) ctx.Il.Emit(OpCodes.Castclass, objFn.Clr());
            else throw new NotImplementedException($"not ported yet: backend conversion object -> {to.GetType().Name}");
            return to;
        }
        throw new NotImplementedException($"not ported yet: backend conversion {from.GetType().Name} -> {to.GetType().Name}");
    }
}
