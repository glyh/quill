using Quill.Compiler;

namespace Quill.Backend;

/// <summary>
/// The .NET backend: elaborate a program, emit IL, run it, and return the
/// printed result. Slice 1a' covers atoms, primitives, Var/Let/Lam/Ap and Fix.
/// </summary>
public static class Backend
{
    public static string Run(Elaborated program) =>
        throw new NotImplementedException("not ported yet: backend");

    public static string Run(string source, IReadOnlyDictionary<string, string> units) =>
        Run(Driver.Elaborate(source, units));
}
