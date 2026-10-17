using System.Reflection;
using Quill.Compiler;

namespace Quill.Backend;

/// <summary>
/// The .NET backend: emit an elaborated program to IL, run it, and return the
/// printed result. Slice 1a' covers atoms, primitives, Var/Let/Lam/Ap and Fix.
/// </summary>
public static class Backend
{
    public static string Run(Elaborated program)
    {
        var entry = Emitter.EmitProgram(program);
        try
        {
            return Runtime.Describe(entry()!);
        }
        // A language error raised inside emitted code is the same failure the
        // interpreter reports, not a backend bug; unwrap it.
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    public static string Run(string source, IReadOnlyDictionary<string, string> units) =>
        Run(Driver.Elaborate(source, units));
}
