using Quill.Compiler;

namespace Quill.Backend;

/// <summary>
/// The helpers emitted code calls: I64 arithmetic with the interpreter's
/// semantics (<see cref="Primitives"/>), the recursion depth guard, and
/// <see cref="Describe"/>. Nothing here is a value representation -- a quill
/// I64 is a C# <c>long</c>, and this class is only the operations on it.
/// </summary>
public static class Runtime
{
    /// <summary>
    /// The self-imposed recursion ceiling. The CLR dies with an uncatchable stack
    /// overflow at ~250k trivial frames (docs/wayfinder/topics/dotnet-backend.md), so the
    /// guard must sit well under that; emitted frames are larger than the probe's.
    /// </summary>
    // shortcut: an implementation constant, not a language rule; whether it is one is
    // undecided (docs/wayfinder/topics/dotnet-backend.md "Open").
    public const int MaxDepth = 10_000;

    [ThreadStatic] private static int _depth;

    /// <summary>Enters one emitted function call, failing as a quill error at the ceiling.</summary>
    public static void Enter()
    {
        if (++_depth > MaxDepth)
        {
            _depth--;
            throw new FunException("stack depth limit exceeded");
        }
    }

    /// <summary>Leaves one emitted function call.</summary>
    public static void Exit() => _depth--;

    /// <summary>Calls a function value of unknown C# shape: the object tier's application.</summary>
    public static object Apply(object fn, object arg) => ((Func<object, object>)fn)(arg);

    /// <summary>
    /// What a program produced, as far as the conformance suite may observe it:
    /// an <c>I64</c> as its digits, a constructor as its name. Anything else is a
    /// debug form no case may depend on. The I64 and constructor cases mirror
    /// <see cref="Driver.Describe"/>; the rest is the backend's own, and slice 1a
    /// produces no constructors.
    /// </summary>
    public static string Describe(object value) => value switch
    {
        long n => n.ToString(),
        _ => value.GetType().Name,
    };

    // ---- I64, exactly as Primitives.cs reduces it ---------------------------

    public static long Add(long a, long b)
    {
        var r = unchecked(a + b);
        if (((a ^ r) & (b ^ r)) < 0) throw new FunException("integer overflow in +");
        return r;
    }

    public static long Sub(long a, long b)
    {
        var r = unchecked(a - b);
        if (((a ^ b) & (a ^ r)) < 0) throw new FunException("integer overflow in -");
        return r;
    }

    public static long Mul(long a, long b)
    {
        var high = Math.BigMul(a, b, out var low);
        if (high != low >> 63) throw new FunException("integer overflow in *");
        return low;
    }

    /// <summary>Division by zero is an error, and so is <c>MinValue / -1</c>.</summary>
    public static long Div(long a, long b)
    {
        if (b == 0) throw new FunException("division by zero");
        if (b == -1)
        {
            if (a == long.MinValue) throw new FunException("integer overflow in /");
            return -a;
        }
        return a / b;
    }

    /// <summary>Division by zero is an error; <c>MinValue % -1</c> is 0, where the host would throw.</summary>
    public static long Mod(long a, long b)
    {
        if (b == 0) throw new FunException("division by zero");
        if (b == -1) return 0;
        return a % b;
    }

    // Predicates return I64 1/0: Bool is a library enum, never an atom.
    public static long Eq(long a, long b) => a == b ? 1L : 0L;
    public static long Ne(long a, long b) => a != b ? 1L : 0L;
    public static long Lt(long a, long b) => a < b ? 1L : 0L;
    public static long Gt(long a, long b) => a > b ? 1L : 0L;
    public static long Le(long a, long b) => a <= b ? 1L : 0L;
    public static long Ge(long a, long b) => a >= b ? 1L : 0L;
}
