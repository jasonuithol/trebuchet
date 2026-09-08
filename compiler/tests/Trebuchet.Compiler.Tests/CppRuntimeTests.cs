using System.Diagnostics;
using Trebuchet.Compiler.Backends;
using Xunit;

namespace Trebuchet.Compiler.Tests;

/// <summary>Compiles and runs the C++ runtime's own test program with g++.</summary>
public class CppRuntimeTests
{
    [Fact]
    public void RuntimeChecksPass() => Run("");

    /// <summary>The same program with atomic refcounts and locked cells, plus the multi-threaded checks that flag enables.</summary>
    [Fact]
    public void RuntimeChecksPassWithThreads() => Run("-DTREB_THREADS -pthread");

    private static void Run(string extraFlags)
    {
        var runtime = CppEmitter.FindRuntimeDir();
        var source = Path.Combine(runtime, "tests", "runtime_tests.cpp");
        var exe = Path.Combine(Path.GetTempPath(), "treb-rt-" + Guid.NewGuid().ToString("N")[..8]);
        var build = Process.Start(new ProcessStartInfo("g++", $"-std=c++20 -O0 -Wall -Wno-unused {extraFlags} -I\"{runtime}\" \"{source}\" -o \"{exe}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var log = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
        build.WaitForExit();
        Assert.True(build.ExitCode == 0, "g++ failed:\n" + log);
        var run = Process.Start(new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = run.StandardOutput.ReadToEnd() + run.StandardError.ReadToEnd();
        run.WaitForExit();
        Assert.True(run.ExitCode == 0, output);
        Assert.Contains("all runtime checks passed", output);
    }
}
