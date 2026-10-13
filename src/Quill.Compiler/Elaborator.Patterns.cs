using System.Collections.Immutable;
using Quill.Kernel;

namespace Quill.Compiler;

public sealed partial record Context
{
    /// <summary>
    /// The type-case refinement a branch is elaborated under: the matched variable's
    /// level, and what the branch reads it as. It lives only on the context
    /// <c>RefineContext</c> derives for that branch, so it can reach
    /// neither a sibling branch nor code after the <c>match</c>.
    /// </summary>
    public (int Level, Value Replacement)? Refinement { get; init; }
}

public static partial class Elaborator
{
    /// <summary>
    /// <c>P {x = p, y}</c>: the scrutinee is a record of <c>P</c>; every named field
    /// exists, none is named twice, and unless the pattern is partial every field
    /// is named. Binders come out ordered by field label - the order the match
    /// compiler visits a record's fields in - so the arm's context lines up with
    /// the decision tree's leaf.
    /// </summary>
    private static (CorePattern, List<(string Name, Value Type)>) ElaborateRecordPattern(Context ctx, Pattern.Record record, Value type)
    {
        var structType = RecordPatternType(ctx, record);
        ctx.Unify(type, structType);

        var declared = structType.Entries.OfType<ModuleEntry.Field>().Where(f => f.Kind == MemberKind.Field).ToList();
        RejectDuplicates(record.Fields.Select(f => f.Name));
        foreach (var (name, _) in record.Fields)
            if (declared.All(d => d.Name != name)) throw new FunException($"unknown record field `{name}`");
        if (!record.Partial)
            foreach (var field in declared)
                if (record.Fields.All(f => f.Name != field.Name)) throw new FunException($"missing record field `{field.Name}`");

        var fields = new List<(string, CorePattern)>();
        var binders = new List<(string Label, List<(string, Value)> Binders)>();
        foreach (var (name, pattern) in record.Fields)
        {
            var (core, fieldBinders) = ElaboratePattern(ctx, pattern, declared.Last(d => d.Name == name).Value);
            fields.Add((name, core));
            binders.Add((name, fieldBinders));
        }
        return (new CorePattern.Record([.. fields], record.Partial),
                [.. binders.OrderBy(b => b.Label, StringComparer.Ordinal).SelectMany(b => b.Binders)]);
    }

    /// <summary>The struct type a record pattern's head names: the type of the struct value it resolves to.</summary>
    private static Value.VStruct RecordPatternType(Context ctx, Pattern.Record record)
    {
        var (_, typeType) = Infer(ctx, record.Type);
        return ctx.Force(typeType) switch
        {
            Value.VStruct structType => structType,
            _ => throw new FunException("record pattern fields must follow a struct"),
        };
    }

    /// <summary>A struct's constructor fields, by label, the last of a label winning (I3).</summary>
    private static Value? FieldType(Value.VStruct structType, string name) =>
        structType.Entries.OfType<ModuleEntry.Field>().LastOrDefault(f => f.Name == name && f.Kind == MemberKind.Field)?.Value;

    // ---- pattern synonyms -----------------------------------------------------

    /// <summary>
    /// <c>pattern Flip(a, b) = Pt(b, a)</c>: the right-hand side elaborated once,
    /// where it is written, each parameter marked where its binder sits. Every
    /// binder of the right-hand side is a parameter, and every parameter is bound
    /// exactly once (both alternatives of an or-pattern count as one).
    /// </summary>
    private static (Term, Value) InferPatternSynonym(Context ctx, Syntax.PatternSynonym synonym)
    {
        // A synonym use nested in this right-hand side instantiates type parameters
        // that this declaration generalizes in turn; they are its own parameters, not
        // uses that must resolve here, so they are not reported.
        var registeredBefore = ctx.Metas.SynonymTypeParams.Count;
        var names = synonym.Params.Select(p => p.Name).ToList();
        if (names.Distinct().Count() != names.Count) throw new FunException("a pattern synonym names a parameter twice");

        var rhs = MarkSynonymParams(synonym.Rhs, names);
        // A synonym's right-hand side may name a type-case head only as a member
        // of a binder sealed at a generative module (E11); a type former is
        // refused, as the prototype refuses it.
        RejectFormerHeads(ctx, rhs);
        var scrutineeType = RefineScrutineeType(ctx, ctx.RawMeta(), [new MatchBranch(rhs, synonym)]);

        var (core, binders) = ElaboratePattern(ctx, rhs, scrutineeType);
        // The prototype refines a synonym over a type-case head to the instance
        // the head names (not just "a type"), so a use against that instance
        // unifies with the definition's scrutinee. A sealed projection that does
        // not force to a nominal here leaves the scrutinee a type.
        if (ctx.Force(scrutineeType) is Value.VU && rhs is Pattern.Con { Head: var head } && TypeHead(ctx, head) is { Value: var headType }
            && ctx.Force(headType) is Value.VNominal)
            scrutineeType = headType;

        var parameters = binders.Select(b => (Index: int.Parse(b.Name[SynonymParamPrefix.Length..]), b.Type)).ToList();
        foreach (var (name, index) in names.Select((n, i) => (n, i)))
        {
            var count = parameters.Count(p => p.Index == index);
            if (count != 1)
                throw new FunException(count == 0
                    ? $"a pattern synonym's parameter `{Label(name)}` is not bound by its pattern"
                    : $"a pattern synonym's parameter `{Label(name)}` is bound twice");
        }
        // The ruling: what the right-hand side cannot determine is generalized,
        // the step a `let` performs - the unknown metas become the synonym's type
        // parameters, instantiated where it is used. The right-hand side is still
        // elaborated (and checked) here, once, at its declaration.
        var generalized = new List<int>();
        CollectSynonymMetas(ctx, ctx.Force(scrutineeType), generalized);
        foreach (var (_, type) in parameters) CollectSynonymMetas(ctx, type, generalized);

        (Term, Value) value = (new Term.PatternSynonym(new Value.VPatternSynonym(
                    names.Count, generalized.Count, [.. generalized],
                    core, ctx.Force(scrutineeType), [.. parameters],
                    ctx.Environment, ctx.Width)),
                Value.VU.Instance);
        ctx.Metas.SynonymTypeParams.RemoveRange(registeredBefore, ctx.Metas.SynonymTypeParams.Count - registeredBefore);
        return value;
    }

    /// <summary>
    /// The local rule: a name the pattern itself binds (an explicit binder
    /// position) is a reference in the rest of that pattern, so each bare mention
    /// of it in <paramref name="pattern"/> becomes a <see cref="Pattern.Pin"/> to
    /// the binder the case rule would otherwise re-bind. A nested boundary that
    /// rebinds the name stops the walk.
    /// </summary>
    private static Pattern ReferenceBound(Pattern pattern, string name) => pattern switch
    {
        Pattern.Bind b when b.Name.Name == name => new Pattern.Pin(new Syntax.Var(b.Name)),
        Pattern.Prod p => p with { Items = [.. p.Items.Select(i => ReferenceBound(i, name))] },
        Pattern.Or o => o with { Left = ReferenceBound(o.Left, name), Right = ReferenceBound(o.Right, name) },
        Pattern.Con c => c with { Args = [.. c.Args.Select(i => ReferenceBound(i, name))] },
        Pattern.Record r => r with { Fields = [.. r.Fields.Select(f => (f.Name, ReferenceBound(f.Pattern, name)))] },
        Pattern.StructType s => s with { Fields = [.. s.Fields.Select(f => (f.Name, ReferenceBound(f.Pattern, name)))] },
        Pattern.Arrow a => a with { Domain = ReferenceBound(a.Domain, name), Codomain = ReferenceBound(a.Codomain, name) },
        Pattern.ImplicitArrow a when a.Binder.Name != name => a with { Codomain = ReferenceBound(a.Codomain, name) },
        _ => pattern,
    };

    private const string SynonymParamPrefix = "synonym-param#";

    /// <summary>A synonym's right-hand side with each parameter's binder marked by its position; any other binder is an error.</summary>
    private static Pattern MarkSynonymParams(Pattern pattern, List<string> names) => pattern switch
    {
        Pattern.Bind b => names.IndexOf(b.Name.Name) is var i and >= 0
            ? new Pattern.SynonymParam(i)
            : throw new FunException($"a pattern synonym's pattern binds `{Label(b.Name.Name)}`, which is not a parameter"),
        Pattern.Prod p => p with { Items = [.. p.Items.Select(x => MarkSynonymParams(x, names))] },
        Pattern.Or o => o with { Left = MarkSynonymParams(o.Left, names), Right = MarkSynonymParams(o.Right, names) },
        Pattern.Con c => c with { Args = [.. c.Args.Select(x => MarkSynonymParams(x, names))] },
        Pattern.Record r => r with { Fields = [.. r.Fields.Select(f => (f.Name, MarkSynonymParams(f.Pattern, names)))] },
        Pattern.StructType s => s with { Fields = [.. s.Fields.Select(f => (f.Name, MarkSynonymParams(f.Pattern, names)))] },
        Pattern.Arrow a => a with { Domain = MarkSynonymParams(a.Domain, names), Codomain = MarkSynonymParams(a.Codomain, names) },
        Pattern.ImplicitArrow a => a with { Codomain = MarkSynonymParams(a.Codomain, names) },
        _ => pattern,
    };

    /// <summary>
    /// The path a synonym use's head names, with the implicit applications that
    /// supply its type parameters peeled off.
    /// </summary>
    private static Syntax SynonymBaseHead(Syntax head) =>
        head is Syntax.Ap { Explicitness: Explicitness.Implicit, Fn: var fn } ? SynonymBaseHead(fn) : head;

    /// <summary>The types a synonym use supplies for its type parameters, in the order written.</summary>
    private static List<Syntax> SynonymSupply(Syntax head) => head switch
    {
        Syntax.Ap { Explicitness: Explicitness.Implicit, Fn: var fn, Arg: var arg } => [.. SynonymSupply(fn), arg],
        _ => [],
    };

    /// <summary>
    /// The pattern synonym a pattern head resolves to - through its binder, an
    /// open, or a member path, like any other name - or null when it resolves
    /// to something else.
    /// </summary>
    private static Value.VPatternSynonym? SynonymAt(Context ctx, Syntax head)
    {
        head = SynonymBaseHead(head);
        return head switch
        {
            Syntax.Var v => ctx.Force(ctx.Environment[ctx.Locate(v.Id.Name).Index]) as Value.VPatternSynonym,
            Syntax.OpenChoice c => ctx.Force(ctx.Environment[ctx.LocateChoice(c.Name.Name, c.Opens, c.Fallback).Index]) as Value.VPatternSynonym,
            Syntax.FieldAccess => ctx.Force(ctx.Eval(Infer(ctx, head).Item1)) as Value.VPatternSynonym,
            _ => null,
        };
    }

    /// <summary>
    /// A synonym's generalized type parameters as this use's fresh metas: each
    /// stored meta becomes a fresh meta here, so one declaration is instantiated
    /// once per use. The template is read back and evaluated under the
    /// definition's environment, so the names it captures stay the definition's
    /// (hygiene); only the generalized types come from the use. A use may supply
    /// the leading ones, as it supplies a call's implicit arguments; each supplied
    /// type solves its fresh meta, and each parameter the use leaves out - and
    /// nothing else determines - is reported where the use's scope ends.
    /// </summary>
    private static (Value ScrutineeType, List<(int Index, Value Type)> Params) InstantiateSynonym(
        Context ctx, Value.VPatternSynonym synonym, List<Syntax>? supply = null)
    {
        supply ??= [];
        if (supply.Count > synonym.TypeParams)
            throw new FunException($"this pattern synonym takes {synonym.TypeParams} type parameters, the pattern supplies {supply.Count}");

        if (synonym.TypeParams == 0)
            return (synonym.ScrutineeType, [.. synonym.Params.Select(p => (p.Index, p.Type))]);

        var fresh = new Dictionary<int, int>();
        for (var i = 0; i < synonym.TypeParams; i++)
        {
            var id = ctx.Metas.Fresh();
            fresh[synonym.Generalized[i]] = id;
            ctx.Metas.SynonymTypeParams.Add(id);
            if (i < supply.Count) ctx.Metas.Solve(id, ctx.Eval(Check(ctx, supply[i], Value.VU.Instance)));
        }

        Term Instantiate(Term term) => term.Map((t, _) =>
            t is Term.Meta m && fresh.TryGetValue(m.Id, out var replacement)
                ? new Term.Meta(replacement)
                : null);

        Value Read(Value value) =>
            Nbe.Eval(ctx.Metas, synonym.Env, Instantiate(Nbe.Quote(ctx.Metas, synonym.Width, value)));

        return (Read(synonym.ScrutineeType),
                [.. synonym.Params.Select(p => (p.Index, Read(p.Type)))]);
    }

    /// <summary>
    /// <c>Flip(first, second)</c>: the synonym's generalized types instantiate as
    /// its implicit type parameters - a use may supply them, as it supplies a call's
    /// (<c>M.Two[I64, Bool](x, b)</c>) - and each argument is matched against its
    /// parameter's type and put where that parameter sits. Binders come out in the
    /// order the parameters sit in the scrutinee, as the arm's context needs.
    /// </summary>
    private static (CorePattern, List<(string Name, Value Type)>) ElaborateSynonymUse(
        Context ctx, Pattern.Con use, Value.VPatternSynonym synonym, Value type)
    {
        if (use.Args.Length != synonym.Arity)
            throw new FunException($"this pattern synonym takes {synonym.Arity} arguments, the pattern gives {use.Args.Length}");
        var (scrutineeType, parameters) = InstantiateSynonym(ctx, synonym, SynonymSupply(use.Head));
        ctx.Unify(type, scrutineeType);

        var args = new CorePattern[synonym.Arity];
        var binders = new List<(string, Value)>[synonym.Arity];
        foreach (var (index, paramType) in parameters)
            (args[index], binders[index]) = ElaboratePattern(ctx, use.Args[index], paramType);

        return (FillSynonymParams(synonym.Rhs, args), [.. parameters.SelectMany(p => binders[p.Index])]);
    }

    /// <summary>
    /// A synonym's right-hand side may not name a type former as a type-case
    /// head - <c>pattern IsOpt(a) = Option(a)</c> is refused, the prototype's
    /// <c>UnknownConstructor</c>. Only a sealed binder's member (E11) carries a
    /// head term through a template.
    /// </summary>
    private static void RejectFormerHeads(Context ctx, Pattern pattern)
    {
        switch (pattern)
        {
            case Pattern.Con c:
                // A use of another synonym is not a type former; one that supplied its
                // type parameters reads as an application, which this must not see.
                if (c.Head is not Syntax.Ap { Explicitness: Explicitness.Implicit }
                    && TypeHead(ctx, c.Head) is { } && SealedDecl(ctx, c.Head) is null)
                    throw new FunException($"unknown constructor `{HeadLabel(c.Head)}`");
                foreach (var arg in c.Args) RejectFormerHeads(ctx, arg);
                break;
            case Pattern.Prod p:
                foreach (var item in p.Items) RejectFormerHeads(ctx, item);
                break;
            case Pattern.Or o:
                RejectFormerHeads(ctx, o.Left);
                RejectFormerHeads(ctx, o.Right);
                break;
            case Pattern.Record r:
                foreach (var (_, field) in r.Fields) RejectFormerHeads(ctx, field);
                break;
            case Pattern.StructType s:
                foreach (var (_, field) in s.Fields) RejectFormerHeads(ctx, field);
                break;
            case Pattern.Arrow a:
                RejectFormerHeads(ctx, a.Domain);
                RejectFormerHeads(ctx, a.Codomain);
                break;
            case Pattern.ImplicitArrow a:
                RejectFormerHeads(ctx, a.Codomain);
                break;
        }
    }

    /// <summary>The written name a pattern head was spelled with, for error messages.</summary>
    private static string HeadLabel(Syntax head) => SynonymBaseHead(head) switch
    {
        Syntax.Var v => Label(v.Id.Name),
        Syntax.OpenChoice c => Label(c.Name.Name),
        Syntax.FieldAccess f => f.Field,
        _ => "type",
    };

    private static CorePattern FillSynonymParams(CorePattern pattern, CorePattern[] args) => pattern switch
    {
        CorePattern.SynonymParam p => args[p.Index],
        CorePattern.NominalHead h => h with { Params = [.. h.Params.Select(x => FillSynonymParams(x, args))] },
        CorePattern.Prod p => p with { Items = [.. p.Items.Select(x => FillSynonymParams(x, args))] },
        CorePattern.Or o => o with { Left = FillSynonymParams(o.Left, args), Right = FillSynonymParams(o.Right, args) },
        CorePattern.Con c => c with { Args = [.. c.Args.Select(x => FillSynonymParams(x, args))] },
        CorePattern.Record r => r with { Fields = [.. r.Fields.Select(f => (f.Name, FillSynonymParams(f.Pattern, args)))] },
        CorePattern.StructType s => s with { Fields = [.. s.Fields.Select(f => (f.Name, FillSynonymParams(f.Pattern, args)))] },
        CorePattern.Arrow a => a with { Domain = FillSynonymParams(a.Domain, args), Codomain = FillSynonymParams(a.Codomain, args) },
        CorePattern.ImplicitArrow a => a with { Codomain = FillSynonymParams(a.Codomain, args) },
        _ => pattern,
    };

    /// <summary>Whether a type still holds an unsolved meta anywhere its shape reveals; a shape it cannot see into counts as holding one.</summary>
    private static bool HasMeta(Context ctx, Value value) => ctx.Force(value) switch
    {
        Value.VMeta => true,
        Value.VU or Value.VAtomTy or Value.VAtom => false,
        Value.VNominal n => n.Captures.Any(c => HasMeta(ctx, c)),
        Value.VProdTy p => p.Items.Any(i => HasMeta(ctx, i)),
        Value.VProd p => p.Items.Any(i => HasMeta(ctx, i)),
        Value.VStruct s => s.Entries.OfType<ModuleEntry.Field>().Any(f => HasMeta(ctx, f.Value)),
        Value.VVar v => v.Spine.Any(a => HasMeta(ctx, a)),
        _ => true,
    };

    /// <summary>The unsolved metas a synonym's scrutinee and parameter types mention, each once, first-seen first.</summary>
    private static void CollectSynonymMetas(Context ctx, Value value, List<int> seen)
    {
        switch (ctx.Force(value))
        {
            case Value.VMeta { Spine.IsEmpty: true } m:
                if (!seen.Contains(m.Id)) seen.Add(m.Id);
                return;
            case Value.VMeta m:
                foreach (var a in m.Spine) CollectSynonymMetas(ctx, a, seen);
                return;
            case Value.VU or Value.VAtomTy or Value.VAtom: return;
            case Value.VNominal n:
                foreach (var c in n.Captures) CollectSynonymMetas(ctx, c, seen);
                return;
            case Value.VProdTy p:
                foreach (var i in p.Items) CollectSynonymMetas(ctx, i, seen);
                return;
            case Value.VProd p:
                foreach (var i in p.Items) CollectSynonymMetas(ctx, i, seen);
                return;
            case Value.VStruct s:
                foreach (var f in s.Entries.OfType<ModuleEntry.Field>()) CollectSynonymMetas(ctx, f.Value, seen);
                return;
            case Value.VRef r:
                // A cell's identity is a stamp, never generalizable; its content
                // is an ordinary value and may still mention a meta.
                CollectSynonymMetas(ctx, r.Cell.Value, seen);
                return;
            case Value.VVar v:
                foreach (var a in v.Spine) CollectSynonymMetas(ctx, a, seen);
                return;
            case Value.VPi pi:
            {
                var binder = new Value.VVar(ctx.Width, []);
                CollectSynonymMetas(ctx, pi.Domain, seen);
                foreach (var e in Nbe.EvalRowClosure(ctx.Metas, pi.Row, binder).Effects) CollectSynonymMetas(ctx, e, seen);
                CollectSynonymMetas(ctx, Nbe.ApplyClosure(ctx.Metas, pi.Codomain, binder), seen);
                return;
            }
            case Value.VRefTy r:
                CollectSynonymMetas(ctx, r.Heap, seen);
                CollectSynonymMetas(ctx, r.Element, seen);
                return;
            case Value.VNeutral n:
                // A stuck neutral is a head plus a spine: the head is a variable
                // or a rigid atom and contributes no metas; the spine's arguments
                // may still mention one.
                foreach (var f in n.Frames)
                    if (f is Frame.FApp a) CollectSynonymMetas(ctx, a.Arg, seen);
                return;
            case Value.VEffectRowTy:
                return;
            default:
                throw new NotImplementedException($"not ported yet: generalising a pattern synonym over a {ctx.Force(value).GetType().Name}");
        }
    }

    // ---- type-case ------------------------------------------------------------

    /// <summary><c>struct { x : p; _ }</c>: the scrutinee is a type; each field's pattern matches a type.</summary>
    private static (CorePattern, List<(string Name, Value Type)>) ElaborateStructTypePattern(Context ctx, Pattern.StructType pattern, Value type)
    {
        if (ctx.Force(type) is not Value.VStruct) ctx.Unify(type, Value.VU.Instance);
        RejectDuplicates(pattern.Fields.Select(f => f.Name));

        var fields = new List<(string, CorePattern)>();
        var binders = new List<(string Label, List<(string, Value)> Binders)>();
        foreach (var (name, field) in pattern.Fields)
        {
            var (core, fieldBinders) = ElaboratePattern(ctx, field, Value.VU.Instance);
            fields.Add((name, core));
            binders.Add((name, fieldBinders));
        }
        return (new CorePattern.StructType([.. fields], pattern.Partial),
                [.. binders.OrderBy(b => b.Label, StringComparer.Ordinal).SelectMany(b => b.Binders)]);
    }

    /// <summary>
    /// <c>Option(p)</c> against a type: the nominal the head names - a nominal type,
    /// or a type former applied to one type per parameter - each parameter
    /// matched by its pattern.
    /// </summary>
    private static (CorePattern, List<(string Name, Value Type)>) ElaborateNominalHeadPattern(
        Context ctx, Pattern.Con pattern, (Term Head, Value Value, NominalDecl Decl, int Arity) head)
    {
        var (term, _, decl, arity) = head;
        if (pattern.Args.Length != arity)
            throw new FunException($"this type takes {arity} parameters, the pattern gives {pattern.Args.Length}");

        var parameters = new List<CorePattern>();
        var binders = new List<(string, Value)>();
        foreach (var arg in pattern.Args)
        {
            var (core, argBinders) = ElaboratePattern(ctx, arg, Value.VU.Instance);
            parameters.Add(core);
            binders.AddRange(argBinders);
        }
        return (new CorePattern.NominalHead(decl, term, arity, [.. parameters]) { HeadWidth = ctx.Width }, binders);
    }

    /// <summary>
    /// A pattern head in a type-case: the tuple former (<c>Tuple(2, a, b)</c>), a
    /// nominal type, an existing term of type <c>Type</c> (a reference, the same
    /// thing as a pin), or a refusal naming the offending term.
    /// </summary>
    private static (CorePattern, List<(string Name, Value Type)>) ElaborateTypeCaseHead(Context ctx, Pattern.Con pattern)
    {
        if (TupleFormerArity(ctx, pattern) is int arity)
            return ElaborateTupleTypePattern(ctx, pattern.Args.Skip(1), arity);

        if (TypeHead(ctx, pattern.Head) is { } head)
            return ElaborateNominalHeadPattern(ctx, pattern, head);

        // A head naming an existing term of type `Type` is a reference - the same
        // internal thing as `^name` (a pattern-binders-are-lowercase ruling), and
        // what makes `Option(A)` with an enclosing `A` writable.
        var (term, headType) = Infer(ctx, pattern.Head);
        if (pattern.Args.IsEmpty && ctx.Force(headType) is Value.VU)
            return (new CorePattern.Pin(term) { Width = ctx.Width }, []);

        // An applied head that names no type former is the argument fault (`Option(Foo(1))`),
        // and the message says which term; a bare head that names no type is the other fault.
        var label = HeadLabel(pattern.Head);
        throw new FunException(pattern.Args.IsEmpty
            ? $"`{label}` is not a type or nominal in scope"
            : $"`{label}` must name a type in a type-case");
    }

    /// <summary>A tuple type pattern: <c>(a, b)</c> and <c>Tuple(2, a, b)</c> are one form, each component a type pattern.</summary>
    private static (CorePattern, List<(string Name, Value Type)>) ElaborateTupleTypePattern(
        Context ctx, IEnumerable<Pattern> items, int? expected)
    {
        var patterns = new List<CorePattern>();
        var binders = new List<(string, Value)>();
        foreach (var item in items)
        {
            var (core, itemBinders) = ElaboratePattern(ctx, item, Value.VU.Instance);
            patterns.Add(core);
            binders.AddRange(itemBinders);
        }
        if (expected is int n && patterns.Count != n)
            throw new FunException($"Tuple({n}, …) takes {n} component types, the pattern gives {patterns.Count}");
        return (new CorePattern.TupleType([.. patterns]), binders);
    }

    /// <summary>The component count of a <c>Tuple(n, …)</c> head pattern, or null when the head is no tuple former.</summary>
    private static int? TupleFormerArity(Context ctx, Pattern.Con pattern)
    {
        if (pattern.Args.Length == 0 || pattern.Args[0] is not Pattern.Atom { Value: Atom.I64 n }) return null;
        if (!NamesTupleFormer(ctx, pattern.Head)) return null;
        if (n.Value < 0) throw new FunException("Tuple: the number of components is negative");
        return (int)n.Value;
    }

    /// <summary>Whether a bare head names the <c>Tuple</c> primitive, by the value it resolves to.</summary>
    private static bool NamesTupleFormer(Context ctx, Syntax head)
    {
        if (head is not (Syntax.Var or Syntax.OpenChoice)) return false;
        try { return ctx.Force(ctx.Eval(Infer(ctx, head).Item1)) is Value.VNeutral { Head: Head.HPrim { Name: "Tuple" } }; }
        catch (FunException) { return false; }
    }

    /// <summary>
    /// What a pattern head names when it names a type: its term, the value it denotes -
    /// the nominal instance, or the projection on a sealed binder a generative module
    /// minted - the declaration, and how many parameters it takes. Any function reducing
    /// to a nominal qualifies - an alias is as good as its name. Null when the head names
    /// no type.
    /// </summary>
    private static (Term Head, Value Value, NominalDecl Decl, int Arity)? TypeHead(Context ctx, Syntax head)
    {
        var (term, type) = Infer(ctx, head);
        var value = ctx.Eval(term);
        var arity = 0;
        type = ctx.Force(type);
        while (type is Value.VPi pi)
        {
            var arg = ctx.RawMeta();
            (value, type, arity) = (Nbe.Apply(ctx.Metas, value, arg), ctx.Force(Nbe.ApplyClosure(ctx.Metas, pi.Codomain, arg)), arity + 1);
        }
        if (type is not Value.VU) return null;
        if (ctx.Force(value) is Value.VNominal nominal) return (term, value, nominal.Decl, arity);
        // A member of a binder sealed at a generative module (E11): the value is a
        // projection, and the sealing context records the declaration behind it.
        return SealedDecl(ctx, head) is { } decl ? (term, value, decl, arity) : null;
    }

    /// <summary>
    /// What a path names when it is one member of a sealed binder (E11): the sealing
    /// context of that binder, by member label. Null for any other path.
    /// </summary>
    private static NominalDecl? SealedDecl(Context ctx, Syntax head)
    {
        if (head is not Syntax.FieldAccess { Of: var of, Field: var member }) return null;
        var level = of switch
        {
            Syntax.Var v => ctx.Names.TryGetValue(v.Id.Name, out var bound) ? bound.Level : null,
            Syntax.OpenChoice c => ChoiceLevel(ctx, c),
            _ => null,
        };
        return level is int l && ctx.Sealed.TryGetValue(l, out var sealedMembers) && sealedMembers.TryGetValue(member, out var decl)
            ? decl
            : null;
    }

    private static int? ChoiceLevel(Context ctx, Syntax.OpenChoice c)
    {
        foreach (var label in c.Opens)
            if (ctx.Opened.TryGetValue(label, out var members) && members.TryGetValue(c.Name.Name, out var entry))
                return entry.Level;
        if (c.Fallback is not null && ctx.Names.TryGetValue(c.Fallback, out var fallback)) return fallback.Level;
        return ctx.BaseNames.TryGetValue(c.Name.Name, out var based) ? based.Level : null;
    }

    // ---- refinement -----------------------------------------------------------

    /// <summary>
    /// The level of the type variable a match refines: a scrutinee of type
    /// <c>Type</c> that evaluates to a bare variable. Null for anything else.
    /// </summary>
    // The target is the variable the scrutinee *evaluates to*, not a name that
    // merely denotes one: an alias (`U = T`) evaluates to its variable, and only then
    // can a branch see through it. A scrutinee evaluating to anything else - a
    // constructor type, a stuck neutral - refines nothing.
    private static int? RefinementTarget(Context ctx, Term scrutinee, Value scrutineeType) =>
        ctx.Force(scrutineeType) is Value.VU
        && ctx.Force(ctx.Eval(scrutinee)) is Value.VVar { Spine.Length: 0 } target
            ? target.Level
            : null;

    /// <summary>The type a branch's pattern pins the matched type to, or null when it pins none.</summary>
    private static Value? RefinementOf(Context ctx, Pattern pattern) => pattern switch
    {
        Pattern.AtomType t => new Value.VAtomTy(t.Ty),
        // A universe branch refines the matched type variable to `Type`.
        Pattern.Universe => Value.VU.Instance,
        Pattern.Or o => RefinementOf(ctx, o.Left) ?? RefinementOf(ctx, o.Right),
        // A synonym use is not a type head, whether or not it supplies its types.
        Pattern.Con c when SynonymAt(ctx, c.Head) is null => TypeHead(ctx, c.Head)?.Value,
        _ => null,
    };

    /// <summary>
    /// The context inside a type-case branch: every entry at or after the matched
    /// type variable reads it as <paramref name="replacement"/>. Entries before it
    /// have types over a context it is not part of.
    /// </summary>
    // Only the entries that can mention the variable are rewritten, each once
    // per branch - the rule, not the prototype's walk over every value.
    // `BaseNames` is left alone: its entries' types are closed, so nothing in them
    // can mention the variable. Every other channel an entry can live in is rewritten
    // - the opened members, the resume entry, the self methods, the constructor
    // entries, and each evidence entry - so a branch sees the matched type the same
    // way however the name it reads reaches its entry.
    private static Context RefineContext(Context ctx, int level, Value replacement) => ctx with
    {
        Names = ctx.Names.ToImmutableDictionary(n => n.Key, n => Refined(ctx, level, replacement, n.Value)),
        SelfEntry = ctx.SelfEntry is { } self ? Refined(ctx, level, replacement, self) : null,
        ResumeEntry = ctx.ResumeEntry is { } resume ? Refined(ctx, level, replacement, resume) : null,
        Opened = ctx.Opened.ToImmutableDictionary(o => o.Key, o => o.Value.ToImmutableDictionary(m => m.Key, m => Refined(ctx, level, replacement, m.Value))),
        SelfMethods = ctx.SelfMethods.ToImmutableDictionary(m => m.Key, m => Substitute(ctx, level, replacement, m.Value)),
        ConstructorEntries = ctx.ConstructorEntries.ToImmutableDictionary(c => c.Key, c => c.Key < level
            ? c.Value
            : (Substitute(ctx, level, replacement, c.Value.Type), Substitute(ctx, level, replacement, c.Value.TypeType), c.Value.Constructor)),
        Evidence = ctx.Evidence.Select(e => RefinedEvidence(ctx, level, replacement, e)).ToImmutableList(),
        Refinement = (level, replacement),
    };

    /// <summary>The evidence entry with the matched variable at <paramref name="level"/> read as <paramref name="replacement"/>: what the Evidence channel writes.</summary>
    private static TraitEvidence RefinedEvidence(Context ctx, int level, Value replacement, TraitEvidence e) => e with
    {
        Args = [.. e.Args.Select(a => Substitute(ctx, level, replacement, a))],
        Type = Substitute(ctx, level, replacement, e.Type),
    };

    /// <summary>The entry with the bound variable at <paramref name="level"/> read as <paramref name="replacement"/>.</summary>
    private static Entry Refined(Context ctx, int level, Value replacement, Entry entry) =>
        entry.Level < level ? entry : entry with { Type = Substitute(ctx, level, replacement, entry.Type) };

    /// <summary>A value with the bound variable at <paramref name="level"/> read as <paramref name="replacement"/>.</summary>
    private static Value Substitute(Context ctx, int level, Value replacement, Value value) =>
        Nbe.Eval(ctx.Metas, ctx.Environment.Replace(ctx.Width - 1 - level, replacement), Nbe.Quote(ctx.Metas, ctx.Width, value));
}
