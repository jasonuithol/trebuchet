using Trebuchet.Compiler.Semantics;
using Trebuchet.Compiler.Testing;
using Xunit;

namespace Trebuchet.Compiler.Tests;

public class PropertyTests
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

    [Fact]
    public void TheSamplePropertiesHold()
    {
        var runner = new PropertyRunner(ModuleSet.Load(Path.Combine(ExamplesDir(), "properties")));
        Assert.Empty(runner.Diagnostics);
        var outcomes = runner.RunAll(100);
        Assert.Equal(7, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Passed, PropertyRunner.Report(new[] { o })));
    }

    [Fact]
    public void AFailingPropertyIsShrunkToASmallCounterexample()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            module t
            fn prop_short(xs: Vector[Int]) -> Bool ! Pure
              length(xs) < 3
            fn prop_smallPositive(n: Int) -> Bool ! Pure
              n < 10
            record Pair(a: Int, b: Int)
            fn prop_pairs(p: Pair) -> Bool ! Pure
              p.a + p.b != 7
            fn helper(x: Int) -> Int ! Pure
              x
            """) });
        var runner = new PropertyRunner(modules);
        Assert.Empty(runner.Diagnostics);
        var outcomes = runner.RunAll(200);
        Assert.Equal(3, outcomes.Count);
        var shortOne = outcomes.Single(o => o.Name == "prop_short");
        Assert.False(shortOne.Passed);
        Assert.Equal("xs = [0, 0, 0]", shortOne.Counterexample);
        var positive = outcomes.Single(o => o.Name == "prop_smallPositive");
        Assert.Equal("n = 10", positive.Counterexample);
        var pairs = outcomes.Single(o => o.Name == "prop_pairs");
        Assert.False(pairs.Passed);
        Assert.Contains("Pair(", pairs.Counterexample);
    }
}
