using Quill.Compiler;
using Quill.Kernel;


/// <summary>One-line labels for values, so the environment index is readable.</summary>
static class Labels
{
    public static string Short(Value v) => v switch
    {
        Value.VAtom a => $"atom {a.Atom}",
        Value.VAtomTy t => $"atomty {t.Ty}",
        Value.VCon c => $"con {c.Name} of {c.Nominal.Decl} captures={c.Nominal.Captures.Length}",
        Value.VNominal n => $"nominal {n.Decl} captures={n.Captures.Length} ctors=[{string.Join(",", n.Decl.Constructors.Select(x => x.Name + "/" + x.Payloads.Length))}]",
        Value.VLam => "VLam",
        Value.VPi => "VPi",
        Value.VU => "VU",
        Value.VProd ps => $"VProd[{ps.Items.Length}]",
        Value.VProdTy ps => $"VProdTy[{ps.Items.Length}]",
        Value.VModule ms => $"VModule[{ms.Entries.Length}] partial={ms.Partial}",
        Value.VStruct ss => $"VStruct[{ss.Entries.Length}] partial={ss.Partial}",
        Value.VRecord r => $"VRecord type={Short(r.Type)}",
        Value.VNeutral n2 => $"VNeutral ty={Short(n2.Ty)} head={n2.Head} frames={n2.Frames.Length}",
        Value.VVar lv => $"VVar level={lv.Level}",
        Value.VCont => "VCont",
        Value.VRef => "VRef",
        Value.VRefTy rt => $"VRefTy elem={Short(rt.Element)}",
        Value.VEffect e => $"VEffect {e.Family.Name}#{e.Family.Id} params={e.Params.Length}",
        Value.VEffectRow r2 => $"VEffectRow effects={r2.Effects.Length} tails={r2.Tails.Length}",
        Value.VSig => "VSig",
        _ => v.GetType().Name,
    };
}

