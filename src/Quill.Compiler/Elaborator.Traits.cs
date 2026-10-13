using System.Collections.Immutable;
using Quill.Kernel;

namespace Quill.Compiler;

/// <summary>
/// Evidence that <see cref="Args"/> implement <see cref="Trait"/>: the dictionary
/// sits at <see cref="Level"/>, at dictionary type <see cref="Type"/>. <see cref="Vars"/>
/// are the impl's own type variables, the metas its head's free names bound.
/// </summary>
public sealed record TraitEvidence(TraitDecl Trait, EquatableArray<Value> Args, int Level, Value Type, EquatableArray<int> Vars = default, EquatableArray<ImplBound> Bounds = default);

/// <summary>
/// A dictionary chosen later: <see cref="Meta"/> stands for the impl of
/// <see cref="Trait"/> at <see cref="Args"/>, resolved in <see cref="Context"/> once
/// the arguments are known, at the latest when the unit's elaboration ends.
/// </summary>
public sealed record PendingEvidence(int Meta, Context Context, TraitDecl Trait, EquatableArray<Value> Args);

public sealed partial record Context
{
    /// <summary>
    /// The impls in scope, innermost first. Impls are resolved from lexical scope:
    /// a block's impl, a bound's hidden dictionary, an opened module's public impls.
    /// </summary>
    public ImmutableList<TraitEvidence> Evidence { get; init; } = ImmutableList<TraitEvidence>.Empty;

    public Context AddEvidence(TraitEvidence evidence) => this with { Evidence = Evidence.Insert(0, evidence) };
}

public static partial class Elaborator
{
    /// <summary>What an impl contributes: its trait and argument, dictionary type, and dictionary term and value.</summary>
    private sealed record ImplContribution(TraitDecl Trait, Value Arg, Value DictType, Term Core, Value Value, EquatableArray<int> Vars, EquatableArray<ImplBound> Bounds);

    /// <summary>
    /// A trait: its one parameter is a defined entry standing for itself, and each
    /// operation type is read under it and kept as a closure over the declaring
    /// environment, applied to the trait's argument where the trait is used. The
    /// parameter's own type starts as a meta the sig solves: used as a type it is
    /// `Type`, applied to an argument it is a function type — so a trait over a type
    /// constructor (`Functor(f)`, applied as `f(A)`) needs no written kind.
    /// </summary>
    private static TraitDecl ElaborateTrait(Context ctx, string name, string param, EquatableArray<(string Name, Syntax Type)> fields)
    {
        RequireLowercase(Label(param), $"a trait parameter `{Label(param)}` must be lowercase");

        var seen = new HashSet<string>();
        foreach (var (field, _) in fields)
            if (!seen.Add(field)) throw new FunException($"duplicate trait field `{field}`");

        var paramType = ctx.Eval(FreshMeta(ctx));
        var paramCtx = ctx.Define(param, paramType, new Value.VVar(ctx.Width, []));
        return new TraitDecl(name, [.. fields.Select(f => (f.Name, new Closure(ctx.Environment, TypeTerm(paramCtx, f.Type))))], paramType);
    }

    /// <summary><c>trait T(a) = sig { … }; body</c>: the trait is a definition the body sees.</summary>
    private static (Term, Value) InferTraitDef(Context ctx, Syntax.TraitDef t)
    {
        var decl = ElaborateTrait(ctx, Label(t.Name.Name), t.Param.Name, t.Fields);
        var (body, bodyType) = Infer(ctx.Define(t.Name.Name, Value.VU.Instance, new Value.VTrait(decl)), t.Body);
        return (new Term.Let(Term.U.Instance, new Term.TraitRef(decl), body), bodyType);
    }

    /// <summary>A module item <c>[pub] trait T(a) = …</c>: a member whose value is the trait.</summary>
    private static Context InferTraitBinding(Context ctx, Binding.Trait t, List<BindingTerm> terms, List<ModuleEntry> entries)
    {
        var decl = ElaborateTrait(ctx, Label(t.Name.Name), t.Param.Name, t.Fields);
        var kind = t.Public ? MemberKind.Public : MemberKind.Private;
        var term = new BindingTerm.Let(Label(t.Name.Name), kind, new Term.TraitRef(decl));
        terms.Add(term);
        entries.Add(new ModuleEntry.Field(term.Name, kind, Value.VU.Instance));
        return ExtendFromSlots(ctx, term, [(t.Name.Name, Value.VU.Instance)]);
    }

    /// <summary>The trait a path names, by the identity its value carries - never by its spelling.</summary>
    private static TraitDecl LocateTrait(Context ctx, Syntax path)
    {
        var (term, _) = Infer(ctx, path);
        return ctx.Force(ctx.Eval(term)) is Value.VTrait trait
            ? trait.Decl
            : throw new FunException("unknown trait");
    }

    /// <summary><c>Trait(Arg)</c>: the trait, its argument, and the dictionary type an impl of it provides.</summary>
    private static (TraitDecl Trait, Value Arg, Value.VTraitDict DictType) ImplDictType(Context ctx, Syntax traitPath, Syntax argSyntax)
    {
        var trait = LocateTrait(ctx, traitPath);
        var (argTerm, argType) = Infer(ctx, argSyntax);
        var arg = ctx.Eval(argTerm);
        // The head's argument is checked against the trait's own parameter type: `Type`
        // for a trait over a type, a function type for one over a type constructor.
        var paramType = ctx.Force(trait.ParamType);
        if (paramType is Value.VU) CheckTypeLike(ctx, argType, arg);
        else ctx.Unify(argType, paramType);
        return (trait, arg, new Value.VTraitDict(trait, [arg], Nbe.OperationTypes(ctx.Metas, trait, arg)));
    }

    /// <summary><c>struct { a : p; _ }</c> as a head's type pattern: the rest marker a lone <c>_</c> item was read as.</summary>
    private static bool IsStructRest(Binding binding) =>
        binding is Binding.Field { Name: "_", Type: Syntax.Atom { Value: Atom.Unit } };

    /// <summary>A struct type's constructor fields, by label - the members matching and coverage compare.</summary>
    private static List<ModuleEntry.Field> ConFields(Value.VStruct s) =>
        [.. s.Entries.OfType<ModuleEntry.Field>().Where(f => f.Kind == MemberKind.Field)];

    /// <summary>Whether a struct syntax is a head's type pattern: it names a rest.</summary>
    private static bool HasStructRest(Syntax syntax) => syntax is Syntax.Struct st && st.Bindings.Any(IsStructRest);

    /// <summary>
    /// A struct read as an impl head's type pattern - <c>struct { a : p; _ }</c>:
    /// its constructor fields' types, and partial when the rest is written. It is
    /// a condition on a type, not a type of its own: matching is the unification
    /// that already compares a partial struct's fields against a struct holding
    /// them (a width), so a partial head matches every record with those fields
    /// and an exact one matches none with more. Each field's type is the pattern's
    /// own binder where it names one, and the body's demand on it is the hidden
    /// dictionary argument the head's other variables already provide.
    /// </summary>
    private static (Term, Value) InferImplHeadStruct(Context ctx, Syntax.Struct st)
    {
        var fields = new List<ModuleEntry.Field>();
        var partial = false;
        foreach (var item in st.Bindings)
        {
            if (IsStructRest(item)) { partial = true; continue; }
            if (item is not Binding.Field field)
                throw new FunException("an impl head's struct names fields and a rest `_`, nothing else");
            fields.Add(new ModuleEntry.Field(field.Name, MemberKind.Field, TypeValue(ctx, field.Type)));
        }
        RejectDuplicates(fields.Select(f => f.Name));
        var value = new Value.VStruct([.. fields], partial);
        return (ctx.Quote(value), Value.VU.Instance);
    }

    /// <summary>
    /// An impl head's free lowercase names are the impl's own type variables,
    /// bindable with no declaration: <c>impl Size(Option(a))</c> makes <c>a</c> its
    /// own. Each is pushed as a definition of a fresh meta around the head's own
    /// inference (not the body's), and the head's free occurrences are rewritten
    /// to it. A free uppercase name is a reference, not a binder, so one that
    /// resolves to nothing is an error rather than a fresh variable - which is
    /// where the old silent `impl Size(Optoin(a))` typo used to hide.
    /// </summary>
    private static (Context Head, Syntax HeadSyntax, EquatableArray<(string Name, Value Value)> Vars) BindHeadNames(Context ctx, Syntax head, EquatableArray<Param> binders)
    {
        var written = binders.Select(b => b.Name.Name).ToHashSet();
        var free = new List<string>();
        head.Map(new SyntaxMapper
        {
            Form = form =>
            {
                switch (form)
                {
                    // A name the head's binders write is the impl's binding form: it
                    // binds even where an outer binding of that name would make it a
                    // reference.
                    case Syntax.Var v when written.Contains(v.Id.Name) || !Resolves(ctx, v.Id.Name):
                        if (!written.Contains(v.Id.Name))
                            RequireLowercase(v.Id.Name, $"`{v.Id.Name}` in an impl head is a reference, not a binder; an impl head binder must be lowercase");
                        free.Add(v.Id.Name);
                        break;
                    case Syntax.OpenChoice c when written.Contains(c.Name.Name) || !Resolves(ctx, c.Name.Name, c.Opens, c.Fallback):
                        if (!written.Contains(c.Name.Name))
                            RequireLowercase(c.Name.Name, $"`{c.Name.Name}` in an impl head is a reference, not a binder; an impl head binder must be lowercase");
                        free.Add(c.Name.Name);
                        break;
                }
                return form;
            },
        });
        free = [.. free.Distinct()];
        if (free.Count == 0) return (ctx, head, []);

        var vars = EquatableArray<(string Name, Value Value)>.Empty;
        foreach (var name in free)
        {
            var id = ctx.Metas.Fresh();
            var value = ctx.Eval(new Term.InsertedMeta(id, ctx.EntryKinds));
            ctx = ctx.Define(name, Value.VU.Instance, value);
            vars = vars.Add((name, value));
        }
        var bound = free.ToHashSet();
        var rewritten = head.Map(new SyntaxMapper
        {
            Form = form => form is Syntax.OpenChoice c && bound.Contains(c.Name.Name) ? new Syntax.Var(c.Name) : form,
        });
        return (ctx, rewritten, vars);
    }

    /// <summary>
    /// A name that would bind must be lowercase: a bare uppercase name refers, so
    /// an uppercase one that resolves to nothing is an error rather than a silent
    /// fresh variable. Keywords are their own syntax nodes and never reach here,
    /// so <c>Self</c> keeps referring.
    /// </summary>
    private static void RequireLowercase(string name, string message)
    {
        if (name.Length > 0 && char.IsAsciiLetterUpper(name[0]))
            throw new FunException($"{message}; write `{char.ToLowerInvariant(name[0])}{name[1..]}` to bind");
    }

    /// <summary>Whether a name is supplied by the context, as <see cref="Context.Locate"/> would.</summary>
    private static bool Resolves(Context ctx, string name)
    {
        try { ctx.Locate(name); return true; }
        catch (FunException) { return false; }
    }

    /// <summary>Whether an open choice is supplied, as <see cref="Context.LocateChoice"/> would.</summary>
    private static bool Resolves(Context ctx, string name, EquatableArray<string> opens, string? fallback)
    {
        try { ctx.LocateChoice(name, opens, fallback); return true; }
        catch (FunException) { return false; }
    }

    /// <summary>
    /// An impl's dictionary: every operation of the trait given exactly once, each
    /// checked at its type for this argument. The dictionary is a struct of the
    /// operations; each is elaborated in the impl's context, so the ith is shifted
    /// past the i entries the struct pushes before it.
    /// </summary>
    private static ImplContribution Contribute(Context ctx, Id? name, Syntax traitPath, Syntax argSyntax, EquatableArray<(string Name, Syntax Value)> fields, EquatableArray<Param> binders)
    {
        var (headCtx, head, declared) = BindHeadNames(ctx, argSyntax, binders);
        var (trait, arg, dictType) = ImplDictType(headCtx, traitPath, head);
        var seen = new HashSet<string>();
        foreach (var (field, _) in fields)
        {
            if (!seen.Add(field)) throw new FunException($"duplicate field `{field}`");
            if (dictType.Operations.All(o => o.Name != field)) throw new FunException($"unknown trait method `{field}`");
        }
        foreach (var (op, _) in dictType.Operations)
            if (!seen.Contains(op)) throw new FunException($"missing trait field `{op}`");

        var pendingMark = ctx.Metas.PendingEvidence.Count;
        var bindings = fields.Select((f, i) => (BindingTerm)new BindingTerm.Let(f.Name, MemberKind.Public,
            Check(ctx, f.Value, dictType.Operations.Last(o => o.Name == f.Name).Type).Shift(i))).ToList();

        // The impl is generic over the variables its head names. The head is elaborated
        // now, so each name stands for the variable it was left as: the meta the head's
        // own binder made, once inference has solved it on to the fresh meta a type
        // constructor's parameter introduced (the variable that is left, which both the
        // demand and the matching name).
        var vars = EquatableArray<int>.Empty;
        foreach (var (_, value) in declared)
            if (ctx.Force(value) is Value.VMeta meta && IndexOf(vars, meta.Id) < 0)
                vars = vars.Add(meta.Id);

        int VarOf(string n)
        {
            foreach (var (d, v) in declared)
                if (d == n) return ctx.Force(v) is Value.VMeta m ? IndexOf(vars, m.Id) : -1;
            return -1;
        }
        string VarName(int variable)
        {
            foreach (var (d, v) in declared)
                if (ctx.Force(v) is Value.VMeta m && IndexOf(vars, m.Id) == variable) return d;
            return "?";
        }
        var who = name is null ? $"an impl of `{trait.Name}`" : $"impl `{name.Name}`";

        // The bounds the head wrote - `impl name[a : Eq] : …` - are the whole truth
        // about the impl's bounds: one implicit dictionary argument per (trait,
        // variable), in the order written, whether or not the body ever uses it
        // (writing a bound deliberately narrows the impl).
        var bounds = new List<(ImplBound Bound, Value Arg)>();
        if (!binders.IsEmpty)
        {
            foreach (var binder in binders)
            {
                var variable = VarOf(binder.Name.Name);
                if (variable < 0) throw new FunException($"{who}: the binder `{binder.Name.Name}` does not occur in its head");
                if (binder.Type is null) continue;
                foreach (var bound in TraitBounds(headCtx, binder.Type) ?? throw new FunException($"{who}: the bound on `{binder.Name.Name}` must name traits"))
                    if (!bounds.Any(b => ReferenceEquals(b.Bound.Trait, bound) && b.Bound.Var == variable))
                        bounds.Add((new ImplBound(bound, variable), new Value.VMeta(vars[variable], [])));
            }
        }

        // The evidence the body demanded for the head's variables: with nothing
        // written it is the impl's bound (the inference an unannotated impl keeps);
        // with bounds written, each demand must be one the head wrote - the body may
        // use at most what the declaration writes. A demand for anything else is left
        // where it was made, so a missing one is reported at the impl's definition,
        // not through a misleading use site.
        var promoted = new Dictionary<int, int>();
        foreach (var pending in ctx.Metas.PendingEvidence.Skip(pendingMark).ToList())
        {
            if (pending.Args.Length != 1 || ctx.Force(pending.Args[0]) is not Value.VMeta meta) continue;
            var variable = IndexOf(vars, meta.Id);
            if (variable < 0) continue;
            var at = bounds.FindIndex(b => ReferenceEquals(b.Bound.Trait, pending.Trait) && b.Bound.Var == variable);
            if (at < 0)
            {
                if (binders.IsEmpty) { at = bounds.Count; bounds.Add((new ImplBound(pending.Trait, variable), meta)); }
                else
                {
                    var wrote = string.Join(", ", bounds.Select(b => $"{VarName(b.Bound.Var)} : {b.Bound.Trait.Name}"));
                    throw new FunException($"{who}: its body demands `{pending.Trait.Name}({VarName(variable)})`, which its head `[{wrote}]` does not write");
                }
            }
            promoted[pending.Meta] = at;
            ctx.Metas.PendingEvidence.Remove(pending);
        }

        // The impl is its dictionary as a function of those dictionaries: the leading
        // implicit binders whose occurrences each promoted choice becomes.
        var core = Rebounded(new Term.Struct([], [.. bindings], Partial: false), bounds.Count, promoted);
        for (var i = 0; i < bounds.Count; i++) core = new Term.Lam(core);
        var type = ctx.Quote(dictType);
        for (var i = bounds.Count - 1; i >= 0; i--)
        {
            var domain = new Value.VTraitDict(bounds[i].Bound.Trait, [bounds[i].Arg],
                Nbe.OperationTypes(ctx.Metas, bounds[i].Bound.Trait, bounds[i].Arg));
            type = new Term.Pi(Explicitness.Implicit, ctx.Quote(domain), type.Shift(1));
        }
        return new ImplContribution(trait, arg, ctx.Eval(type), core, ctx.Eval(core), vars, [.. bounds.Select(b => b.Bound)]);
    }

    /// <summary>
    /// The impl's own body under its <paramref name="lambdas"/> bound dictionaries: an
    /// inserted meta a promoted choice stands for becomes that binder, and every other
    /// index moves out past the binders.
    /// </summary>
    private static Term Rebounded(Term body, int lambdas, Dictionary<int, int> promoted)
    {
        var shifted = body.Shift(lambdas);
        return shifted.Map((term, under) => term is Term.InsertedMeta m && promoted.TryGetValue(m.Id, out var at)
            ? new Term.Var(under + lambdas - at - 1)
            : null);
    }

    /// <summary>The position of <paramref name="id"/> in <paramref name="vars"/>, or -1.</summary>
    private static int IndexOf(EquatableArray<int> vars, int id)
    {
        for (var i = 0; i < vars.Length; i++) if (vars[i] == id) return i;
        return -1;
    }

    /// <summary>
    /// The dictionary an impl offers at its head, past the implicit binders its own
    /// variables' bounds added. Null when the type is no impl's dictionary at all.
    /// </summary>
    private static Value.VTraitDict? OfferedDict(Context ctx, Value type) => ctx.Force(type) switch
    {
        Value.VTraitDict dict => dict,
        Value.VPi { Explicitness: Explicitness.Implicit } pi =>
            OfferedDict(ctx, Nbe.ApplyClosure(ctx.Metas, pi.Codomain, new Value.VVar(ctx.Width, []))),
        _ => null,
    };

    /// <summary><c>impl Trait(Arg) = module { … }; body</c>: the dictionary is an entry, and evidence, for the body.</summary>
    private static (Term, Value) InferImplDef(Context ctx, Syntax.ImplDef i)
    {
        var c = Contribute(ctx, i.Name, i.TraitPath, i.Arg, i.Fields, i.Binders);
        var (inner, entry) = ctx.DefineAnonymous(c.DictType, c.Value);
        inner = inner.AddEvidence(new TraitEvidence(c.Trait, [c.Arg], entry.Level, c.DictType, c.Vars, c.Bounds));
        var (body, bodyType) = Infer(inner, i.Body);
        return (new Term.Let(ctx.Quote(c.DictType), c.Core, body), bodyType);
    }

    /// <summary>
    /// A module or struct item <c>[pub] impl [name :] Trait(Arg) = …</c>: one entry,
    /// pushed through the slot list (I2), and evidence for the items after it.
    /// </summary>
    private static (Context, BindingTerm, ModuleEntry) ElaborateImplItem(Context ctx, Binding.Impl impl)
    {
        var fields = impl.Fields ?? throw new InvalidOperationException("an impl item without operations outside a signature");
        var c = Contribute(ctx, impl.Name, impl.TraitPath, impl.Arg, fields, impl.Binders);
        var kind = impl.Public ? MemberKind.Public : MemberKind.Private;
        var name = impl.Name is { } written ? Label(written.Name) : null;
        var term = new BindingTerm.Impl(name, kind, c.Core, c.DictType);

        var slots = term.Slots() ?? throw new InvalidOperationException("an impl has a slot list");
        if (slots.Length != 1 || slots[0].Source is not SlotSource.Def def)
            throw new InvalidOperationException("an impl contributes one entry");
        var value = ctx.Eval(def.Term);
        var (after, entry) = ctx.DefineAnonymous(c.DictType, value);
        after = after.AddEvidence(new TraitEvidence(c.Trait, [c.Arg], entry.Level, c.DictType, c.Vars, c.Bounds));
        return (after, term, new ModuleEntry.Impl(name, kind, c.DictType, value, c.Vars, c.Bounds));
    }

    private static Context InferImplBinding(Context ctx, Binding.Impl impl, List<BindingTerm> terms, List<ModuleEntry> entries)
    {
        var (after, term, entry) = ElaborateImplItem(ctx, impl);
        terms.Add(term);
        entries.Add(entry);
        return after;
    }

    /// <summary>
    /// <c>name : impl Trait(Arg)</c> in a signature: the named impl the module must
    /// provide, whose member is its dictionary type.
    /// </summary>
    private static Context InferSignatureImpl(Context inner, Value self, Binding.Impl impl, List<BindingTerm> bindings)
    {
        var name = impl.Name is { } written ? Label(written.Name) : throw new InvalidOperationException("a signature's impl is named");
        var (_, _, dictType) = ImplDictType(inner, impl.TraitPath, impl.Arg);
        bindings.Add(new BindingTerm.Impl(name, MemberKind.Public, inner.Quote(dictType), Value.VU.Instance));
        return inner.DefineAnonymous(dictType, Nbe.DotValue(self, name)).Item1;
    }

    /// <summary>
    /// The traits an implicit binder's bound names - <c>[A : Eq]</c> or
    /// <c>[A : {Eq, Show}]</c> - or null when the domain is an ordinary type. A
    /// <c>{…}</c> set must list traits, each once.
    /// </summary>
    private static List<TraitDecl>? TraitBounds(Context ctx, Syntax domain)
    {
        TraitDecl? Named(Syntax form)
        {
            var (term, type) = Infer(ctx, form);
            return ctx.Force(type) is Value.VU && ctx.Force(ctx.Eval(term)) is Value.VTrait trait ? trait.Decl : null;
        }

        List<TraitDecl> traits;
        switch (domain)
        {
            case Syntax.TraitBoundSet set:
                traits = [.. set.Traits.Select(f => Named(f) ?? throw new FunException("a {…} bound lists traits"))];
                break;
            case Syntax.Var or Syntax.OpenChoice or Syntax.FieldAccess:
                if (Named(domain) is not { } single) return null;
                traits = [single];
                break;
            default:
                return null;
        }

        for (var i = 0; i < traits.Count; i++)
            if (traits.Skip(i + 1).Any(t => ReferenceEquals(t, traits[i])))
                throw new FunException($"duplicate trait bound `{traits[i].Name}`");
        return traits;
    }

    /// <summary>
    /// <c>[A : Eq + Show] -&gt; B</c>: an implicit type <c>A</c>, then one hidden
    /// dictionary per bound, each evidence for <c>A</c> in <c>B</c>.
    /// </summary>
    private static (Term, Value) InferBoundArrow(Context ctx, Syntax.Arrow arrow, List<TraitDecl> traits)
    {
        var arg = new Value.VVar(ctx.Width, []);
        // The bound variable takes the bound trait's own parameter type: `[A : Type]`
        // for a trait over a type, `[F : Type -> Type]` for one over a type constructor.
        var paramType = traits.Count > 0 ? ctx.Force(traits[0].ParamType) : Value.VU.Instance;
        var inner = ctx.Bind(arrow.Name!.Name, paramType);
        var dicts = new List<Term>();
        foreach (var trait in traits)
        {
            var dictType = new Value.VTraitDict(trait, [arg], Nbe.OperationTypes(ctx.Metas, trait, arg));
            dicts.Add(inner.Quote(dictType));
            (inner, var entry) = inner.BindAnonymous(dictType);
            inner = inner.AddEvidence(new TraitEvidence(trait, [arg], entry.Level, dictType));
        }
        var body = TypeTerm(inner, arrow.Codomain);
        for (var i = dicts.Count - 1; i >= 0; i--) body = new Term.Pi(Explicitness.Implicit, dicts[i], body);
        return (new Term.Pi(Explicitness.Implicit, Nbe.Quote(ctx.Metas, ctx.Width, paramType), body), Value.VU.Instance);
    }

    /// <summary>
    /// Checking a lambda's body against a type that next takes hidden dictionaries:
    /// each is bound, as evidence for the body. The count is how many lambdas the
    /// body's term is wrapped in.
    /// </summary>
    private static (Context, Value, int) InsertHiddenDicts(Context ctx, Value type)
    {
        var count = 0;
        while (ctx.Force(type) is Value.VPi { Explicitness: Explicitness.Implicit } pi && ctx.Force(pi.Domain) is Value.VTraitDict dict)
        {
            (ctx, var entry) = ctx.BindAnonymous(dict);
            ctx = ctx.AddEvidence(new TraitEvidence(dict.Decl, dict.Args, entry.Level, dict));
            type = Nbe.ApplyClosure(ctx.Metas, pi.Codomain, new Value.VVar(entry.Level, []));
            count++;
        }
        return (ctx, type, count);
    }

    /// <summary>A candidate impl for a use: its head arguments, its own variables, the dictionary it offers, and its bounds.</summary>
    private sealed record EvidenceCandidate(EquatableArray<Value> Args, EquatableArray<int> Vars, Term Term, Value Type, EquatableArray<ImplBound> Bounds);

    /// <summary>
    /// The impl in scope for <paramref name="trait"/> at <paramref name="args"/>
    /// (traits.md, "Resolution"): evidence whose arguments match, and a struct
    /// argument's own public impls, of which the most precise is chosen. Null when
    /// there is none; no unique most precise one is an ambiguity. An argument whose
    /// type is not yet known (a meta) makes the choice wait.
    /// </summary>
    private static (Term Term, Value Type)? ResolveEvidence(Context ctx, TraitDecl trait, EquatableArray<Value> args)
    {
        // An unknown argument type waits (rule 4): a candidate matching it could still
        // be beaten by a later, more precise one, so resolving now would be a guess.
        if (args.Any(a => ctx.Force(a) is Value.VMeta or Value.VNeutral)) return null;

        // Whether a head's arguments answer a use's: rule 4's match, and the direction a
        // width pattern adds. Unification alone cannot say the second - its partial-struct
        // rule satisfies whichever side has fewer members, so a head naming more fields
        // than the use's type would match it, and the width is the other direction only.
        bool HeadMatches(EquatableArray<Value> head, EquatableArray<int> vars, EquatableArray<Value> use) =>
            Matches(ctx, head, use, vars, out _) && head.Zip(use).All(pair => WidthRequires(pair.First, pair.Second));

        // Every field a width pattern names is one the type at the use has, at any depth
        // (`Option(struct { a : q; _ })`). A shape it cannot see into defers to
        // unification, so a width never refuses what unification would accept.
        bool WidthRequires(Value pattern, Value target)
        {
            switch (ctx.Force(pattern), ctx.Force(target))
            {
                case (Value.VStruct p, Value.VStruct t):
                    return (!p.Partial || ConFields(p).All(f => ConFields(t).Any(x => x.Name == f.Name)))
                        && ConFields(p).All(f => ConFields(t).LastOrDefault(x => x.Name == f.Name) is { } match
                            ? WidthRequires(f.Value, match.Value) : true);
                case (Value.VNominal p, Value.VNominal t)
                    when ReferenceEquals(p.Decl, t.Decl) && p.Captures.Length == t.Captures.Length:
                    return p.Captures.Zip(t.Captures).All(c => WidthRequires(c.First, c.Second));
                default:
                    return true;
            }
        }

        // P is more precise than Q when P's arguments are an instance of Q's: Q's own
        // variables are filled in to give P's (rule 2). Two width patterns are ordered
        // structurally instead (PatternSubsumes), since unification cannot order them -
        // each holds the other's fields - and unmatched shapes fall back to it.
        bool Instance(EvidenceCandidate p, EvidenceCandidate q) =>
            (p.Args.Length == 1 && q.Args.Length == 1
                ? PatternSubsumes(ctx.Force(p.Args[0]), ctx.Force(q.Args[0]), q)
                : null)
            ?? Matches(ctx, q.Args, p.Args, q.Vars, out _);

        // Whether every use matching the pattern P also matches Q, structurally, when
        // the two heads are the same shape: Q's fields are P's (a partial pattern
        // requires no field P does not), each pair is an instance in turn - or unifies
        // with Q's own variables solvable, the direction rule 2 gives a value head - and
        // only a partial P is subsumed by the open width a partial Q leaves. Null where
        // the relation has no opinion (two exact structs), which is where rule 2 answers.
        bool? PatternSubsumes(Value pattern, Value other, EvidenceCandidate q)
        {
            switch (pattern, other)
            {
                case (Value.VStruct p, Value.VStruct o):
                    if (!p.Partial && !o.Partial) return null;
                    var mine = ConFields(p);
                    return (!p.Partial || o.Partial) && ConFields(o).All(field =>
                        mine.LastOrDefault(f => f.Name == field.Name) is { } both
                        && (PatternSubsumes(both.Value, field.Value, q)
                            ?? Matches(ctx, [field.Value], [both.Value], q.Vars, out _)));
                // A container of patterns compares its captures the same way, so a width
                // nests (`Option(struct { a : q; _ })`); a capture it has no opinion on
                // falls back to unification.
                case (Value.VNominal p, Value.VNominal o)
                    when ReferenceEquals(p.Decl, o.Decl) && p.Captures.Length == o.Captures.Length:
                    return p.Captures.Zip(o.Captures).All(c =>
                        PatternSubsumes(c.First, c.Second, q) ?? Matches(ctx, [c.Second], [c.First], q.Vars, out _));
                default:
                    return null;
            }
        }

        var candidates = ctx.Evidence
            .Where(e => ReferenceEquals(e.Trait, trait) && HeadMatches(e.Args, e.Vars, args))
            .Select(e => new EvidenceCandidate(e.Args, e.Vars,
                new Term.Var(Nbe.LevelToIndex(ctx.Width, e.Level)), e.Type, e.Bounds))
            .ToList();

        if (args.Length == 1 && ctx.Force(args[0]) is Value.VStruct st)
            candidates.AddRange(st.Entries.OfType<ModuleEntry.Impl>()
                .Where(i => i.Kind == MemberKind.Public && OfferedDict(ctx, i.DictType) is { } d
                            && ReferenceEquals(d.Decl, trait) && HeadMatches(d.Args, i.Vars, args))
                .Select(i => new EvidenceCandidate(
                    OfferedDict(ctx, i.DictType)!.Args, i.Vars, ctx.Quote(i.Value), i.DictType, i.Bounds)));

        if (candidates.Count == 0) return null;
        var best = candidates.Where(p => candidates.All(q => ReferenceEquals(p, q) || Instance(p, q))).ToList();
        return best.Count == 1
            ? (Instantiate(ctx, best[0], args), best[0].Type)
            : throw new FunException($"ambiguous implementation of `{trait.Name}`");
    }

    /// <summary>
    /// The candidate's dictionary: the impl's term, applied to one dictionary per bound
    /// its own variables carry - each read from the use's scope at the value the match
    /// gave that variable, exactly as a bounded function's hidden arguments are.
    /// </summary>
    private static Term Instantiate(Context ctx, EvidenceCandidate candidate, EquatableArray<Value> args)
    {
        if (candidate.Bounds.Length == 0) return candidate.Term;
        if (!Matches(ctx, candidate.Args, args, candidate.Vars, out var solved))
            throw new InvalidOperationException("the chosen impl no longer matches");
        var term = candidate.Term;
        foreach (var bound in candidate.Bounds)
        {
            var arg = solved[bound.Var]
                ?? throw new FunException($"missing implementation of `{bound.Trait.Name}`");
            term = new Term.Ap(term, Explicitness.Implicit, Evidence(ctx, bound.Trait, [arg]));
        }
        return term;
    }

    /// <summary>
    /// The dictionary for <paramref name="trait"/> at <paramref name="args"/>: the impl
    /// chosen now, or - while an argument is not yet known - a meta the choice fills
    /// once it is (<see cref="ResolvePendingEvidence"/>). A known argument with no impl
    /// is an error.
    /// </summary>
    private static Term Evidence(Context ctx, TraitDecl trait, EquatableArray<Value> args)
    {
        if (ResolveEvidence(ctx, trait, args) is { } found) return found.Term;
        if (!args.Any(a => Unresolved(ctx, a))) throw MissingImplementation(ctx, trait, args);
        var meta = ctx.Metas.Fresh();
        ctx.Metas.PendingEvidence.Add(new PendingEvidence(meta, ctx, trait, args));
        return new Term.InsertedMeta(meta, ctx.EntryKinds);
    }

    /// <summary>
    /// The choices that waited on argument types, made now that elaboration of the
    /// unit has ended: each argument is known and one impl matches, or it is an error.
    /// Only the choices this unit made (metas from <paramref name="since"/>) must be
    /// settled; an importer's pending choices wait for the importer.
    /// </summary>
    public static void ResolvePendingEvidence(MetaContext metas, int since)
    {
        foreach (var pending in metas.PendingEvidence.Where(p => p.Meta >= since).ToList())
        {
            metas.PendingEvidence.Remove(pending);
            var ctx = pending.Context;
            var found = ResolveEvidence(ctx, pending.Trait, pending.Args)
                ?? throw (pending.Args.Any(a => ctx.Force(a) is Value.VMeta)
                    ? new FunException($"cannot choose an implementation of `{pending.Trait.Name}`: its argument type is never known")
                    : MissingImplementation(ctx, pending.Trait, pending.Args));
            // The chosen dictionary is one value whatever the meta is applied to (levels
            // are stable), so its solution ignores the spine: a lambda per bound entry.
            Term solution = new Term.Imported(ctx.Eval(found.Term));
            foreach (var kind in ctx.EntryKinds)
                if (kind == EntryKind.Bound) solution = new Term.Lam(solution);
            metas.Solve(pending.Meta, Nbe.Eval(metas, Environment.Empty, solution));
        }
    }

    /// <summary>Whether an argument is not yet known well enough to say no impl exists for it.</summary>
    private static bool Unresolved(Context ctx, Value arg) => ctx.Force(arg) is Value.VMeta or Value.VVar or Value.VNeutral;

    /// <summary>
    /// The failure when no entry implements <paramref name="trait"/> at <paramref name="args"/>.
    /// A genuine absence is the user's <c>missing implementation</c>; but an evidence entry
    /// refinement left reading the matched variable - one that would answer the demand had
    /// <c>RefineContext</c>'s Evidence channel rewritten it - is the implementation's own
    /// break, an invariant failure rather than a language error.
    /// </summary>
    internal static Exception MissingImplementation(Context ctx, TraitDecl trait, EquatableArray<Value> args)
    {
        if (ctx.Refinement is { } refinement)
        {
            var stale = ctx.Evidence.FirstOrDefault(e => ReferenceEquals(e.Trait, trait)
                && ResolveEvidence(ctx with
                {
                    Evidence = ImmutableList.Create(RefinedEvidence(ctx, refinement.Level, refinement.Replacement, e)),
                }, trait, args) is not null);
            if (stale is not null)
                return new InvalidOperationException(
                    $"evidence for `{trait.Name}` was not refined: the branch asks for {DescribeDict(ctx, trait.Name, args)}, "
                    + $"the entry still says {DescribeDict(ctx, stale.Trait.Name, stale.Args)}. RefineContext's Evidence channel missed it.");
        }
        return new FunException($"missing implementation of `{trait.Name}`");
    }

    /// <summary>A dictionary type's name and arguments, for an invariant's message: <c>Size(Char)</c>.</summary>
    private static string DescribeDict(Context ctx, string name, EquatableArray<Value> args) =>
        $"{name}({string.Join(", ", args.Select(a => DescribeArg(ctx, a)))})";

    /// <summary>One dictionary argument, for an invariant's message: <c>Char</c>, <c>A</c>.</summary>
    private static string DescribeArg(Context ctx, Value arg) => ctx.Force(arg) switch
    {
        Value.VAtomTy t => t.Ty.ToString(),
        Value.VU => "Type",
        Value.VVar v => NameAt(ctx, v.Level),
        Value.VNominal n => n.Decl.Name,
        Value.VCon c => c.Name,
        Value.VTraitDict d => DescribeDict(ctx, d.Decl.Name, d.Args),
        var other => other.GetType().Name,
    };

    /// <summary>The context name bound at <paramref name="level"/>, as a user wrote it.</summary>
    private static string NameAt(Context ctx, int level)
    {
        foreach (var (name, entry) in ctx.Names)
            if (entry.Level == level)
            {
                var i = name.IndexOf('#');
                return i >= 0 ? name[..i] : name;
            }
        return $"#{level}";
    }

    /// <summary>
    /// Whether an impl head's arguments match a use's: read-back equality, or a
    /// structural unification that solves only the impl's own variables
    /// (<paramref name="vars"/>) - the use's metas and variables stay rigid - and is
    /// undone, so the impl stays generic. The no-solve guard beyond those variables
    /// is what makes it a match rather than a guess: it admits width subtyping and
    /// eta, but never commits to the use's unknowns, so a choice still waits on an
    /// unknown argument type (traits.md, "Resolution", rule 4).
    /// </summary>
    private static bool Matches(Context ctx, EquatableArray<Value> pattern, EquatableArray<Value> target, EquatableArray<int> vars, out Value?[] solved)
    {
        solved = [];
        if (pattern.Length != target.Length) return false;
        var before = ctx.Metas.Snapshot();
        // The match relation is more permissive than equality: under it a struct is
        // compared by its fields alone (MetaContext.Matching). The trial is undone
        // either way, and the unifier proper runs with it false.
        var wasMatching = ctx.Metas.Matching;
        ctx.Metas.Matching = true;
        var ok = true;
        try
        {
            foreach (var (p, t) in pattern.Zip(target))
                if (!Nbe.Convertible(ctx.Metas, ctx.Width, p, t) && !Unify.TryValues(ctx.Metas, ctx.Width, p, t))
                { ok = false; break; }
            if (ok)
            {
                var after = ctx.Metas.Snapshot();
                for (var i = 0; i < before.Length && ok; i++)
                    if (before[i] is null && after[i] is not null && !vars.Contains(i)) ok = false;
                // Read the impl's own variables out before the trial is undone: the values
                // are stable, and an unsolved one stays the use's meta after the restore.
                if (ok)
                    solved = [.. vars.Select(v => ctx.Metas.Solution(v) is { } s ? ctx.Force(s) : null)];
            }
        }
        catch (FunException) { ok = false; }
        finally { ctx.Metas.Restore(before); ctx.Metas.Matching = wasMatching; }
        return ok;
    }

    /// <summary>The trait a checked form's value is, when it is one.</summary>
    private static TraitDecl? TraitOf(Context ctx, Term term, Value type) =>
        ctx.Force(type) is Value.VU && ctx.Force(ctx.Eval(term)) is Value.VTrait trait ? trait.Decl : null;

    /// <summary>
    /// <c>Trait.op</c>: generic over the trait's argument, taking the dictionary for it
    /// as a hidden argument - <c>[A : Type] -&gt; [Trait(A)] -&gt; op's type at A</c> - so
    /// the impl is chosen at the use's argument type exactly as a bound's is.
    /// </summary>
    private static (Term, Value) TraitMethod(Context ctx, TraitDecl trait, string name)
    {
        var arg = new Value.VVar(ctx.Width, []);
        var dictType = new Value.VTraitDict(trait, [arg], Nbe.OperationTypes(ctx.Metas, trait, arg));
        if (dictType.Operations.All(o => o.Name != name)) throw new FunException($"unknown trait method `{name}`");
        var opType = dictType.Operations.Last(o => o.Name == name).Type;
        // The argument binder takes the trait's own parameter type: `[A : Type]` for a
        // trait over a type, `[F : Type -> Type]` for one over a type constructor.
        var type = new Term.Pi(Explicitness.Implicit, Nbe.Quote(ctx.Metas, ctx.Width, ctx.Force(trait.ParamType)),
            new Term.Pi(Explicitness.Implicit, Nbe.Quote(ctx.Metas, ctx.Width + 1, dictType),
                Nbe.Quote(ctx.Metas, ctx.Width + 2, opType)));
        return (new Term.Lam(new Term.Lam(new Term.Dot(new Term.Var(0), name))), ctx.Eval(type));
    }

    /// <summary>
    /// An application whose function next takes hidden dictionaries: each waits on
    /// a placeholder until the explicit argument is checked (solving the type
    /// arguments), then is resolved. An argument still unknown takes a meta; a known
    /// one with no impl is an error. Past a dictionary, the traversal continues
    /// through the trait's own hidden sig parameters - they sit between the
    /// dictionary and the explicit argument (`[Dict(F)] -> [A : Type] -> [B : Type]
    /// -> …`) - each taking a meta exactly as <see cref="InsertImplicitArgs"/>
    /// supplies one. With no dictionary among them the ordinary path applies.
    /// </summary>
    private static (Term, Value)? InferApWithPendingDicts(Context ctx, Term fn, Value fnType, Syntax argSyntax)
    {
        // Every hidden binder before the explicit argument, in application order: a
        // dictionary fills in with its evidence after the argument is checked, any
        // other binder with the meta it takes now.
        var hidden = new List<(Value.VTraitDict? Dict, Term? Arg)>();
        var type = fnType;
        while (ctx.Force(type) is Value.VPi { Explicitness: Explicitness.Implicit } pi)
        {
            if (ctx.Force(pi.Domain) is Value.VTraitDict dict)
            {
                hidden.Add((dict, null));
                type = Nbe.ApplyClosure(ctx.Metas, pi.Codomain, ctx.RawMeta());
            }
            else if (hidden.Any(h => h.Dict is not null))
            {
                var meta = FreshMeta(ctx);
                hidden.Add((null, meta));
                type = Nbe.ApplyClosure(ctx.Metas, pi.Codomain, ctx.Eval(meta));
            }
            else break;
        }
        if (!hidden.Any(h => h.Dict is not null) || ctx.Force(type) is not Value.VPi { Explicitness: Explicitness.Explicit } explicitPi) return null;

        var (arg, argEffects) = Collecting(ctx, c => Approx(c, () => Check(c, argSyntax, explicitPi.Domain)));
        Emit(ctx, argEffects);
        foreach (var (dict, meta) in hidden)
            fn = new Term.Ap(fn, Explicitness.Implicit, dict is { } d ? Evidence(ctx, d.Decl, d.Args) : meta!);
        // The argument's value is read only when evaluating it is safe: one that
        // performs takes a rigid stand-in as the codomain's argument.
        var result = Nbe.ApplyClosure(ctx.Metas, explicitPi.Codomain, ArgumentValue(ctx, arg, argEffects));
        return (EmitLatent(ctx, explicitPi, new Term.Ap(fn, Explicitness.Explicit, arg)), ctx.Force(result));
    }

    /// <summary>
    /// What <c>open</c> pushes for a public impl of the module: an entry holding the
    /// dictionary, and evidence - unless the same impl is already evidence, since
    /// opening a module twice brings one impl, not two.
    /// </summary>
    private static Context OpenImpl(Context ctx, ModuleEntry.Impl impl, Value module, int index, List<OpenMember> opened)
    {
        var member = new OpenMember.Impl(index, impl.Name);
        var value = Nbe.OpenedImpl(module, member);
        var duplicate = OfferedDict(ctx, impl.DictType) is { } dict && ctx.Evidence.Any(e =>
            ReferenceEquals(e.Trait, dict.Decl)
            && e.Args.Length == dict.Args.Length
            && e.Args.Zip(dict.Args).All(p => Nbe.Convertible(ctx.Metas, ctx.Width, p.First, p.Second))
            && Nbe.Convertible(ctx.Metas, ctx.Width, ctx.Environment[Nbe.LevelToIndex(ctx.Width, e.Level)], value));
        var (after, entry) = ctx.DefineAnonymous(impl.DictType, value);
        opened.Add(member);
        return !duplicate && OfferedDict(after, impl.DictType) is { } d
            ? after.AddEvidence(new TraitEvidence(d.Decl, d.Args, entry.Level, impl.DictType, impl.Vars, impl.Bounds))
            : after;
    }
}
