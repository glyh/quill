// The `quill` CLI. `quill run <file>` compiles a program to a .NET assembly
// through the backend and prints what it produced; `quill build` is not yet.
using Quill.Backend;

if (args is ["run", var file])
{
    try
    {
        var full = Path.GetFullPath(file);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileNameWithoutExtension(full);
        // The runner's convention: <name>.unit-<unit>.qll beside a case is importable as "<unit>".
        var prefix = name + ".unit-";
        var units = Directory.EnumerateFiles(dir, prefix + "*.qll")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f)[prefix.Length..], File.ReadAllText);
        Console.WriteLine(Backend.Run(File.ReadAllText(full), units));
        return 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"quill: {e.Message}");
        return 1;
    }
}

Console.Error.WriteLine("usage: quill run <file>");
return 1;
