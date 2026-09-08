using Trebuchet.Compiler.Syntax;
using Xunit;

namespace Trebuchet.Compiler.Tests;

/// <summary>
/// Every .treb file under examples/ must parse, and printing it must reproduce the
/// source exactly, comments included, once blank lines are stripped. Printing the reparsed
/// output must be a fixed point.
/// </summary>
public class RoundTripTests
{
    public static IEnumerable<object[]> ExampleFiles()
    {
        var dir = FindExamplesDir();
        foreach (var f in Directory.EnumerateFiles(dir, "*.treb", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            yield return new object[] { Path.GetRelativePath(dir, f) };
    }

    private static string FindExamplesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "examples");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("examples directory not found above test output");
    }

    private static string Normalize(string src)
    {
        var lines = src.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines) + "\n";
    }

    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"') inString = !inString;
            if (!inString && line[i] == '/' && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void PrintReproducesSource(string relative)
    {
        var path = Path.Combine(FindExamplesDir(), relative);
        var source = File.ReadAllText(path);
        var ast = Parser.ParseFile(source, path);
        var printed = Printer.Print(ast);
        Assert.Equal(Normalize(source), Normalize(printed));
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void PrintIsAFixedPoint(string relative)
    {
        var path = Path.Combine(FindExamplesDir(), relative);
        var once = Printer.Print(Parser.ParseFile(File.ReadAllText(path), path));
        var twice = Printer.Print(Parser.ParseFile(once, path));
        Assert.Equal(once, twice);
    }
}
