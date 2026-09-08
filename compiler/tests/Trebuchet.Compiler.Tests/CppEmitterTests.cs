using System.Diagnostics;
using Trebuchet.Compiler.Backends;
using Trebuchet.Compiler.Semantics;
using Xunit;

namespace Trebuchet.Compiler.Tests;

/// <summary>
/// Emits a sample to C++, compiles it with g++ together with a hand-written driver, runs
/// it, and compares the printed scenario with what the interpreter produces.
/// </summary>
public class CppEmitterTests
{
    private static string ExamplesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "examples");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("examples directory not found");
    }

    private static (string outDir, string log, int exit) Emit(string sample, string? driver)
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), sample));
        var checker = TypeChecker.CheckWithEffects(modules);
        Assert.Empty(checker.Diagnostics);
        var files = new CppEmitter(modules, checker).Emit();
        var outDir = Path.Combine(Path.GetTempPath(), "treb-cpp-" + sample + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        foreach (var (name, content) in files) File.WriteAllText(Path.Combine(outDir, name), content);
        var source = driver ?? Path.Combine(outDir, "check.cpp");
        if (driver is null) File.WriteAllText(source, "#include \"generated.hpp\"\nint main() { return 0; }\n");
        var runtime = CppEmitter.FindRuntimeDir();
        var psi = new ProcessStartInfo("g++", $"-std=c++20 -O0 -Wno-unused -I\"{runtime}\" -I\"{outDir}\" \"{source}\" -o \"{Path.Combine(outDir, "driver")}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var proc = Process.Start(psi)!;
        var log = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (outDir, log, proc.ExitCode);
    }

    [Fact]
    public void BookingsCompilesAndRunsTheInterpreterScenario()
    {
        var (outDir, log, exit) = Emit("bookings", Path.Combine(ExamplesDir(), "bookings", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = run.StandardOutput.ReadToEnd();
        var err = run.StandardError.ReadToEnd();
        run.WaitForExit();
        Assert.True(run.ExitCode == 0, "driver failed: " + err);
        var expected = """
            first=Ok bk-1
            second=Error Conflict
            past=Error BadRequest
            missing=Error NotFound
            crowd=Error BadRequest
            waiting=Ok bk-6
            confirmed=Ok
            cancelled=Ok
            again=Error Conflict
            promoted=Held
            free=Ok true
            count=2
            page=1/2
            stats=boardroom held=1 cancelled=1 guests=1
            info=2 rooms on test-host

            """.Replace("\r\n", "\n");
        Assert.Equal(expected, output);
    }

    [Fact]
    public void GenericsCompileAndRun()
    {
        var (outDir, log, exit) = Emit("generics", Path.Combine(ExamplesDir(), "generics", "cpp", "driver.cpp"));
        Assert.True(exit == 0, log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().Trim();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.GenericsDemoExpected, output);
    }

    [Fact]
    public void SetBuiltinsCompileAndRun()
    {
        var (outDir, log, exit) = Emit("collections", Path.Combine(ExamplesDir(), "collections", "cpp", "driver.cpp"));
        Assert.True(exit == 0, log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().Trim();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.CollectionsDemoExpected, output);
    }

    [Fact]
    public void GenericsCompileAndRunTwice()
    {
        var (outDir, log, exit) = Emit("generics", Path.Combine(ExamplesDir(), "generics", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().TrimEnd();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.GenericsDemoExpected, output);
    }

    [Fact]
    public void ExternsCallTheHostAndMapExceptions()
    {
        var (outDir, log, exit) = Emit("ffi", Path.Combine(ExamplesDir(), "ffi", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var (present, missing) = InterpreterTests.FfiFiles();
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true, ArgumentList = { present, missing } })!;
        var output = run.StandardOutput.ReadToEnd().TrimEnd();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.FfiDemoExpected, output);
    }

    [Fact]
    public void ResourcesReleaseWhenTheLastReferenceDrops()
    {
        var (outDir, log, exit) = Emit("resources", Path.Combine(ExamplesDir(), "resources", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().TrimEnd();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.ResourcesDemoExpected, output);
    }

    [Fact]
    public void SleepSuspendsOnTheEventLoop()
    {
        var (outDir, log, exit) = Emit("async", Path.Combine(ExamplesDir(), "async", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().TrimEnd();
        run.WaitForExit();
        Assert.Equal("sequential=a, b\nconcurrent=b, a", output.Replace("\r", ""));
    }

    [Fact]
    public void RecursiveUnionsAreBoxed()
    {
        var (outDir, log, exit) = Emit("trees", Path.Combine(ExamplesDir(), "trees", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        Assert.Contains("Box<Expr>", File.ReadAllText(Path.Combine(outDir, "generated.hpp")));
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().Trim();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.TreesDemoExpected, output);
    }

    [Fact]
    public void RootSynthesisesFactoryLambdas()
    {
        var (outDir, log, exit) = Emit("factories", Path.Combine(ExamplesDir(), "factories", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().Trim();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.FactoriesExpected, output);
    }

    [Fact]
    public void SuperviseCatchesPanicsInGeneratedCode()
    {
        var (outDir, log, exit) = Emit("supervision", Path.Combine(ExamplesDir(), "supervision", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().Trim();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.SuperviseExpected, output);
    }

    [Fact]
    public void ShapesOverTypesCompileAndRun()
    {
        var (outDir, log, exit) = Emit("classes", Path.Combine(ExamplesDir(), "classes", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().Trim();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.ClassesExpected, output);
    }

    [Fact]
    public void PatternsCompileAndRun()
    {
        var (outDir, log, exit) = Emit("patterns", Path.Combine(ExamplesDir(), "patterns", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().Trim();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.PatternsExpected, output);
    }

    [Fact]
    public void OrdersAcceptanceScenario()
    {
        var (outDir, log, exit) = Emit("orders", Path.Combine(ExamplesDir(), "orders", "cpp", "driver.cpp"));
        Assert.True(exit == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(Path.Combine(outDir, "driver")) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd().Trim();
        run.WaitForExit();
        Assert.Equal(InterpreterTests.OrdersExpected, output);
    }
}
