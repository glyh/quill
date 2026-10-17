// Prints the elaborated Core.term of a program, the way the .NET backend's emitter
// receives it. Docs: docs/wayfinder/topics/dotnet-backend-term-shapes.md.
//
//   quill-dump-term <file.qll> [--expand] [--depth N] [--env N] [--type] [--no-run]
//
//   --expand     print module bindings instead of eliding them
//   --depth N    stop recursing past N (default 60)
//   --env N      print the value of Context.Environment[N] (a de Bruijn index)
//   --type       print the program's type (a Value) only
//   --no-run     skip the Context.Names index and the run
//   --no-values  do not descend into closure environments and values
using Quill.Compiler;
using Quill.Kernel;

var file = args.FirstOrDefault(a => !a.StartsWith("--"));
var expand = args.Contains("--expand");
var noRun = args.Contains("--no-run");
var depth = Arg(args, "--depth") is { } d ? int.Parse(d) : 60;

var source = File.ReadAllText(file!);
var units = new Dictionary<string, string>(StringComparer.Ordinal);
var dir = Path.GetDirectoryName(Path.GetFullPath(file!))!;
var name = Path.GetFileNameWithoutExtension(file!);
foreach (var f in Directory.EnumerateFiles(dir, name + ".unit-*.qll"))
    units[Path.GetFileNameWithoutExtension(f).Substring(name.Length + ".unit-".Length)] = File.ReadAllText(f);

Elaborated program;
try { program = Driver.Elaborate(source, units); }
catch (Exception e) { Console.WriteLine($"ELABORATION FAILED: {e.Message}"); return 1; }

var p = new Printer(expand, depth, !args.Contains("--no-values"), program.Context);

if (args.Contains("--type"))
{
    p.PrintValue("program type", program.Type);
    Console.WriteLine(p.Text);
    return 0;
}

if (args.FirstOrDefault(a => a.StartsWith("--env")) is { } envArg)
{
    var index = int.Parse(envArg[5..].TrimStart('='));
    p.PrintValue($"Environment[{index}]", program.Context.Environment[index]);
    var entry = program.Context.Width - 1 - index;
    foreach (var kv in program.Context.Names)
        if (kv.Value.Level == entry) p.Line($"  named by Context.Names: {kv.Key}");
    foreach (var (open, members) in program.Context.Opened)
        foreach (var (n, e) in members)
            if (e.Level == entry) p.Line($"  named by Context.Opened: {open}.{n}");
    foreach (var kv in program.Context.BaseNames)
        if (kv.Value.Level == entry) p.Line($"  named by Context.BaseNames: {kv.Key}");
    Console.WriteLine(p.Text);
    return 0;
}

p.IndexBaseContext();
p.PrintTerm("program term", program.Term);
p.PrintValue("program type", program.Type);
p.Line($"environment entries: {program.Context.Environment.Count}, context width: {program.Context.Width}");

if (!noRun)
{
    p.Line("--- Context.Names (name : level = env[index] : type) ---");
    foreach (var kv in program.Context.Names.OrderBy(k => k.Value.Level))
        p.Line($"  {kv.Key} : level {kv.Value.Level} = env[{program.Context.Width - 1 - kv.Value.Level}] : {Labels.Short(kv.Value.Type)}");
    p.Line("--- run ---");
    try { p.Line($"VALUE {Driver.Describe(Driver.Run(program))}"); }
    catch (Exception e) { p.Line($"EVAL {e.Message}"); }
}

if (p.Truncated) Console.Error.WriteLine("NOTE: output truncated (visit/line cap)");
Console.WriteLine(p.Text);
return 0;

static string? Arg(string[] args, string flag)
{
    var a = args.FirstOrDefault(x => x.StartsWith(flag));
    if (a is null || a.Length <= flag.Length) return null;
    var v = a.Substring(flag.Length).TrimStart('=');
    return v.Length > 0 ? v : null;
}

/// <summary>Indented pretty-printer for terms, values and decision trees.</summary>
sealed class Printer(bool expand, int maxDepth, bool values, Context context)
{
    private readonly System.Text.StringBuilder _sb = new();
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
    private int _level;

    public string Text => _sb.ToString();

    // Environments nest, so printing every entry of every closure is
    // exponential; these two caps keep a dump readable (and the process alive).
    private int _lines;
    // Breadth explodes too: a closure's environment holds values holding closures.
    // Cap the nodes visited, not just the lines printed.
    private int _visits;
    // The binders the term itself introduces, innermost last, so a `Var i` can be
    // read as a name and a type. Indices past this fall to the program's own
    // Context (the base context, and any open in the term above it).
    private readonly List<(string Name, Value? Type)> _stack = [];
    private readonly Dictionary<int, string> _baseNames = [];
    private readonly Dictionary<int, Value> _baseTypes = [];

    private void Push(string name, Value? type = null) => _stack.Add((name, type));
    private void Pop(int n) => _stack.RemoveRange(_stack.Count - n, n);

    private string Resolve(int index)
    {
        var at = _stack.Count - 1 - index;
        if (at >= 0) return _stack[at].Name + " (term binder)";
        var b = index - _stack.Count;
        return _baseNames.TryGetValue(b, out var nm) ? $"{nm} (Context level {context.Width - 1 - b})" : $"<unresolved index {b}>";
    }

    private const int VisitCap = 60_000;
    public bool Truncated { get; private set; }
    private const int LineCap = 20_000;
    private const int EnvCap = 12;

    public void Line(string s = "")
    {
        if (_lines++ > LineCap) { if (_lines == LineCap + 1) _sb.AppendLine("… line cap reached, output truncated"); return; }
        if (s.Length > 0) _sb.Append(new string(' ', _level * 2));
        _sb.AppendLine(s);
    }

    private void Nest(Action body) { _level++; body(); _level--; }

    private void Label(string label) => Line(label + " =");

    private void Guarded(Action body, params object?[] keys)
    {
        var k = keys.FirstOrDefault(x => x is not null);
        if (_visits++ > VisitCap) { Truncated = true; Line("… visit cap reached"); return; }
        if (_level >= maxDepth) { Line("… depth"); return; }
        if (k is not null && !_seen.Add(k)) { Line("…"); return; }
        try { body(); } finally { if (k is not null) _seen.Remove(k); }
    }

    private void Guarded(Term t) => Guarded(() => Print(t), t);
    private void Guarded(Value v) => Guarded(() => PrintVal(v), v);
    private void Guarded(BindingTerm b) => Guarded(() => PrintBinding(b), b);

    public void IndexBaseContext()
    {
        foreach (var kv in context.Names) _baseNames[context.Width - 1 - kv.Value.Level] = kv.Key;
        foreach (var kv in context.BaseNames) _baseNames[context.Width - 1 - kv.Value.Level] = kv.Key;
        foreach (var (open, members) in context.Opened)
            foreach (var (n, e) in members)
                _baseNames[context.Width - 1 - e.Level] = $"{open}.{n}";
        foreach (var kv in context.Names) _baseTypes[context.Width - 1 - kv.Value.Level] = kv.Value.Type;
    }

    // ---- terms -------------------------------------------------------------

    public void PrintTerm(string label, Term t)
    {
        Label(label);
        Guarded(t);
    }

    private void Print(Term t)
    {
        switch (t)
        {
            case Term.Var v: Line($"Var {v.Index} -> {Resolve(v.Index)}"); return;
            case Term.Lam l:
                Line("Lam");
                Push("#" + _stack.Count);
                Nest(() => Guarded(l.Body));
                Pop(1);
                return;
            case Term.Ap a:
                Line($"Ap ({a.Explicitness})");
                Nest(() => { Label("fn"); Guarded(a.Fn); Label("arg"); Guarded(a.Arg); });
                return;
            case Term.Let l:
                Line("Let");
                Push("#let");
                Nest(() => { Label("type"); Guarded(l.Type); Label("def"); Guarded(l.Def); Label("body"); Guarded(l.Body); });
                Pop(1);
                return;
            case Term.Pi p:
                Line($"Pi ({p.Explicitness}) row={(p.Row.IsPure ? "pure" : $"effects={p.Row.Effects.Length} tails={p.Row.Tails.Length}")}");
                Nest(() => { Label("dom"); Guarded(p.Domain); Label("cod"); Guarded(p.Codomain); });
                return;
            case Term.U: Line("U"); return;
            case Term.Atom a: Line($"Atom {a.Value}"); return;
            case Term.AtomTy at: Line($"AtomTy {at.Ty}"); return;
            case Term.Prod p: Line($"Prod[{p.Items.Length}]"); Nest(() => { foreach (var it in p.Items) Guarded(it); }); return;
            case Term.ProdTy p: Line($"ProdTy[{p.Items.Length}]"); Nest(() => { foreach (var it in p.Items) Guarded(it); }); return;
            case Term.Proj p: Line($"Proj {p.Index}"); Nest(() => { Label("of"); Guarded(p.Of); }); return;
            case Term.Dot d:
                Line($"Dot .{d.Name}");
                Nest(() => { Label("of"); Guarded(d.Of); });
                return;
            case Term.Module m:
                Line($"Module signature={m.Signature} bindings[{m.Bindings.Length}]");
                if (expand) Nest(() => { foreach (var b in m.Bindings) Guarded(b); });
                else Line($"  (elided; names: {string.Join(", ", m.Bindings.Select(BindingName))})");
                return;
            case Term.Open o:
                Line($"Open members=[{string.Join(",", o.Members.Select(OpenName))}]");
                foreach (var m in o.Members) Push(OpenName(m));
                Nest(() => { Label("of"); Guarded(o.Of); Label("body"); Guarded(o.Body); });
                Pop(o.Members.Length);
                return;
            case Term.Imported im:
                Line($"Imported {Labels.Short(im.Value)}");
                if (expand && values) Nest(() => Guarded(im.Value));
                return;
            case Term.Fix f2:
                Line($"Fix members[{f2.Members.Length}] index={f2.Index}");
                Nest(() =>
                {
                    foreach (var m in f2.Members) Push(m.Name);
                    foreach (var m in f2.Members) { Label($"member {m.Name} pure={m.Pure}"); Guarded(m.Body); }
                    Pop(f2.Members.Length);
                });
                return;
            case Term.TraitRef tr: Line($"TraitRef {tr.Decl}"); return;
            case Term.TraitDictTy td:
                Line($"TraitDictTy {td.Decl} args[{td.Args.Length}] ops=[{string.Join(", ", td.Operations.Select(o => o.Name))}]");
                Nest(() => { foreach (var a in td.Args) Guarded(a); foreach (var o in td.Operations) { Label($"op {o.Name}"); Guarded(o.Type); } });
                return;
            case Term.Prim p: Line($"Prim {p.Name}"); return;
            case Term.Meta m: Line($"Meta {m.Id}"); return;
            case Term.InsertedMeta m: Line($"InsertedMeta {m.Id} kinds[{m.EntryKinds.Length}]"); return;
            case Term.Nominal n:
                Line($"Nominal {n.Decl} captures[{n.Captures.Length}] declComplete={n.Decl.IsComplete} ctors=[{string.Join(", ", n.Decl.Constructors.Select(x => x.Name + "/" + x.Payloads.Length))}]");
                if (expand) Nest(() => { foreach (var c in n.Captures) Guarded(c); });
                return;
            case Term.Con c:
                Line($"Con {c.Name} args[{c.Args.Length}]");
                Nest(() => { foreach (var x in c.Args) Guarded(x); Label("of"); Guarded(c.Of); });
                return;
            case Term.Match m:
                Line($"Match handler={m.Handler} effectBranches={m.EffectBranches.Length} patterns=[{string.Join(" | ", m.Patterns.Select(Pat))}]");
                Nest(() =>
                {
                    Label("scrutinee"); Guarded(m.Scrutinee);
                    Label("tree"); PrintTree(m.Tree, 0);
                    for (var i = 0; i < m.Bodies.Length; i++) { Label($"body[{i}]"); Guarded(m.Bodies[i]); }
                    for (var i = 0; i < m.EffectBranches.Length; i++)
                    {
                        var b = m.EffectBranches[i];
                        Label($"effectBranch[{i}] op={b.Op}");
                        Nest(() => { Label("instance"); Guarded(b.Instance); Label("argument tree"); PrintTree(b.Argument, 0); Label("body"); Guarded(b.Body); });
                    }
                });
                return;
            case Term.Struct s:
                Line($"Struct partial={s.Partial} conFields=[{string.Join(", ", s.ConFields.Select(f => f.Name))}]");
                Nest(() => { foreach (var b in s.Bindings) Guarded(b); });
                return;
            case Term.RecordConstruct r:
                Line($"RecordConstruct fields=[{string.Join(", ", r.Fields.Select(f => f.Name))}]");
                Nest(() => { Label("type"); Guarded(r.Type); Label("values"); foreach (var (_, v) in r.Fields) Guarded(v); });
                return;
            case Term.Sig s: Line("Sig"); Nest(() => Guarded(s.Body)); return;
            case Term.RefTy r: Line("RefTy"); Nest(() => { Label("heap"); Guarded(r.Heap); Label("elem"); Guarded(r.Element); }); return;
            case Term.RefNew r: Line("RefNew"); Nest(() => Guarded(r.Arg)); return;
            case Term.RefGet r: Line("RefGet"); Nest(() => Guarded(r.Ref)); return;
            case Term.RefSet r: Line("RefSet"); Nest(() => { Guarded(r.Ref); Guarded(r.Value); }); return;
            case Term.EffectRowTy: Line("EffectRowTy"); return;
            case Term.EffectRowLit r: Line($"EffectRowLit effects[{r.Row.Effects.Length}] tails[{r.Row.Tails.Length}]"); return;
            case Term.EffectDecl e: Line($"EffectDecl {e.Family.Name}#{e.Family.Id} params={e.Family.ParamCount} ops=[{string.Join(",", e.Family.Operations.Select(o => o.Name + " : " + o.Input + " -> " + o.Output))}]"); return;
            case Term.Effect e:
                Line($"Effect {e.Family.Name}#{e.Family.Id} params[{e.Params.Length}]");
                Nest(() => { Label("closed over Environment"); PrintEnv(e.Environment); Label("params"); foreach (var x in e.Params) Guarded(x); });
                return;
            case Term.Perform p:
                Line($"Perform .{p.Op}");
                Nest(() => { Label("instance"); Guarded(p.Instance); Label("arg"); Guarded(p.Arg); });
                return;
            case Term.Tunnel tu:
                Line($"Tunnel named[{tu.Named.Length}] handlers=[{string.Join(",", tu.Handlers)}]");
                Nest(() => { Label("body"); Guarded(tu.Body); });
                return;
            default: Line($"{t.GetType().Name}: {t}"); return;
        }
    }

    private void PrintBinding(BindingTerm b)
    {
        switch (b)
        {
            case BindingTerm.Let l:
                Label($"binding Let {l.Name} ({l.Kind})");
                Nest(() => Guarded(l.Def));
                return;
            case BindingTerm.Impl im:
                Label($"binding Impl {im.Name} ({im.Kind}) vars={im.Vars.Length} bounds=[{string.Join(",", im.Bounds.Select(x => x.Trait.Name + "/" + x.Var))}]");
                Nest(() => { Label("dict type"); Line("  " + Labels.Short(im.DictType)); Label("def"); Guarded(im.Def); });
                return;
            case BindingTerm.Open o:
                Label("binding Open");
                Nest(() => { Label("of"); Guarded(o.Of); Line("members=[" + string.Join(",", o.Members.Select(OpenName)) + "]"); });
                return;
            default: Line($"binding {b.GetType().Name}"); return;
        }
    }

    private static string BindingName(BindingTerm b) => b switch
    {
        BindingTerm.Let l => l.Name,
        BindingTerm.Impl i => "impl:" + i.Name,
        BindingTerm.Open => "open",
        _ => b.GetType().Name,
    };

    private static string OpenName(OpenMember o) => o switch
    {
        OpenMember.Field f => "field " + f.Name,
        OpenMember.Constructor c => "ctor " + c.Name + "/" + c.FormerArity,
        OpenMember.Impl i => "impl " + i.Name,
        _ => o.GetType().Name,
    };

    private static string Pat(CorePattern p) => p switch
    {
        CorePattern.Wild => "_",
        CorePattern.Bind => "bind",
        CorePattern.Atom a => a.Value.ToString(),
        CorePattern.Prod ps => "(" + string.Join(", ", ps.Items.Select(Pat)) + ")",
        CorePattern.Or l2 => Pat(l2.Left) + " | " + Pat(l2.Right),
        CorePattern.Con c => $"{c.Name}/{c.TypeParams}({string.Join(", ", c.Args.Select(Pat))})",
        CorePattern.AtomType a => "atomtype " + a.Ty,
        CorePattern.NominalHead n => $"nominalhead {n.Decl}/{n.Arity}({string.Join(", ", n.Params.Select(Pat))})",
        _ => p.GetType().Name,
    };

    private static string Occ(Occurrence o) => o switch
    {
        Occurrence.Base => "base",
        Occurrence.Child c => Occ(c.Parent) + "." + c.Index,
        Occurrence.Payload c => Occ(c.Parent) + "." + c.Constructor + "[" + c.Index + "]",
        Occurrence.Field c => Occ(c.Parent) + "." + c.Name,
        _ => o.GetType().Name,
    };

    private void PrintTree(DecisionTree? t, int depthGuard)
    {
        if (depthGuard > 40) { Line("…"); return; }
        switch (t)
        {
            case null: Line("null"); return;
            case DecisionTree.Leaf l: Line($"Leaf branch={l.Branch} bindings=[{string.Join(",", l.Bindings.Select(Occ))}]"); return;
            case DecisionTree.Destruct d:
                Line($"Destruct at={Occ(d.At)} cases[{d.Cases.Length}] default={(d.Default is null ? "none" : "yes")}");
                Nest(() =>
                {
                    foreach (var c in d.Cases) { Label($"case {c.Name}"); PrintTree(c.Tree, depthGuard + 1); }
                    if (d.Default is not null) { Label("default"); PrintTree(d.Default, depthGuard + 1); }
                });
                return;
            case DecisionTree.Switch s:
                Line($"Switch at={Occ(s.At)} cases[{s.Cases.Length}]");
                Nest(() =>
                {
                    foreach (var c in s.Cases) { Label($"case {c.Key}"); PrintTree(c.Tree, depthGuard + 1); }
                    Label("default"); PrintTree(s.Default, depthGuard + 1);
                });
                return;
            case DecisionTree.TypeSwitch ts:
                Line($"TypeSwitch at={Occ(ts.At)} cases[{ts.Cases.Length}]");
                Nest(() =>
                {
                    foreach (var c in ts.Cases) { Label($"case {Key(c.Key)}"); PrintTree(c.Tree, depthGuard + 1); }
                    Label("default"); PrintTree(ts.Default, depthGuard + 1);
                });
                return;
            case DecisionTree.Sequential sq:
                Line($"Sequential arms[{sq.Arms.Length}]");
                Nest(() => { foreach (var a in sq.Arms) Line($"  arm {Pat(a)}"); });
                return;
            default: Line(t.GetType().Name); return;
        }
    }

    private static string Key(TypeKey k) => k switch
    {
        TypeKey.Atom a => "TypeKey.Atom " + a.Ty,
        TypeKey.Nominal n => "TypeKey.Nominal " + n.Decl,
        TypeKey.Pi => "TypeKey.Pi",
        TypeKey.U => "TypeKey.U",
        TypeKey.Tuple t => "TypeKey.Tuple/" + t.Arity,
        _ => k.GetType().Name,
    };

    // ---- values ------------------------------------------------------------

    public void PrintValue(string label, Value v)
    {
        Label(label);
        Guarded(v);
    }

    private void PrintEnv(Environment e)
    {
        if (!values) { Line($"Environment[{e.Count}]"); return; }
        Line($"Environment[{e.Count}]");
        var show = Math.Min(e.Count, EnvCap);
        for (var i = 0; i < show; i++) { Label($"[{i}]"); Guarded(e[i]); }
        if (e.Count > show) Line($"… {e.Count - show} more entries");
    }

    private void PrintVal(Value v)
    {
        switch (v)
        {
            case Value.VAtom a: Line($"VAtom {a.Atom}"); return;
            case Value.VAtomTy t: Line($"VAtomTy {t.Ty}"); return;
            case Value.VCon c: Line($"VCon {c.Name} nominal={c.Nominal.Decl}"); return;
            case Value.VNominal n: Line($"VNominal {n.Decl} captures[{n.Captures.Length}]"); return;
            case Value.VLam l: Line("VLam"); Nest(() => PrintClosure(l.Body)); return;
            case Value.VPi p:
                Line($"VPi ({p.Explicitness}) row={(p.Row.Row.IsPure ? "pure" : "effectful")}");
                Nest(() => { Label("dom"); PrintVal(p.Domain); Label("cod closure"); PrintClosure(p.Codomain); });
                return;
            case Value.VU: Line("VU"); return;
            case Value.VProd ps: Line($"VProd[{ps.Items.Length}]"); Nest(() => { foreach (var x in ps.Items) Guarded(x); }); return;
            case Value.VProdTy ps: Line($"VProdTy[{ps.Items.Length}]"); Nest(() => { foreach (var x in ps.Items) Guarded(x); }); return;
            case Value.VModule m:
                Line($"VModule partial={m.Partial} entries[{m.Entries.Length}]");
                Nest(() =>
                {
                    var show = m.Entries.ToList().Take(expand ? int.MaxValue : 8);
                    foreach (var e in show) { var f = (ModuleEntry.Field)e; Line($"  {f.Name} ({f.Kind}) ctor={(f.Constructor is null ? "no" : f.Constructor.Constructor.Name)} : {Labels.Short(f.Value)}"); }
                    if (m.Entries.Length > 8) Line($"  … {m.Entries.Length - 8} more");
                });
                return;
            case Value.VStruct s:
                Line($"VStruct partial={s.Partial} entries[{s.Entries.Length}]");
                Nest(() => { foreach (var e in s.Entries.ToList()) { var f = (ModuleEntry.Field)e; Line($"  {f.Name} ({f.Kind}) : {Labels.Short(f.Value)}"); } });
                return;
            case Value.VRecord r:
                Line($"VRecord type={Labels.Short(r.Type)}");
                Nest(() => { Label("type"); PrintVal(r.Type); foreach (var (n, f) in r.Fields) { Label($"field {n}"); PrintVal(f); } });
                return;
            case Value.VNeutral n2:
                Line($"VNeutral head={n2.Head} frames[{n2.Frames.Length}]");
                Nest(() => { Label("type"); PrintVal(n2.Ty); });
                return;
            case Value.VMeta mi: Line($"VMeta {mi.Id} spine[{mi.Spine.Length}]"); return;
            case Value.VVar lv: Line($"VVar level={lv.Level} spine[{lv.Spine.Length}]"); return;
            case Value.VSig s: Line("VSig"); Nest(() => PrintClosure(s.Body)); return;
            case Value.VRefTy rt: Line($"VRefTy elem={Labels.Short(rt.Element)} heap={Labels.Short(rt.Heap)}"); return;
            case Value.VRef r: Line($"VRef cell#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(r.Cell)}"); return;
            case Value.VCont: Line("VCont"); return;
            case Value.VEffect e: Line($"VEffect {e.Family.Name}#{e.Family.Id} params[{e.Params.Length}]"); return;
            case Value.VEffectRow r: Line($"VEffectRow effects[{r.Effects.Length}] tails[{r.Tails.Length}]"); return;
            default: Line(v.GetType().Name); return;
        }
    }

    private void PrintClosure(Closure c)
    {
        if (!values) { Line($"Closure(Environment[{c.Environment.Count}], {c.Body.GetType().Name})"); return; }
        Label("closure env"); PrintEnv(c.Environment);
        Label("closure body"); Guarded(c.Body);
    }
}