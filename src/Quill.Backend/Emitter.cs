using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using Quill.Compiler;
using Quill.Kernel;

namespace Quill.Backend;

/// <summary>
/// <see cref="Term"/> to IL, built with <see cref="ILGenerator"/> and saved with
/// <see cref="PersistedAssemblyBuilder"/>. Slices 1a′/1b/1c-i/1c-ii emit atoms, primitives,
/// <c>Var</c>/<c>Let</c>/<c>Lam</c>/<c>Ap</c>/<c>Fix</c>, the whole <c>Match</c> walk except the
/// type-case trees, the prelude value graph, the nominal tier (<c>Nominal</c>/<c>Con</c>/
/// <c>Open</c>/<c>Dot</c> over a nominal), and the structural tier — a module, a struct type, a
/// record, a tuple, <c>open</c> and <c>Dot</c> over a value, <c>Imported</c>
/// (<c>Emitter.Structs.cs</c>). Everything else is refused with a
/// <see cref="NotImplementedException"/> naming the shape: an unported path must never satisfy a
/// case expecting an error. Which shapes those are, measured, is in
/// <c>docs/wayfinder/topics/dotnet-backend.md</c>'s staging table.
/// </summary>
/// <remarks>
/// The emitter compiles a <em>(term, value-environment)</em> pair: a de Bruijn
/// index either names a binder the emitter introduced, a value captured into the current method's
/// closure, or an entry of the ambient environment the program runs in -- the base context, pushed
/// to by the prelude's <c>open</c>. Every quill function is unary, so a function value is a
/// <c>Func&lt;param, ret&gt;</c> delegate over a generated method, and a call is a direct
/// <c>call</c> when the callee is a known method. A lambda that reads an enclosing method's local
/// cannot be static: it becomes an instance method of a generated class holding one
/// <c>object[]</c> of captured values, and the delegate is built over that instance. A tail call is
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

    /// <summary>What a binder in scope names: an IL local, a captured value, a generated static method, or a type.</summary>
    private abstract record Binding(Cty Ty)
    {
        public sealed record Local(int Owner, int Slot, Cty Ty) : Binding(Ty);

        /// <summary>A value captured into the current method's closure environment, at <paramref name="Slot"/>.</summary>
        public sealed record Env(int Owner, int Slot, Cty Ty) : Binding(Ty);

        /// <summary>A member read off a container value by label: what an <c>open</c> pushes.</summary>
        public sealed record Member(Local Container, string Name) : Binding(Cty.O);

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

        /// <summary>The closure object's environment field, when this method is a capturing lambda's body.</summary>
        public FieldBuilder? Env { get; init; }

        public int AddLocal(LocalBuilder lb)
        {
            Locals.Add(lb);
            return Locals.Count - 1;
        }
    }

    /// <summary>
    /// A lambda's generated method, and the values it captures. When
    /// <see cref="Closure"/> is null the method is static and nothing travels with the
    /// delegate; otherwise it is an instance method of a generated class holding one
    /// <c>object[]</c> of captured values.
    /// </summary>
    private sealed record LambdaInfo(MethodBuilder Method, List<(int Index, Binding Original)> Captures, TypeBuilder? Closure, ConstructorBuilder? Ctor);

    /// <summary>The identity <see cref="LambdaInfo"/> is cached under: the lambda itself, by reference.</summary>
    private sealed class LambdaKey : IEqualityComparer<(Term Lam, Cty Fn)>
    {
        public static readonly LambdaKey Comparer = new();

        public bool Equals((Term Lam, Cty Fn) a, (Term Lam, Cty Fn) b) =>
            ReferenceEquals(a.Lam, b.Lam) && a.Fn == b.Fn;

        public int GetHashCode((Term Lam, Cty Fn) key) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Lam), key.Fn);
    }

    private ModuleBuilder _module = null!;
    private TypeBuilder _type = null!;
    private int _nameSeq;
    private int _methodSeq;
    // Keyed by the lambda by *reference* -- two textually identical lambdas are different
    // functions and capture different scopes, while Term records compare structurally -- and by
    // the shape it is emitted at, because the same lambda used at two shapes is two methods.
    private readonly Dictionary<(Term Lam, Cty Fn), LambdaInfo> _lambdas = new(LambdaKey.Comparer);
    private readonly Dictionary<FixMember, MethodBuilder> _fixMembers = new();
    private readonly List<TypeBuilder> _closuresToCreate = new();

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
        _module = assembly.DefineDynamicModule("quill");
        _type = _module.DefineType("Quill.Generated.Program",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);

        var entry = _type.DefineMethod("Entry", MethodAttributes.Public | MethodAttributes.Static,
            typeof(object), Type.EmptyTypes);
        var ctx = new Ctx(entry.GetILGenerator(), _methodSeq++, Cty.O);
        var produced = Emit(term, type, new Scope(ambient), ctx, tail: false);
        Convert(produced, Cty.O, ctx);
        ctx.Il.Emit(OpCodes.Ret);

        // Closure classes first: a generated method's IL refers to one before the enclosing
        // type exists, so every type is created only once all bodies are complete.
        foreach (var closure in _closuresToCreate) closure.CreateType();
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
        // A spliced unit's value already carries its own environment (I5); it is the value
        // that crosses, so there is nothing to emit for the splice itself.
        Term.Imported imported => EmitAmbient(imported.Value, expected, ctx),
        Term.Struct st => EmitStructType(st, expected, scope, ctx),
        Term.RecordConstruct record => EmitRecordConstruct(record, expected, scope, ctx),
        Term.Module module => EmitModule(module, expected, scope, ctx),
        Term.Prod prod => EmitProd(prod, expected, scope, ctx),
        Term.ProdTy prodTy => EmitProdTy(prodTy, expected, scope, ctx),
        Term.Proj proj => EmitProj(proj, expected, scope, ctx),
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
        if (atom.Value is Atom.I64 n)
        {
            ctx.Il.Emit(OpCodes.Ldc_I8, n.Value);
            return Convert(Cty.L, expected, ctx);
        }
        return Convert(EmitAtomText(atom.Value.ToString(), ctx), expected, ctx);
    }

    /// <summary>
    /// A non-I64 atom as a value. Its own text is all anything in emitted code can read off
    /// it, and it is enough: a `#stamp` module's Unit is the loudest, and no primitive applies.
    /// </summary>
    private static Cty EmitAtomText(string text, Ctx ctx)
    {
        ctx.Il.Emit(OpCodes.Ldstr, text);
        ctx.Il.Emit(OpCodes.Newobj, AtomCtor);
        return Cty.O;
    }

    private Cty EmitVar(int index, Cty expected, Scope scope, Ctx ctx)
    {
        if (index < scope.Locals.Count)
            return LoadBinding(scope.Locals[index], expected, ctx);
        var value = scope.Ambient[index - scope.Locals.Count];
        return EmitAmbient(value, expected, ctx);
    }

    /// <summary>Leaves a local of this method on the stack.</summary>
    private static Cty LocalValue(Ctx ctx, Binding.Local local)
    {
        ctx.Il.Emit(OpCodes.Ldloc, ctx.Locals[local.Slot]);
        return local.Ty;
    }

    /// <summary>Leaves what a binder names on the stack.</summary>
    private Cty LoadBinding(Binding binding, Cty expected, Ctx ctx) => binding switch
    {
        Binding.Local local when local.Owner == ctx.Id =>
            Convert(LocalValue(ctx, local), expected, ctx),
        Binding.Env env when env.Owner == ctx.Id =>
            Convert(Exact(LoadEnviron(ctx, env.Slot), env.Ty, ctx), expected, ctx),
        Binding.Member member =>
            Convert(LoadMember(member, ctx), expected, ctx),
        Binding.MethodRef method => EmitMethodValue(method, expected, ctx),
        Binding.Nominal nominal when nominal.Former is null => EmitNominalValue(nominal.Decl, expected, ctx),
        Binding.Nominal { Former: { } former } => EmitMethodValue(former, expected, ctx),
        Binding.Ctor ctor => EmitCtorValue(ctor.Constructor, expected, ctx),
        // A value of another method that the closure conversion did not thread is an emitter
        // bug, not an unported shape: it must be loud, never a silent wrong answer.
        Binding.Local or Binding.Env => throw new InvalidOperationException("a captured local was not threaded into the closure"),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// A captured value back at the shape it was captured at. The cast is exact by
    /// construction -- <see cref="EmitCaptures"/> boxed a value of this very shape -- where
    /// <see cref="Convert"/>'s object-to-delegate path cannot be: the object tier keeps no
    /// shape for a delegate.
    /// </summary>
    private static Cty Exact(Cty from, Cty to, Ctx ctx)
    {
        if (from == to) return to;
        ctx.Il.Emit(from is Cty.Obj && to is Cty.I64 ? OpCodes.Unbox_Any : OpCodes.Castclass, to.Clr());
        return to;
    }

    /// <summary>One captured value out of the current closure object's environment, boxed.</summary>
    private static Cty LoadEnviron(Ctx ctx, int slot)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0);
        ctx.Il.Emit(OpCodes.Ldfld, ctx.Env!);
        ctx.Il.Emit(OpCodes.Ldc_I4, slot);
        ctx.Il.Emit(OpCodes.Ldelem_Ref);
        return Cty.O;
    }

    private static Cty LoadMember(Binding.Member member, Ctx ctx)
    {
        LocalValue(ctx, member.Container);
        ctx.Il.Emit(OpCodes.Ldstr, member.Name);
        ctx.Il.Emit(OpCodes.Call, RuntimeMember);
        return Cty.O;
    }

    // let x : A = def in body. A function-shaped definition is a known method, so
    // its body calls it directly and only a use as a value builds a delegate.
    private Cty EmitLet(Term.Let let, Cty expected, Scope scope, Ctx ctx, bool tail) =>
        Emit(let.Body, expected, scope.Push(Bind(let.Type, let.Def, scope, ctx)), ctx, tail);

    /// <summary>Emits a definition and says what its binder names.</summary>
    private Binding Bind(Term type, Term def, Scope scope, Ctx ctx)
    {
        var ty = ShapeOfTerm(type);
        // An enum declaration binds a type, and the emitter knows which one: the
        // constructors are read off the declaration, so nothing is evaluated and no
        // local is needed. A use in a value position materialises it on demand.
        if (def is Term.Nominal nominal)
            return new Binding.Nominal(nominal.Decl, Cty.O, null);
        if (def is Term.Fix fix)
            return FormerOf(new Binding.MethodRef(EnsureFixMember(fix, fix.Index, ty, scope), ty), fix.Members[fix.Index].Body);
        if (def is Term.Lam lam && ty is Cty.Fn fn)
        {
            var info = EnsureLambda(lam, fn, scope);
            if (info.Closure is null)
                return FormerOf(new Binding.MethodRef(info.Method, ty), lam.Body);
        }
        return BindLocal(def, ty, scope, ctx);
    }

    /// <summary>Evaluates <paramref name="def"/> into a local of <paramref name="ty"/>.</summary>
    private Binding.Local BindLocal(Term def, Cty ty, Scope scope, Ctx ctx)
    {
        var local = ctx.Il.DeclareLocal(ty.Clr());
        Convert(Emit(def, ty, scope, ctx, tail: false), ty, ctx);
        ctx.Il.Emit(OpCodes.Stloc, local);
        return new Binding.Local(ctx.Id, ctx.AddLocal(local), ty);
    }

    private Cty EmitLam(Term.Lam lam, Cty expected, Scope scope, Ctx ctx)
    {
        // shortcut: an unannotated lambda falls back to an object-shaped delegate, because
        // Core.term carries no domain for Lam and no annotation table exists yet. Upgrade when
        // docs/wayfinder/tickets/elaborator-records-term-types.md lands, at which point every
        // lambda here has a written or recovered signature.
        var fn = expected as Cty.Fn ?? Cty.ObjFn;
        return EmitLambdaValue(lam, fn, scope, ctx, expected);
    }

    /// <summary>
    /// A lambda used as a value: a delegate over its generated method. A capturing lambda's
    /// method is an instance method, so its closure object is built here from the values it
    /// captures out of this method's locals and environment.
    /// </summary>
    private Cty EmitLambdaValue(Term.Lam lam, Cty.Fn fn, Scope scope, Ctx ctx, Cty expected)
    {
        var info = EnsureLambda(lam, fn, scope);
        if (info.Closure is null)
            ctx.Il.Emit(OpCodes.Ldnull);
        else
        {
            EmitCaptures(info.Captures, ctx);
            ctx.Il.Emit(OpCodes.Newobj, info.Ctor!);
            // No `dup`: `ldftn` needs the instance on the stack but does not consume it --
            // it is the delegate constructor's first argument (Roslyn's shape for `o.M`).
        }
        ctx.Il.Emit(OpCodes.Ldftn, info.Method);
        ctx.Il.Emit(OpCodes.Newobj, DelegateCtor(fn));
        return Convert(fn, expected, ctx);
    }

    /// <summary>One <c>object[]</c> of the values a lambda captures, in capture order.</summary>
    private void EmitCaptures(List<(int Index, Binding Original)> captures, Ctx ctx)
    {
        ctx.Il.Emit(OpCodes.Ldc_I4, captures.Count);
        ctx.Il.Emit(OpCodes.Newarr, typeof(object));
        for (var i = 0; i < captures.Count; i++)
        {
            ctx.Il.Emit(OpCodes.Dup);
            ctx.Il.Emit(OpCodes.Ldc_I4, i);
            Convert(LoadBinding(captures[i].Original, Cty.O, ctx), Cty.O, ctx);
            ctx.Il.Emit(OpCodes.Stelem_Ref);
        }
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

        // A lambda literal applied at once: a direct call to its method, no delegate when it
        // captures nothing; otherwise its closure object and the delegate's Invoke, never the
        // object tier's `Runtime.Apply`, so the parameter keeps its shape.
        if (ap.Fn is Term.Lam lam)
        {
            var param = Natural(ap.Arg, scope);
            var fn = new Cty.Fn(param, expected);
            var info = EnsureLambda(lam, fn, scope);
            if (info.Closure is null)
            {
                Emit(ap.Arg, param, scope, ctx, tail: false);
                return EmitCall(info.Method, fn.Ret, expected, ctx, tail);
            }
            EmitLambdaValue(lam, fn, scope, ctx, fn);
            Emit(ap.Arg, param, scope, ctx, tail: false);
            return EmitInvoke(fn, expected, ctx, tail);
        }

        var callee = Natural(ap.Fn, scope);
        if (callee is Cty.Fn shape)
        {
            Emit(ap.Fn, shape, scope, ctx, tail: false);
            Emit(ap.Arg, shape.Param, scope, ctx, tail: false);
            return EmitInvoke(shape, expected, ctx, tail);
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

    /// <summary>A call of a delegate already on the stack, with its argument below it.</summary>
    private static Cty EmitInvoke(Cty.Fn fn, Cty expected, Ctx ctx, bool tail)
    {
        var invoke = fn.Clr().GetMethod("Invoke")!;
        if (tail && fn.Ret == ctx.Ret)
        {
            ctx.Il.Emit(OpCodes.Tailcall);
            ctx.Il.Emit(OpCodes.Callvirt, invoke);
            return ctx.Ret;
        }
        ctx.Il.Emit(OpCodes.Callvirt, invoke);
        return Convert(fn.Ret, expected, ctx);
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

    private static readonly ConstructorInfo AtomCtor = typeof(Runtime.Atom).GetConstructor([typeof(string)])!;

    private static readonly MethodInfo RuntimeMember =
        typeof(Runtime).GetMethod(nameof(Runtime.MemberOf))!;

    private static readonly MethodInfo RuntimeProject =
        typeof(Runtime).GetMethod(nameof(Runtime.Project))!;

    private static long I64Key(Atom key) =>
        key is Atom.I64 n ? n.Value : throw new NotImplementedException($"not ported yet: backend switch key {key.GetType().Name}");

    // ---- functions ----------------------------------------------------------

    /// <summary>
    /// Emits — or returns the already emitted — method for a lambda, plus the class that holds
    /// its captures. A lambda that reads nothing from the enclosing method stays a static
    /// method: that is why the prelude value graph needs no closure object. One that reads a
    /// local does not, so its body becomes an instance method over a generated class holding
    /// one <c>object[]</c> of captured values, and the delegate is built over that instance.
    /// </summary>
    // shortcut: one array field rather than one field per captured value, and everything the
    // enclosing method's scope holds is captured rather than only what the body reads -- a capture
    // costs an array, an entry per enclosing local, and boxing. Upgrade when a program's captures
    // show up in a profile.
    private LambdaInfo EnsureLambda(Term.Lam lam, Cty.Fn fn, Scope enclosing)
    {
        if (_lambdas.TryGetValue((lam, fn), out var existing)) return existing;

        var captures = CaptureSlots(enclosing);
        var ctxId = _methodSeq++;
        TypeBuilder? closure = null;
        ConstructorBuilder? ctor = null;
        MethodBuilder method;
        FieldBuilder? env = null;
        if (captures.Count == 0)
            method = DefineFnMethod(fn);
        else
        {
            closure = _module.DefineType($"Closure_{_nameSeq++}", TypeAttributes.NotPublic | TypeAttributes.Sealed);
            env = closure.DefineField("Env", typeof(object[]), FieldAttributes.Public);
            ctor = closure.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig,
                CallingConventions.Standard, [typeof(object[])]);
            var cil = ctor.GetILGenerator();
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            cil.Emit(OpCodes.Ldarg_0);
            cil.Emit(OpCodes.Ldarg_1);
            cil.Emit(OpCodes.Stfld, env);
            cil.Emit(OpCodes.Ret);
            method = closure.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig,
                fn.Ret.Clr(), [fn.Param.Clr()]);
        }

        var info = new LambdaInfo(method, captures, closure, ctor);
        // Cached before the body: a lambda that mentions itself, or a sibling defined later,
        // finds it already defined.
        _lambdas[(lam, fn)] = info;

        var ctx = new Ctx(method.GetILGenerator(), ctxId, fn.Ret) { Env = env };
        // The captures keep their positions in the scope list, so every de Bruijn index -- and
        // therefore every ambient index -- still means what the elaborator decided it means.
        var locals = enclosing.Locals;
        for (var slot = 0; slot < captures.Count; slot++)
            locals = locals.SetItem(captures[slot].Index, new Binding.Env(ctxId, slot, captures[slot].Original.Ty));
        var scope = new Scope(enclosing.Ambient) { Locals = locals };
        var parameter = LoadParameter(fn, ctx, offset: closure is null ? 0 : 1);
        var body = Emit(lam.Body, fn.Ret, scope.Push(parameter), ctx, tail: true);
        Convert(body, fn.Ret, ctx);
        ctx.Il.Emit(OpCodes.Ret);
        if (closure is not null) _closuresToCreate.Add(closure);
        return info;
    }

    /// <summary>
    /// The values a method generated here cannot reach on its own: the enclosing method's own
    /// locals and its environment entries. Every captured value is a boxed entry of the new
    /// closure's <c>object[]</c>, in list order.
    /// </summary>
    private static List<(int Index, Binding Original)> CaptureSlots(Scope enclosing)
    {
        // The innermost method owns whichever entries come first that belong to a method at
        // all; entries owned by a method further out are unreachable from the new lambda -- and
        // unreachable from the enclosing body too, which is why they never get captured twice.
        var owner = enclosing.Locals.FirstOrDefault(b => b is Binding.Local or Binding.Env) switch
        {
            Binding.Local local => local.Owner,
            Binding.Env env => env.Owner,
            _ => (int?)null,
        };
        if (owner is null) return [];
        var captures = new List<(int Index, Binding Original)>();
        for (var i = 0; i < enclosing.Locals.Count; i++)
            if (enclosing.Locals[i] switch
                {
                    Binding.Local local => local.Owner == owner,
                    Binding.Env env => env.Owner == owner,
                    _ => false,
                })
                captures.Add((i, enclosing.Locals[i]));
        return captures;
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
        // A `rec` whose body reads an enclosing method's local would need the same closure
        // object every member shares; it is refused rather than emitted unreachable.
        if (enclosing.Locals.Any(b => b is Binding.Local or Binding.Env))
            throw new NotImplementedException("not ported yet: backend captured local");
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

    private static Binding LoadParameter(Cty.Fn fn, Ctx ctx, int offset = 0)
    {
        ctx.Il.Emit(OpCodes.Ldarg, (short)offset);
        var local = ctx.Il.DeclareLocal(fn.Param.Clr());
        ctx.Il.Emit(OpCodes.Stloc, local);
        return new Binding.Local(ctx.Id, ctx.AddLocal(local), fn.Param);
    }

    private Cty EmitMethodValue(Binding.MethodRef method, Cty expected, Ctx ctx)
    {
        if (method.Ty is not Cty.Fn fn)
            throw new NotImplementedException("not ported yet: backend non-function value");
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
        // A spliced unit's value carries its own shape.
        Term.Imported imported => ShapeOfValue(imported.Value),
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
        // A delegate is already an object, and a boxed long is not: only the atom needs IL.
        if (to is Cty.Obj)
        {
            if (from is Cty.I64) ctx.Il.Emit(OpCodes.Box, typeof(long));
            return to;
        }
        if (from is Cty.Obj)
        {
            if (to is Cty.I64) ctx.Il.Emit(OpCodes.Unbox_Any, typeof(long));
            // Only the universal shape can be read back out of the object tier: a delegate
            // stored there keeps its own shape, and the object tier does not record which.
            else if (to is Cty.Fn fn && fn == Cty.ObjFn) ctx.Il.Emit(OpCodes.Castclass, fn.Clr());
            else throw new NotImplementedException($"not ported yet: backend conversion object -> {to.GetType().Name}");
            return to;
        }
        throw new NotImplementedException($"not ported yet: backend conversion {from.GetType().Name} -> {to.GetType().Name}");
    }
}
