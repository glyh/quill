using System.Reflection;
using System.Reflection.Emit;
using Quill.Kernel;

namespace Quill.Backend;

/// <summary>
/// The structural tier: a module, a struct type, a record and a tuple — plus the two reads
/// that consume them, <c>open</c> and <c>Dot</c>/<c>Proj</c>.
///
/// The emitted counterparts are <see cref="Runtime.Module"/>, <see cref="Runtime.Struct"/>,
/// <see cref="Runtime.Record"/>, <see cref="Runtime.Prod"/> and <see cref="Runtime.ProdTy"/>:
/// named members in binding order, the order the interpreter's <see cref="ModuleEntry"/> list
/// keeps, because a de Bruijn index into a container counts the slots its bindings pushed.
/// Building one therefore evaluates each binding in order into a local and keeps those locals
/// — the locals <em>are</em> the scope the later bindings read, and the member table is what a
/// <c>Dot</c> reads afterwards.
/// </summary>
internal sealed partial class Emitter
{
    private static readonly ConstructorInfo MemberCtor =
        typeof(Runtime.Member).GetConstructor([typeof(string), typeof(MemberKind), typeof(object)])!;

    private static readonly ConstructorInfo ModuleCtor =
        typeof(Runtime.Module).GetConstructor([typeof(Runtime.Member[]), typeof(bool)])!;

    private static readonly ConstructorInfo StructCtor =
        typeof(Runtime.Struct).GetConstructor([typeof(Runtime.Member[]), typeof(bool)])!;

    private static readonly ConstructorInfo RecordCtor =
        typeof(Runtime.Record).GetConstructor([typeof(object), typeof(Runtime.Member[])])!;

    private static readonly ConstructorInfo ProdCtor =
        typeof(Runtime.Prod).GetConstructor([typeof(object[])])!;

    private static readonly ConstructorInfo ProdTyCtor =
        typeof(Runtime.ProdTy).GetConstructor([typeof(object[])])!;

    /// <summary>One member of the container being built: what it exports, and how to load its value.</summary>
    private sealed record Pending(string? Name, MemberKind Kind, Binding Value);

    // ---- modules and structs ------------------------------------------------

    /// <summary>
    /// <c>module { … }</c>. A signature is the same shape with <c>Partial</c> set, as
    /// <c>Kont.SignatureOf</c> leaves it.
    /// </summary>
    private Cty EmitModule(Term.Module module, Cty expected, Scope scope, Ctx ctx) =>
        Convert(EmitContainer(false, [], module.Bindings, module.Signature, scope, ctx), expected, ctx);

    /// <summary>A struct type: its constructor fields (name and <em>type</em>), then its bindings.</summary>
    private Cty EmitStructType(Term.Struct st, Cty expected, Scope scope, Ctx ctx) =>
        Convert(EmitContainer(true, st.ConFields, st.Bindings, st.Partial, scope, ctx), expected, ctx);

    /// <summary>
    /// A container's value: the constructor fields' types first, then the bindings, each
    /// evaluated in the scope the earlier bindings built.
    /// </summary>
    private Cty EmitContainer(
        bool isStruct,
        EquatableArray<(string Name, Term Type)> conFields,
        EquatableArray<BindingTerm> bindings,
        bool partial,
        Scope scope,
        Ctx ctx)
    {
        var members = new List<Pending>();
        // A field's type is a member of the struct's value, but not an entry of the scope its
        // bindings are written in: `Nbe.StartStruct` collects them without pushing, so a method
        // body's indices count its own parameters and the bindings before it, nothing else.
        foreach (var (name, type) in conFields)
            members.Add(new Pending(name, MemberKind.Field, BindLocal(type, Cty.O, scope, ctx)));
        var inner = scope;
        foreach (var binding in bindings)
            inner = EmitBinding(binding, inner, ctx, members);
        var table = EmitMembers(members, ctx);
        ctx.Il.Emit(OpCodes.Ldloc, table);
        ctx.Il.Emit(OpCodes.Ldc_I4, partial ? 1 : 0);
        ctx.Il.Emit(OpCodes.Newobj, isStruct ? StructCtor : ModuleCtor);
        return Cty.O;
    }

    /// <summary>One binding of a container: its slots, and whether it exports a member.</summary>
    private Scope EmitBinding(BindingTerm binding, Scope scope, Ctx ctx, List<Pending> members)
    {
        switch (binding)
        {
            case BindingTerm.Let let:
            {
                var bound = BindNatural(let.Def, scope, ctx);
                members.Add(new Pending(let.Name, let.Kind, bound));
                return scope.Push(bound);
            }
            // An open adds no member -- it only pushes the opened names (I2) -- which is why
            // `{ N = module { pub x = 1 }; M = module { open N; pub x = 2 }; M.x }` is 2.
            case BindingTerm.Open open:
            {
                // The container local is not itself a binding: only the opened names are, so
                // the indices of the bindings after it count exactly those names.
                var container = BindLocal(open.Of, Cty.O, scope, ctx);
                var inner = scope;
                foreach (var member in open.Members)
                    inner = inner.Push(new Binding.Member(container, OpenName(member)));
                return inner;
            }
            default:
                throw new NotImplementedException($"not ported yet: backend binding {binding.GetType().Name}");
        }
    }

    /// <summary>
    /// A container binding's definition: <c>BindingTerm.Let</c> carries no type, so the shape is
    /// the value's own. An enum declaration stays a declaration (its constructors are read off
    /// it by name), and a recursive member becomes its method.
    /// </summary>
    private Binding BindNatural(Term def, Scope scope, Ctx ctx) => def switch
    {
        Term.Nominal nominal => new Binding.Nominal(nominal.Decl, Cty.O, null),
        Term.Fix fix => new Binding.MethodRef(EnsureFixMember(fix, fix.Index, Cty.ObjFn, scope), Cty.ObjFn),
        _ => BindLocal(def, Natural(def, scope), scope, ctx),
    };

    /// <summary>The names an <c>open</c> pushes, in order.</summary>
    private static string OpenName(OpenMember member) => member switch
    {
        OpenMember.Field field => field.Name,
        // A constructor exported by `export`/an enum declaration is a member of the module, so
        // the name read is the same read. A former-generic one is a lambda over its captures.
        OpenMember.Constructor constructor => constructor.Name,
        _ => throw new NotImplementedException($"not ported yet: backend open member {member.GetType().Name}"),
    };

    /// <summary>One <see cref="Runtime.Member"/> per pending member, in binding order, into a local.</summary>
    private LocalBuilder EmitMembers(List<Pending> members, Ctx ctx)
    {
        var table = ctx.Il.DeclareLocal(typeof(Runtime.Member[]));
        ctx.Il.Emit(OpCodes.Ldc_I4, members.Count);
        ctx.Il.Emit(OpCodes.Newarr, typeof(Runtime.Member));
        ctx.Il.Emit(OpCodes.Stloc, table);
        for (var i = 0; i < members.Count; i++)
        {
            ctx.Il.Emit(OpCodes.Ldloc, table);
            ctx.Il.Emit(OpCodes.Ldc_I4, i);
            ctx.Il.Emit(OpCodes.Ldstr, members[i].Name ?? "");
            ctx.Il.Emit(OpCodes.Ldc_I4, (int)members[i].Kind);
            LoadBinding(members[i].Value, Cty.O, ctx);
            ctx.Il.Emit(OpCodes.Newobj, MemberCtor);
            ctx.Il.Emit(OpCodes.Stelem_Ref);
        }
        return table;
    }

    // ---- structural values reached through the ambient environment ----------

    /// <summary>
    /// A module or struct that arrives as a <see cref="Value"/>: a unit spliced in by
    /// <c>import</c> (I5), or a prelude module reached through a name. Its members are values,
    /// so they are emitted member by member, exactly as <see cref="EmitContainer"/> emits the
    /// bindings of a container written out.
    /// </summary>
    private Cty EmitModuleValue(EquatableArray<ModuleEntry> entries, bool partial, bool isStruct, Ctx ctx)
    {
        var table = ctx.Il.DeclareLocal(typeof(Runtime.Member[]));
        ctx.Il.Emit(OpCodes.Ldc_I4, entries.Length);
        ctx.Il.Emit(OpCodes.Newarr, typeof(Runtime.Member));
        ctx.Il.Emit(OpCodes.Stloc, table);
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i] is not ModuleEntry.Field field)
                throw new NotImplementedException($"not ported yet: backend prelude value ({entries[i].GetType().Name})");
            EmitMemberAt(table, i, field.Name, field.Kind, () => EmitValueAsObject(field.Value, ctx), ctx);
        }
        ctx.Il.Emit(OpCodes.Ldloc, table);
        ctx.Il.Emit(OpCodes.Ldc_I4, partial ? 1 : 0);
        ctx.Il.Emit(OpCodes.Newobj, isStruct ? StructCtor : ModuleCtor);
        return Cty.O;
    }

    private Cty EmitRecordValue(Value.VRecord record, Ctx ctx)
    {
        var table = ctx.Il.DeclareLocal(typeof(Runtime.Member[]));
        ctx.Il.Emit(OpCodes.Ldc_I4, record.Fields.Length);
        ctx.Il.Emit(OpCodes.Newarr, typeof(Runtime.Member));
        ctx.Il.Emit(OpCodes.Stloc, table);
        for (var i = 0; i < record.Fields.Length; i++)
        {
            var (name, value) = record.Fields[i];
            EmitMemberAt(table, i, name, MemberKind.Field, () => EmitValueAsObject(value, ctx), ctx);
        }
        EmitValueAsObject(record.Type, ctx);
        ctx.Il.Emit(OpCodes.Ldloc, table);
        ctx.Il.Emit(OpCodes.Newobj, RecordCtor);
        return Cty.O;
    }

    /// <summary>A tuple or tuple type that arrives as a value.</summary>
    private Cty EmitTupleValue(EquatableArray<Value> items, bool isType, Ctx ctx)
    {
        ctx.Il.Emit(OpCodes.Ldc_I4, items.Length);
        ctx.Il.Emit(OpCodes.Newarr, typeof(object));
        for (var i = 0; i < items.Length; i++)
        {
            ctx.Il.Emit(OpCodes.Dup);
            ctx.Il.Emit(OpCodes.Ldc_I4, i);
            EmitValueAsObject(items[i], ctx);
            ctx.Il.Emit(OpCodes.Stelem_Ref);
        }
        ctx.Il.Emit(OpCodes.Newobj, isType ? ProdTyCtor : ProdCtor);
        return Cty.O;
    }

    private static void EmitMemberAt(LocalBuilder table, int index, string? name, MemberKind kind,
        Action emitValue, Ctx ctx)
    {
        ctx.Il.Emit(OpCodes.Ldloc, table);
        ctx.Il.Emit(OpCodes.Ldc_I4, index);
        ctx.Il.Emit(OpCodes.Ldstr, name ?? "");
        ctx.Il.Emit(OpCodes.Ldc_I4, (int)kind);
        emitValue();
        ctx.Il.Emit(OpCodes.Newobj, MemberCtor);
        ctx.Il.Emit(OpCodes.Stelem_Ref);
    }

    // ---- records and tuples -------------------------------------------------

    /// <summary>
    /// <c>C{v = 7}</c>: the struct value first, then the fields in written order. The record
    /// keeps its type because a member the record has no field for is a method of that type.
    /// </summary>
    private Cty EmitRecordConstruct(Term.RecordConstruct record, Cty expected, Scope scope, Ctx ctx)
    {
        var type = BindLocal(record.Type, Cty.O, scope, ctx);
        var fields = new List<Pending>();
        foreach (var (name, value) in record.Fields)
            fields.Add(new Pending(name, MemberKind.Field, BindLocal(value, Cty.O, scope, ctx)));
        var table = EmitMembers(fields, ctx);
        LoadBinding(type, Cty.O, ctx);
        ctx.Il.Emit(OpCodes.Ldloc, table);
        ctx.Il.Emit(OpCodes.Newobj, RecordCtor);
        return Convert(Cty.O, expected, ctx);
    }

    private Cty EmitProd(Term.Prod prod, Cty expected, Scope scope, Ctx ctx) =>
        Convert(EmitTuple(prod.Items, ProdCtor, scope, ctx), expected, ctx);

    private Cty EmitProdTy(Term.ProdTy prodTy, Cty expected, Scope scope, Ctx ctx) =>
        Convert(EmitTuple(prodTy.Items, ProdTyCtor, scope, ctx), expected, ctx);

    private Cty EmitTuple(EquatableArray<Term> items, ConstructorInfo ctor, Scope scope, Ctx ctx)
    {
        var array = ctx.Il.DeclareLocal(typeof(object[]));
        ctx.Il.Emit(OpCodes.Ldc_I4, items.Length);
        ctx.Il.Emit(OpCodes.Newarr, typeof(object));
        ctx.Il.Emit(OpCodes.Stloc, array);
        for (var i = 0; i < items.Length; i++)
        {
            ctx.Il.Emit(OpCodes.Ldloc, array);
            ctx.Il.Emit(OpCodes.Ldc_I4, i);
            Convert(Emit(items[i], Cty.O, scope, ctx, tail: false), Cty.O, ctx);
            ctx.Il.Emit(OpCodes.Stelem_Ref);
        }
        ctx.Il.Emit(OpCodes.Ldloc, array);
        ctx.Il.Emit(OpCodes.Newobj, ctor);
        return Cty.O;
    }

    /// <summary><c>e.Index</c> on a tuple or a tuple type.</summary>
    private Cty EmitProj(Term.Proj proj, Cty expected, Scope scope, Ctx ctx)
    {
        Convert(Emit(proj.Of, Cty.O, scope, ctx, tail: false), Cty.O, ctx);
        ctx.Il.Emit(OpCodes.Ldc_I4, proj.Index);
        ctx.Il.Emit(OpCodes.Call, RuntimeProject);
        return Convert(Cty.O, expected, ctx);
    }

    // ---- open and dot -------------------------------------------------------

    /// <summary>
    /// <c>open T</c> over a nominal binds its constructors by name. Over a <em>former</em>
    /// the elaborator binds <c>fn(A1…Ak) { T(A1…Ak).C }</c> (<c>Nbe.Enum.cs</c>), and the
    /// wrapper is erased along with every other type parameter: what is left is the
    /// constructor itself, which is what a use site's explicit spine saturates.
    ///
    /// Over a module or a struct each member is a read off one local, because which names it
    /// exports is known only from the value (<c>open</c> of a <c>Var</c>, a <c>Dot</c> or an
    /// <c>Imported</c>).
    /// </summary>
    private Cty EmitOpen(Term.Open open, Cty expected, Scope scope, Ctx ctx, bool tail)
    {
        if (NominalOf(open.Of, scope) is { } decl)
        {
            var nominal = scope;
            foreach (var member in open.Members)
            {
                if (member is not OpenMember.Constructor ctor)
                    throw new NotImplementedException($"not ported yet: backend open member {member.GetType().Name}");
                var constructor = decl.Constructor(ctor.Name)
                    ?? throw new InvalidOperationException($"`{decl.Name}` has no constructor `{ctor.Name}`");
                nominal = nominal.Push(new Binding.Ctor(decl, constructor));
            }
            return Emit(open.Body, expected, nominal, ctx, tail);
        }
        // The container local is not a binding: only the opened names are, so every de Bruijn
        // index in the body counts exactly what the elaborator pushed.
        var container = BindLocal(open.Of, Cty.O, scope, ctx);
        var inner = scope;
        foreach (var member in open.Members)
            inner = inner.Push(new Binding.Member(container, OpenName(member)));
        return Emit(open.Body, expected, inner, ctx, tail);
    }

    /// <summary><c>T.C</c> over a nominal is that constructor; over a value it is a member read.</summary>
    private Cty EmitDot(Term.Dot dot, Cty expected, Scope scope, Ctx ctx)
    {
        if (NominalOf(dot.Of, scope) is { } decl && decl.Constructor(dot.Name) is { } constructor)
            return EmitCtorValue(constructor, expected, ctx);
        Convert(Emit(dot.Of, Cty.O, scope, ctx, tail: false), Cty.O, ctx);
        ctx.Il.Emit(OpCodes.Ldstr, dot.Name);
        ctx.Il.Emit(OpCodes.Call, RuntimeMember);
        return Convert(Cty.O, expected, ctx);
    }
}
