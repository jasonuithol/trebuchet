using Trebuchet.Compiler.Syntax;

static int Usage()
{
    Console.Error.WriteLine("""
        treb - Trebuchet compiler (prototype)

        usage:
          treb parse <file|dir>...        parse and report syntax errors
          treb fmt [--write] <file|dir>...  print canonical formatting (or rewrite in place)
          treb tokens <file>              dump the token stream
          treb check <dir>                load all modules and report type errors
          treb effects <dir>              print the inferred effects of every function
          treb emit <dir> --out <outdir> [--host] [--target cpp] [--no-lines]
                                          lower to a C# project against Trebuchet.Runtime;
                                          --host adds IServiceCollection registration per root
                                          --no-lines omits #line directives (stack traces then name the .cs files)
          treb serve <dir> [--root dev] [--api api] [--port 5080]
                                          run an API service from a composition root over HTTP
        """);
    return 2;
}

static IEnumerable<string> Expand(IEnumerable<string> paths)
{
    foreach (var p in paths)
    {
        if (Directory.Exists(p))
        {
            foreach (var f in Directory.EnumerateFiles(p, "*.treb", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                yield return f;
        }
        else yield return p;
    }
}

if (args.Length < 2) return Usage();

var command = args[0];
var write = args.Contains("--write");
var options = new Dictionary<string, string>();
var positional = new List<string>();
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--write" || args[i] == "--host" || args[i] == "--no-lines") continue;
    if (args[i].StartsWith("--") && i + 1 < args.Length) { options[args[i][2..]] = args[++i]; continue; }
    positional.Add(args[i]);
}
if (positional.Count == 0) return Usage();

if (command == "emit")
{
    try
    {
        var modules = Trebuchet.Compiler.Semantics.ModuleSet.Load(positional[0]);
        var checker = Trebuchet.Compiler.Semantics.TypeChecker.CheckWithEffects(modules);
        foreach (var d in checker.Diagnostics) Console.Error.WriteLine(d);
        if (checker.Diagnostics.Count > 0) return 1;
        var outDir = Path.GetFullPath(options.GetValueOrDefault("out", "generated"));
        Directory.CreateDirectory(outDir);
        IReadOnlyDictionary<string, string> generated;
        if (options.GetValueOrDefault("target", "csharp") == "cpp")
        {
            generated = new Trebuchet.Compiler.Backends.CppEmitter(modules, checker).Emit();
            Console.WriteLine($"C++ runtime header: {Trebuchet.Compiler.Backends.CppEmitter.FindRuntimeDir()}/trebuchet.hpp");
        }
        else
        {
            var runtimeProject = Trebuchet.Compiler.Backends.CSharpEmitter.FindRuntimeProject();
            var runtime = runtimeProject is null ? null : Path.GetRelativePath(outDir, runtimeProject);
            if (runtime is null) Console.WriteLine($"Generated.csproj references the Trebuchet.Runtime {Trebuchet.Compiler.Backends.CSharpEmitter.RuntimeVersion} package; add the feed that holds it with 'dotnet nuget add source'");
            generated = new Trebuchet.Compiler.Backends.CSharpEmitter(modules, checker).Emit(runtime, host: args.Contains("--host"), lineDirectives: !args.Contains("--no-lines"));
        }
        foreach (var (name, content) in generated) File.WriteAllText(Path.Combine(outDir, name), content);
        Console.WriteLine($"{generated.Count} file(s) written to {outDir}");
        return 0;
    }
    catch (Exception ex) when (ex is SyntaxException or Trebuchet.Compiler.Semantics.SemanticException)
    {
        Console.Error.WriteLine($"error: {ex}");
        return 1;
    }
}

if (command == "effects")
{
    try
    {
        var modules = Trebuchet.Compiler.Semantics.ModuleSet.Load(positional[0]);
        var checker = Trebuchet.Compiler.Semantics.TypeChecker.CheckWithEffects(modules);
        foreach (var d in checker.Diagnostics) Console.WriteLine(d);
        foreach (var (name, fx) in checker.InferredEffects.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Console.WriteLine($"{name,-60} {(fx.Count == 0 ? "Pure" : string.Join(" ", fx.OrderBy(e => e switch { "Nondet" => 0, "Write" => 1, _ => 2 })))}");
        return checker.Diagnostics.Count == 0 ? 0 : 1;
    }
    catch (Exception ex) when (ex is SyntaxException or Trebuchet.Compiler.Semantics.SemanticException)
    {
        Console.Error.WriteLine($"error: {ex}");
        return 1;
    }
}

if (command == "check")
{
    try
    {
        var modules = Trebuchet.Compiler.Semantics.ModuleSet.Load(positional[0]);
        var diagnostics = Trebuchet.Compiler.Semantics.TypeChecker.Check(modules);
        foreach (var d in diagnostics) Console.WriteLine(d);
        Console.WriteLine(diagnostics.Count == 0
            ? $"{modules.Modules.Count} module(s) checked, no errors"
            : $"{diagnostics.Count} error(s) in {diagnostics.Select(d => d.File).Distinct().Count()} file(s)");
        return diagnostics.Count == 0 ? 0 : 1;
    }
    catch (Exception ex) when (ex is SyntaxException or Trebuchet.Compiler.Semantics.SemanticException)
    {
        Console.Error.WriteLine($"error: {ex}");
        return 1;
    }
}

if (command == "serve")
{
    try
    {
        return treb.Serve.Run(
            positional[0],
            options.GetValueOrDefault("root", "dev"),
            options.GetValueOrDefault("api", "api"),
            int.Parse(options.GetValueOrDefault("port", "5080")));
    }
    catch (Exception ex) when (ex is Trebuchet.Compiler.Runtime.TrebPanic or SyntaxException or Trebuchet.Compiler.Semantics.SemanticException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

var files = Expand(positional).ToList();
var failures = 0;

switch (command)
{
    case "parse":
        foreach (var f in files)
        {
            try
            {
                var ast = Parser.ParseFile(File.ReadAllText(f), f);
                Console.WriteLine($"ok    {f}  ({ast.Decls.Count} declarations)");
            }
            catch (SyntaxException ex)
            {
                failures++;
                Console.WriteLine($"error {ex}");
            }
        }
        Console.WriteLine(failures == 0 ? $"{files.Count} file(s) parsed" : $"{failures} of {files.Count} file(s) failed");
        return failures == 0 ? 0 : 1;

    case "fmt":
        foreach (var f in files)
        {
            try
            {
                var ast = Parser.ParseFile(File.ReadAllText(f), f);
                var text = Printer.Print(ast);
                if (write) File.WriteAllText(f, text);
                else
                {
                    if (files.Count > 1) Console.WriteLine($"// ---- {f}");
                    Console.Write(text);
                }
            }
            catch (SyntaxException ex)
            {
                failures++;
                Console.Error.WriteLine($"error {ex}");
            }
        }
        return failures == 0 ? 0 : 1;

    case "tokens":
        try
        {
            foreach (var t in Lexer.Tokenize(File.ReadAllText(files[0]), files[0]))
                Console.WriteLine($"{t.Start,-8} {t}");
        }
        catch (SyntaxException ex)
        {
            Console.Error.WriteLine($"error {ex}");
            return 1;
        }
        return 0;

    default:
        return Usage();
}
