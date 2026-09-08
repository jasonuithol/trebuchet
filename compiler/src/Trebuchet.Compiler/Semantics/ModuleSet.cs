using Trebuchet.Compiler.Syntax;

namespace Trebuchet.Compiler.Semantics;

public sealed class SemanticException : Exception
{
    public Position Position { get; }
    public string? File { get; }

    public SemanticException(Position position, string message, string? file = null) : base(message)
    {
        Position = position;
        File = file;
    }

    public override string ToString() =>
        File is null ? $"{Position}: {Message}" : $"{File}:{Position}: {Message}";
}

public sealed class Module
{
    public string Name { get; }
    public string File { get; }
    public SourceFile Ast { get; }
    public IReadOnlyList<Module> Imports { get; internal set; } = Array.Empty<Module>();

    /// <summary>Last segment of the module path, used for qualified references like <c>handlers.request</c>.</summary>
    public string ShortName => Name.Contains('.') ? Name[(Name.LastIndexOf('.') + 1)..] : Name;

    public Module(string name, string file, SourceFile ast)
    {
        Name = name;
        File = file;
        Ast = ast;
    }

    public IEnumerable<Decl> Decls => Ast.Decls;
}

/// <summary>All modules of a program, loaded from a directory tree of .treb files.</summary>
public sealed class ModuleSet
{
    private readonly Dictionary<string, Module> _byName = new();

    public IReadOnlyCollection<Module> Modules => _byName.Values;

    public Module? Find(string name) => _byName.GetValueOrDefault(name);

    public static ModuleSet Load(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.treb", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);
        return Load(files.Select(f => (f, System.IO.File.ReadAllText(f))));
    }

    public static ModuleSet Load(IEnumerable<(string file, string source)> sources)
    {
        var set = new ModuleSet();
        foreach (var (file, source) in sources)
        {
            var ast = Parser.ParseFile(source, file);
            var name = ast.Module?.ToString() ?? Path.GetFileNameWithoutExtension(file);
            if (set._byName.TryGetValue(name, out var existing))
                throw new SemanticException(ast.Pos, $"module '{name}' is declared in both {existing.File} and {file}", file);
            set._byName[name] = new Module(name, file, ast);
        }
        foreach (var m in set._byName.Values)
        {
            var imports = new List<Module>();
            foreach (var u in m.Ast.Uses)
            {
                var target = set.Find(u.Name.ToString())
                    ?? throw new SemanticException(u.Pos, $"module '{u.Name}' not found (used by '{m.Name}')", m.File);
                imports.Add(target);
            }
            m.Imports = imports;
        }
        return set;
    }

    /// <summary>Finds a root declaration by name anywhere in the program.</summary>
    public (Module module, RootDecl root)? FindRoot(string name)
    {
        foreach (var m in _byName.Values)
            foreach (var d in m.Decls)
                if (d is RootDecl r && r.Name == name) return (m, r);
        return null;
    }
}
