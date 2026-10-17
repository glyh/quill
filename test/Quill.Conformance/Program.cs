// Runs the shared conformance suite -- the cases/<area>/<name>.qll files under
// test/conformance/cases, compared against their sibling <name>.expect. It is the only place
// a language behaviour is tested, and it began life shared with the OCaml prototype (removed
// 2026-09-25), which is why it lives beside the case files in test/ rather than under
// src/, the implementation tree. See ../conformance/cases/README.md.
using System.Diagnostics;
using Quill.Backend;
using Quill.Compiler;

const string UnitInfix = ".unit-";

// --file <path>: run a single program and print the one-line protocol below. It reuses
// the same value/error shape as a normal case, so a program is judged exactly as the
// suite judges it -- the way to check a suspected bug without adding a case first.
if (args is ["--file", var file])
    return RunFile(file);

// --case <path>: run one conformance case in this process, print PASS/FAIL, exit 0/1.
// The --isolated batch mode spawns it per case: a case that kills the process (a stack
// overflow, which .NET does not let a catch intercept) then costs one result, not the
// whole run -- the property a mutation sweep needs.
if (args is ["--case", var casePath])
{
    var why = RunCase(casePath);
    if (why is null) { Console.WriteLine("PASS"); return 0; }
    Console.WriteLine($"FAIL {why}");
    return 1;
}

// --parity: run every case whose .expect is not "ok" through Backend.Run beside
// Driver.Run and report match / differ / not-ported. It is the backend slice's
// evidence, not a pass/fail gate, so it never changes the default mode's verdict.
if (args.Contains("--parity"))
    return RunParity(CasesRoot([.. args.Where(a => a != "--parity")]));

// prototype-divergences.txt is historical now (the second implementation is gone); the
// cases it lists are ordinary ones, and this runner passes every case in the directory.
var root = CasesRoot(args);
var cases = EnumerateCases(root);
var isolated = args.Contains("--isolated");

// Isolated, every case is a child process and only its exit code and verdict line are
// seen: a crash is one case's failure. In-process, a case's exception is classified
// directly -- fast, but an uncatchable crash would take the run with it.
List<(string Path, string Why)> failures;
if (isolated)
{
    failures = new();
    foreach (var path in cases)
    {
        var (line, exit) = RunChild(path);
        if (exit != 0)
            failures.Add((path, line.Length > 0 ? line : $"child exited {exit}"));
    }
}
else
{
    failures = cases
        .Select(path => (path, why: RunCase(path)))
        .Where(r => r.why is not null)
        .Select(r => (r.path, r.why!))
        .ToList();
}

foreach (var (path, why) in failures)
    Console.WriteLine($"FAIL {Path.GetRelativePath(root, path)}: {why}");
Console.WriteLine(isolated
    ? $"conformance (isolated): {cases.Count} cases, {failures.Count} failed"
    : $"conformance: {cases.Count} cases, {failures.Count} failed");
return failures.Count == 0 ? 0 : 1;

// null when the case passes, otherwise why it did not.
static string? RunCase(string path)
{
    var dir = Path.GetDirectoryName(path)!;
    var name = Path.GetFileNameWithoutExtension(path);
    var expect = File.ReadAllText(Path.Combine(dir, name + ".expect")).Trim();
    var units = UnitSources(dir, name);

    // Elaboration is timeboxed like the run below: a reader or elaborator regression that
    // loops must fail this case by name, not wedge the suite. A timeout is a failure and
    // never satisfies an `error` case, or any non-termination would pass one.
    var elabTimeout = TimeSpan.FromSeconds(60);
    var elaborating = Task.Run(() =>
    {
        try { return (Elaborated: (Elaborated?)Driver.Elaborate(File.ReadAllText(path), units), Error: (Exception?)null); }
        catch (Exception x) { return ((Elaborated?)null, x); }
    });
    if (!elaborating.Wait(elabTimeout))
        return $"elaboration did not finish within {elabTimeout.TotalSeconds}s";
    var (elaboratedOrNull, elabError) = elaborating.Result;
    if (elabError is not null) return CaseJudge.Failure(elabError, expect, "elaboration");
    var elaborated = elaboratedOrNull!;

    if (expect == "ok") return null;

    // An `error` case that elaborates is run too: it may fail at evaluation
    // (cases/README.md). Running has no budget, so a run that does not finish is
    // reported rather than left to hang the suite.
    // ponytail: a timed-out run's thread keeps spinning until the process exits.
    var timeout = TimeSpan.FromSeconds(10);
    var run = Task.Run(() =>
    {
        try { return (Value: (string?)Driver.Describe(Driver.Run(elaborated)), Error: (Exception?)null); }
        // Every exception becomes this case's result, classified below. A filtered
        // catch here would fault the task and let `run.Wait` throw AggregateException:
        // an unhandled crash taking the whole run, and every other case's result, with it.
        catch (Exception x) { return (Value: (string?)null, Error: x); }
    });
    if (!run.Wait(timeout)) return $"did not finish within {timeout.TotalSeconds}s";
    var (got, error) = run.Result;
    if (error is not null) return CaseJudge.Failure(error, expect, "evaluation");
    // A case expecting `error` is never satisfied by a value.
    return expect == "error" ? "expected an error" : got == expect ? null : $"expected {expect}, got {got}";
}

// A case's extra compilation units: <name>.unit-<unit>.qll beside it, each
// importable as "<unit>".
static Dictionary<string, string> UnitSources(string dir, string name)
{
    var prefix = name + UnitInfix;
    return Directory.EnumerateFiles(dir, prefix + "*.qll")
        .ToDictionary(
            f => Path.GetFileNameWithoutExtension(f)[prefix.Length..],
            File.ReadAllText,
            StringComparer.Ordinal);
}

// The cases under a root: every area directory, every .qll that is not a case's extra
// unit (<name>.unit-<unit>.qll), in a stable order.
static List<string> EnumerateCases(string root) =>
    Directory.EnumerateDirectories(root)
        .OrderBy(d => d, StringComparer.Ordinal)
        .SelectMany(area => Directory.EnumerateFiles(area, "*.qll")
            .Where(f => !Path.GetFileName(f).Contains(UnitInfix, StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal))
        .ToList();

// Run one case in a child process and report its verdict line and exit code. The child
// is this same runner invoked with --case, launched the way this process was (dotnet
// <dll>, or the apphost directly).
static (string Line, int Exit) RunChild(string path)
{
    var dll = typeof(Program).Assembly.Location;
    var host = System.Environment.ProcessPath;
    var viaDotNet = host is not null && Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    var start = new ProcessStartInfo(viaDotNet ? host! : dll,
            viaDotNet ? $"\"{dll}\" --case \"{path}\"" : $"--case \"{path}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var child = Process.Start(start)!;
    var line = child.StandardOutput.ReadLine()?.Trim();
    if (!child.WaitForExit(120_000))
    {
        child.Kill();
        return ("child did not finish within 120s", -1);
    }
    var exit = child.ExitCode;
    if (line is null) line = child.StandardError.ReadToEnd().Trim();
    return (line ?? "", exit);
}

// --parity: the backend's coverage beside the interpreter. Each case whose .expect is not
// "ok" is elaborated once and run twice -- Driver.Run and Backend.Run -- and the outcomes
// compared. "not ported" is the backend's deliberate NotImplementedException; a case whose
// elaboration fails is a match, because neither path ever runs. A compile failure is a
// BackendException (an emitter bug), and counts as a differ, never as a language error.
static int RunParity(string root)
{
    int match = 0, differ = 0, notPorted = 0;
    var examples = new List<string>();
    foreach (var path in EnumerateCases(root))
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        if (File.ReadAllText(Path.Combine(dir, name + ".expect")).Trim() == "ok") continue;
        var units = UnitSources(dir, name);
        Elaborated elaborated;
        try { elaborated = Driver.Elaborate(File.ReadAllText(path), units); }
        catch (Exception) { match++; continue; }
        var interpreted = Outcome(() => Driver.Describe(Driver.Run(elaborated)));
        var compiled = Outcome(() => Backend.Run(elaborated));
        if (compiled.Error is NotImplementedException) { notPorted++; continue; }
        if (Same(interpreted, compiled)) { match++; continue; }
        differ++;
        if (examples.Count < 10)
            examples.Add($"{Path.GetRelativePath(root, path)}\n      interpreter: {Show(interpreted)}\n      backend:     {Show(compiled)}");
    }
    foreach (var example in examples) Console.WriteLine($"DIFFER {example}");
    Console.WriteLine($"parity: {match + differ + notPorted} cases, {match} match, {differ} differ, {notPorted} not ported");
    return 0;
}

// One path's outcome: the value it produced, or the exception it threw.
static (string? Value, Exception? Error) Outcome(Func<string> run)
{
    try { return (run(), null); } catch (Exception e) { return (null, e); }
}

// Two outcomes agree when both produced the same value, or both failed as a language
// error (the suite never compares error messages, only that a language error happened).
static bool Same((string? Value, Exception? Error) a, (string? Value, Exception? Error) b) =>
    a.Error is null && b.Error is null ? a.Value == b.Value
    : a.Error is FunException && b.Error is FunException;

static string Show((string? Value, Exception? Error) outcome) =>
    outcome.Error is { } e ? $"{e.GetType().Name}: {e.Message}" : outcome.Value ?? "";

// test/conformance/cases, found by walking up to the repo root, so the runner works from
// anywhere. An argument overrides it; --isolated is a mode flag, not a directory.
static string CasesRoot(string[] args)
{
    var dirArg = args.FirstOrDefault(a => a != "--isolated");
    if (dirArg is not null) return dirArg;
    // Walk up to the repo root. The marker is the cases directory itself, not a build file:
    // the runner used to look for `dune-project`, which the prototype's removal took with it.
    for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
    {
        var cases = Path.Combine(dir, "test", "conformance", "cases");
        if (Directory.Exists(cases)) return cases;
    }
    throw new DirectoryNotFoundException("no test/conformance/cases above the runner; pass the cases directory");
}

// The probe protocol, one line per program (the `--file` mode above):
//   OK           the program elaborates (sibling .expect is "ok", so it is not run)
//   VALUE <s>    Driver.Describe of the result
//   ELAB <msg>   expansion/elaboration failed
//   EVAL <msg>   evaluation failed
//   HANG         the run did not finish within 60s
// The accounting stays honest: an unported path is a failure, an invariant
// failure is a failure, and neither passes a program expecting `error`.
static int RunFile(string path)
{
    var full = Path.GetFullPath(path);
    var dir = Path.GetDirectoryName(full)!;
    var name = Path.GetFileNameWithoutExtension(full);
    var units = UnitSources(dir, name);
    var expect = Path.Combine(dir, name + ".expect");
    var elaboratesOnly = File.Exists(expect) && File.ReadAllText(expect).Trim() == "ok";

    // Same timebox as a case: a looping elaboration is reported, not wedged (--file has
    // no external `timeout 300` in front of it).
    var elabTimeout = TimeSpan.FromSeconds(60);
    var elaborating = Task.Run(() =>
    {
        try { return (Elaborated: (Elaborated?)Driver.Elaborate(File.ReadAllText(full), units), Error: (Exception?)null); }
        catch (Exception e) { return ((Elaborated?)null, e); }
    });
    Elaborated elaborated;
    if (!elaborating.Wait(elabTimeout))
    {
        PrintFile("HANG", $"elaboration did not finish within {elabTimeout.TotalSeconds}s");
        return 0;
    }
    var (elaboratedOrNull, elabError) = elaborating.Result;
    if (elabError is not null)
    {
        PrintFile("ELAB", DescribeFailure(elabError));
        return 0;
    }
    elaborated = elaboratedOrNull!;

    if (elaboratesOnly)
    {
        PrintFile("OK", "");
        return 0;
    }

    var timeout = TimeSpan.FromSeconds(60);
    var run = Task.Run(() =>
    {
        try { return (Value: (string?)Driver.Describe(Driver.Run(elaborated)), Error: (Exception?)null); }
        catch (Exception e) { return (Value: (string?)null, Error: e); }
    });
    if (!run.Wait(timeout))
    {
        PrintFile("HANG", "");
        return 0;
    }
    var (value, error) = run.Result;
    if (error is not null)
    {
        PrintFile("EVAL", DescribeFailure(error));
        return 0;
    }
    PrintFile("VALUE", value ?? "");
    return 0;
}

// The honest failure description for --file: an unported path says so, an
// invariant failure says so, anything unexpected names its type.
static string DescribeFailure(Exception e) => e switch
{
    NotImplementedException => "not ported: " + e.Message,
    FunException => e.Message,
    InvalidOperationException or IndexOutOfRangeException or ArgumentException or UnifyException =>
        $"invariant ({e.GetType().Name}): {e.Message}",
    _ => $"{e.GetType().Name}: {e.Message}",
};

static void PrintFile(string tag, string msg) =>
    Console.WriteLine(msg.Length == 0 ? tag : $"{tag} {OneLine(msg)}");

static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

// The one honest classification of the exception a case threw, shared by the
// elaboration and evaluation paths (their hand-kept lists had already drifted).
// A language error judges against `error`; a path still to be ported always
// fails; a known invariant failure fails as such. Anything else is a hard
// failure naming its type and message: an engine invariant a mutated or
// regressed engine broke. It must not abort the run -- the other cases' results
// are the sweep's data -- and it must never pass any case, least of all one
// expecting `error`. `null` passes the case.
public static class CaseJudge
{
    public static string? Failure(Exception e, string expect, string phase) => e switch
    {
        NotImplementedException x => x.Message,
        FunException x => expect == "error" ? null : $"{phase} failed: {x.Message}",
        Exception x when x is InvalidOperationException or IndexOutOfRangeException
            or ArgumentException or UnifyException =>
            $"invariant failure ({x.GetType().Name}): {x.Message}",
        _ => $"hard failure ({e.GetType().Name}): {e.Message}",
    };
}
