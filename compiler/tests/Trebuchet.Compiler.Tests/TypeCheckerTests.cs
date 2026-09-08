using Trebuchet.Compiler.Syntax;
using Trebuchet.Compiler.Semantics;
using Xunit;

namespace Trebuchet.Compiler.Tests;

public class TypeCheckerTests
{
    [Fact]
    public void PrivateDeclarationsDoNotCrossModules()
    {
        var modules = ModuleSet.Load(new[] {
            ("lib.treb", """
                module lib
                private record Secret
                  n: Int
                private fn hidden(x: Int) -> Int ! Pure
                  x + 1
                fn shown(x: Int) -> Int ! Pure
                  hidden(x) + Secret(1).n
                service Counter(start: Int)
                  private fn bump(n: Int) -> Int ! Pure
                    n + 1
                  fn next() -> Int ! Pure
                    bump(start)
                """),
            ("app.treb", """
                module app
                use lib
                fn ok(x: Int) -> Int ! Pure
                  shown(x) + Counter(1).next()
                fn bad(x: Int) -> Int ! Pure
                  hidden(x) + lib.hidden(x) + Counter(1).bump(x)
                """) });
        var checker = TypeChecker.CheckWithEffects(modules);
        var messages = checker.Diagnostics.Select(d => d.Message).ToList();
        Assert.Contains(messages, m => m.Contains("unknown name 'hidden'"));
        Assert.Contains(messages, m => m.Contains("private method of service Counter"));
        Assert.DoesNotContain(messages, m => m.Contains("shown") || m.Contains("next"));
        Assert.All(checker.Diagnostics, d => Assert.EndsWith("app.treb", d.File));
    }

    [Fact]
    public void LazyCombinatorsRejectSuspendingFunctions()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            fn slow(n: Int) -> Int
              sleep(n)
              n
            fn bad() -> Seq[Int] ! Pure
              map(Seq.range(0, 3), \n -> slow(n))
            """) });
        var checker = TypeChecker.CheckWithEffects(modules);
        Assert.Contains(checker.Diagnostics, d => d.Message.Contains("lazy 'map'") && d.Message.Contains("Suspend"));
    }

    [Fact]
    public void PropagationOnOptionNeedsAnOptionReturn()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            fn good(xs: Vector[Int]) -> Option[Int] ! Pure
              x = first(xs)?
              Some(x + 1)
            fn bad(xs: Vector[Int]) -> Result[Int, String] ! Pure
              x = first(xs)?
              ok(x)
            """) });
        var checker = TypeChecker.CheckWithEffects(modules);
        var d = Assert.Single(checker.Diagnostics);
        Assert.Contains("returns Result[Int, String] rather than an Option", d.Message);
    }

    [Fact]
    public void ConstraintsAreCheckedAtCallSitesAndComparisons()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            record Point(x: Int, y: Int)
            fn bigger[T](a: T, b: T) -> Bool ! Pure
              a > b
            fn sorted() -> Vector[Point] ! Pure
              sort([Point(1, 2)])
            fn fine[T: Ord](a: T, b: T) -> Bool ! Pure
              a > b
            fn numbers() -> Vector[Int] ! Pure
              sort([3, 1, 2])
            """) });
        var checker = TypeChecker.CheckWithEffects(modules);
        var messages = checker.Diagnostics.Select(d => d.Message).ToList();
        Assert.Contains(messages, m => m.Contains("'>' on T needs 'T: Ord'"));
        Assert.Contains(messages, m => m.Contains("no instance of Ord for Point"));
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void GuardedArmsDoNotCountTowardsExhaustiveness()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            fn sign(n: Option[Int]) -> String ! Pure
              match n
                Some(x) if x > 0 => "positive"
                None => "none"
            fn pair(p: (Int, String)) -> String ! Pure
              (n, s) = p
              s + toString(n)
            fn bad(xs: Vector[Int]) -> Int ! Pure
              [x, ...rest] = xs
              x
            """) });
        var checker = TypeChecker.CheckWithEffects(modules);
        var messages = checker.Diagnostics.Select(d => d.Message).ToList();
        Assert.Contains(messages, m => m.Contains("not exhaustive; missing Some"));
        Assert.Contains(messages, m => m.Contains("this pattern can fail to match"));
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void SuperviseIsRejectedInAFn()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            fn risky(n: Int) -> Int ! Pure
              n
            fn quiet(n: Int) -> Result[Int, Panic] ! Pure
              supervise risky(n)
            handler loud(n: Int) -> Result[Int, Panic]
              supervise risky(n)
            """) });
        var checker = TypeChecker.CheckWithEffects(modules);
        var d = Assert.Single(checker.Diagnostics);
        Assert.Contains("only allowed in a handler or a service method", d.Message);
        Assert.Equal(4, d.Pos.Line);
    }

    [Fact]
    public void RecursiveGenericUnionInstantiatesWithoutLooping()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            union Tree[T]
              Leaf(value: T)
              Node(left: Tree[T], right: Tree[T])
            record Chain[T]
              value: T
              next: Option[Chain[T]]
            fn depth[T](t: Tree[T]) -> Int ! Pure
              match t
                Leaf(_) => 1
                Node(l, r) => 1 + depth(l) + depth(r)
            fn chainLength[T](c: Chain[T]) -> Int ! Pure
              match c.next
                Some(rest) => 1 + chainLength(rest)
                None => 1
            """) });
        var checker = TypeChecker.CheckWithEffects(modules);
        Assert.Empty(checker.Diagnostics);
        // the recursive call inside a generic body instantiates with the enclosing T, not a fresh variable
        var recursive = checker.CallTypeArgs.Single(kv => kv.Key.Callee is NameExpr { Name: "chainLength" });
        Assert.IsType<ParamT>(Unifier.Prune(recursive.Value[0]));
    }

    private static IReadOnlyList<Diagnostic> Check(string src) =>
        TypeChecker.Check(ModuleSet.Load(new[] { ("t.treb", src) }));

    private static void AssertError(string src, string expectedFragment)
    {
        var diags = Check(src);
        Assert.True(diags.Count > 0, "expected an error but the program checked clean");
        Assert.Contains(diags, d => d.Message.Contains(expectedFragment));
    }

    private static void AssertClean(string src)
    {
        var diags = Check(src);
        Assert.True(diags.Count == 0, "unexpected errors:\n" + string.Join("\n", diags));
    }

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

    [Theory]
    [InlineData("bookings")]
    [InlineData("orders")]
    public void SamplesCheckClean(string sample)
    {
        var diags = TypeChecker.Check(ModuleSet.Load(Path.Combine(ExamplesDir(), sample)));
        Assert.True(diags.Count == 0, string.Join("\n", diags));
    }

    [Fact]
    public void UnknownTypeAndName() 
    {
        AssertError("fn f(x: Nope) -> Int\n  1\n", "unknown type 'Nope'");
        AssertError("fn f() -> Int\n  y\n", "unknown name 'y'");
    }

    [Fact]
    public void ReturnTypeMismatch() =>
        AssertError("fn f() -> Int\n  \"s\"\n", "expected Int but found String");

    [Fact]
    public void ArgumentCountAndType()
    {
        AssertError("fn g(a: Int) -> Int\n  a\nfn f() -> Int\n  g(1, 2)\n", "takes 1 argument(s) but 2 were given");
        AssertError("fn g(a: Int) -> Int\n  a\nfn f() -> Int\n  g(\"x\")\n", "expected Int but found String");
    }

    [Fact]
    public void RecordsAreDeeplyImmutable()
    {
        AssertError("record R\n  c: Cell[Int]\n", "contains mutable state");
        AssertError("entity E(n: Int)\nrecord R\n  e: E\n", "contains mutable state");
        AssertClean("entity E\n  c: Cell[Int]\n");
        AssertClean("record Inner(n: Int)\nrecord R\n  items: Vector[Inner]\n");
    }

    [Fact]
    public void MatchMustBeExhaustive()
    {
        var src = "union S\n  A\n  B(n: Int)\n  C\nfn f(s: S) -> Int\n  match s\n    A => 1\n    B(n) => n\n";
        AssertError(src, "not exhaustive; missing C");
        AssertClean(src.Replace("    B(n) => n\n", "    B(n) => n\n    _ => 0\n"));
        AssertError("fn f(o: Option[Int]) -> Int\n  match o\n    Some(x) => x\n", "missing None");
    }

    [Fact]
    public void PatternArityIsChecked() =>
        AssertError("union S\n  B(n: Int, m: Int)\nfn f(s: S) -> Int\n  match s\n    B(n) => n\n", "has 1 binder(s) but the variant has 2 field(s)");

    [Fact]
    public void FnCannotWrite()
    {
        AssertError("fn f(c: Cell[Int]) -> Unit\n  c.set(1)\n", "'f' is a fn and cannot write: it calls 'set' at 2:3, which has the Write effect");
        AssertError("fn g(c: Cell[Int]) -> Unit\n  c.set(1)\nfn f(c: Cell[Int]) -> Unit\n  g(c)\n", "'f' is a fn and cannot write: it calls 'g' at 4:3, whose inferred effects include Write");
        AssertError("fn f() -> Int ! Write\n  1\n", "a fn cannot declare the Write effect");
        AssertClean("handler h(c: Cell[Int]) -> Unit\n  c.set(1)\n");
        AssertClean("service S(c: Cell[Int])\n  fn m() -> Unit\n    c.set(1)\n");
        // handlers that only build events are pure, and a fn may call them
        AssertClean("record C(x: Int)\nhandler h(c: C) -> Int\n  c.x\nfn f(c: C) -> Int\n  h(c)\n");
    }

    [Fact]
    public void DeclaredEffectsAreAnUpperBound()
    {
        AssertError("fn f() -> String ! Pure\n  uuid()\n", "'f' is declared ! Pure but its body has the Nondet effect: it calls 'uuid' at 2:3");
        AssertError("fn g() -> String\n  uuid()\nfn f() -> String ! Pure\n  g()\n", "'f' is declared ! Pure but its body has the Nondet effect: it calls 'g' at 4:3, whose inferred effects include Nondet");
        AssertClean("fn f() -> Int ! Nondet Suspend\n  1\n");
        AssertClean("fn g() -> Int\n  1\nfn f() -> Int ! Pure\n  g()\n");
    }

    [Fact]
    public void EffectsFlowThroughHigherOrderBuiltins()
    {
        AssertError("fn f(xs: Vector[Int], c: Cell[Int]) -> Vector[Int] ! Pure\n  map(xs, \\x -> x + c.get())\n", "it calls 'map' at 2:3 with a lambda that has the Nondet effect");
        AssertClean("fn f(xs: Vector[Int]) -> Vector[Int] ! Pure\n  map(xs, \\x -> x + 1)\n");
        AssertError("fn g(x: Int) -> Int\n  x + uuid().length\nfn f(xs: Vector[Int]) -> Vector[Int] ! Pure\n  map(xs, g)\n", "it calls 'map' at 4:3 with an argument that has the Nondet effect");
    }

    [Fact]
    public void LambdasMustFitDeclaredFunctionTypes()
    {
        AssertError("fn mk(c: Cell[Int]) -> fn() -> Unit ! Nondet\n  \\-> c.set(1)\n", "this lambda has effects Write but only Nondet are allowed");
        AssertClean("fn mk(c: Cell[Int]) -> fn() -> Unit ! Nondet Write\n  \\-> c.set(1)\n");
        AssertError("fn g(c: Cell[Int]) -> Unit\n  c.set(1)\nservice S(f: fn(Cell[Int]) -> Unit ! Nondet)\n  fn m(c: Cell[Int]) -> Unit\n    f(c)\nroot main\n  f: g\n  s: S\n", "'g' has effects Write but only Nondet are allowed");
    }

    [Fact]
    public void InitialisersMustBePure() =>
        AssertError("record P\n  n: Int\n    init => value + uuid().length\n", "init of P.n must be pure but has the Nondet effect");

    [Fact]
    public void ShapeMembersBoundInferredMethods()
    {
        var shape = "shape Store\n  fn put(x: Int) -> Unit ! Nondet\n";
        AssertError(shape + "service Mem(c: Cell[Int])\n  fn put(x: Int) -> Unit\n    c.set(x)\nservice Uses(store: Store)\n  fn f() -> Unit\n    store.put(1)\nroot main\n  c: Cell.new(0)\n  store: Mem\n  uses: Uses\n", "'put' has effects Write but only Nondet are allowed");
    }

    [Fact]
    public void CallersAreChargedDeclaredEffectsNotBodies()
    {
        var checker = TypeChecker.CheckWithEffects(ModuleSet.Load(new[] { ("t.treb", "fn g() -> Int ! Nondet\n  1\nfn f() -> Int\n  g()\n") }));
        Assert.Empty(checker.Diagnostics);
        Assert.Equal(new[] { "Nondet" }, checker.InferredEffects["t.f"]);
        Assert.Empty(checker.InferredEffects["t.g"]);
    }

    [Fact]
    public void PropagateNeedsResultOnBothSides()
    {
        AssertError("fn f(x: Int) -> Result[Int, String]\n  y = x?\n  ok(y)\n", "'?' needs a Result or an Option but found Int");
        AssertError("fn g() -> Result[Int, String]\n  ok(1)\nfn f() -> Int\n  g()?\n", "returns Int rather than a Result");
        AssertError("record E1(m: String)\nrecord E2(m: String)\nfn g() -> Result[Int, E1]\n  ok(1)\nfn f() -> Result[Int, E2]\n  v = g()?\n  ok(v)\n", "would propagate an error of type E1");
        AssertClean("fn g() -> Result[Int, String]\n  ok(1)\nfn f() -> Result[Int, String]\n  v = g()?\n  ok(v + 1)\n");
    }

    [Fact]
    public void WithChecksPathsAndTypes()
    {
        AssertError("record A(n: Int)\nfn f(a: A) -> A\n  a with { nope: 1 }\n", "A has no field 'nope'");
        AssertError("record A(n: Int)\nfn f(a: A) -> A\n  a with { n: \"s\" }\n", "expected Int but found String");
        AssertClean("record B(city: String)\nrecord A(b: B)\nfn f(a: A) -> A\n  a with\n    b.city: \"x\"\n");
    }

    [Fact]
    public void VariantsUnifyWithTheirUnion() =>
        AssertClean("union E\n  A(n: Int)\n  B\nfn f(x: Int) -> Result[Int, E]\n  if x > 0 then error(E.A(x)) else error(E.B)\n");

    [Fact]
    public void LambdasInferParametersFromContext()
    {
        AssertClean("fn f(xs: Vector[Int]) -> Vector[Int]\n  map(xs, \\x -> x + 1)\n");
        AssertClean("fn f(xs: Vector[Int]) -> Int\n  fold(xs, 0)\n    \\acc, x -> acc + x\n");
        AssertError("fn f(xs: Vector[Int]) -> Vector[String]\n  map(xs, \\x -> x + 1)\n", "expected Vector[String] but found Vector[Int]");
        AssertError("fn f(xs: Vector[Int]) -> Vector[Int]\n  map(xs, \\x, y -> x)\n", "takes 2 parameter(s) but a function of 1 is expected");
    }

    [Fact]
    public void OverloadsResolveByReceiver()
    {
        AssertClean("fn f(c: Cell[Int], m: Map[String, Int]) -> Int\n  c.get() + m.getOr(\"k\", 0) + m.length\n");
        AssertError("fn f(x: Int) -> Int\n  x.length\n", "no overload of 'length' accepts (Int)");
    }

    [Fact]
    public void IfBranchesMustAgree()
    {
        AssertError("fn f(b: Bool) -> Int\n  if b then 1 else \"x\"\n", "expected Int but found String");
        AssertError("fn f(x: Int) -> Int\n  if x then 1 else 2\n", "expected Bool but found Int");
    }

    [Fact]
    public void ShapesAreSatisfiedStructurally()
    {
        var shape = "shape Clock\n  fn now() -> Int ! Nondet\n";
        AssertClean(shape + "service Sys\n  fn now() -> Int\n    1\nservice Uses(clock: Clock)\n  fn t() -> Int\n    clock.now()\nroot main\n  clock: Sys\n  uses: Uses\n");
        AssertError(shape + "service Bad\n  fn later() -> Int\n    1\nservice Uses(clock: Clock)\n  fn t() -> Int\n    clock.now()\nroot main\n  clock: Bad\n  uses: Uses\n", "has no member 'now'");
        AssertError(shape + "service Bad\n  fn now() -> String\n    \"x\"\nservice Uses(clock: Clock)\n  fn t() -> Int\n    clock.now()\nroot main\n  clock: Bad\n  uses: Uses\n", "shape Clock requires fn() -> Int");
        AssertError(shape + "service Loud\n  fn now() -> Int ! Nondet Write\n    1\nservice Uses(clock: Clock)\n  fn t() -> Int\n    clock.now()\nroot main\n  clock: Loud\n  uses: Uses\n", "allows only Nondet");
    }

    [Fact]
    public void RootReportsMissingDependencies() =>
        AssertError("record Cfg(s: String)\nservice S(cfg: Cfg)\n  fn f() -> Int\n    1\nroot main\n  s: S\n", "cannot resolve dependency 'cfg: Cfg'");

    [Fact]
    public void FunctionTypedDependencyChecksEffects()
    {
        var prog = "fn mk() -> fn() -> Int ! Nondet Write\n  \\-> 1\nservice S(ids: fn() -> Int ! Nondet)\n  fn f() -> Int\n    ids()\nroot main\n  ids: mk()\n  s: S\n";
        AssertError(prog, "has effects Nondet Write but only Nondet are allowed");
        AssertClean(prog.Replace("! Nondet)", "! Nondet Write)"));
    }

    [Fact]
    public void InitValidationIsTyped()
    {
        AssertClean("record P\n  name: String\n    init => if value.length > 0 then value else fail(ArgumentError(\"empty\"))\n");
        AssertError("record P\n  name: String\n    init => 1\n", "expected String but found Int");
    }

    [Theory]
    [InlineData("generics")]
    public void GenericsSampleChecksClean(string sample)
    {
        var diags = TypeChecker.Check(ModuleSet.Load(Path.Combine(ExamplesDir(), sample)));
        Assert.True(diags.Count == 0, string.Join("\n", diags));
    }

    [Fact]
    public void ResourceRules()
    {
        var handle = "resource service H(log: Cell[Int])\n  fn release() -> Unit\n    log.set(1)\n";
        AssertClean(handle + "handler f(log: Cell[Int]) -> Unit\n  use h = H(log)\n  unit\n");
        AssertError("resource service H\n  fn touch() -> Unit\n    unit\n", "must define 'fn release() -> Unit'");
        AssertError(handle + "handler f(log: Cell[Int]) -> Unit\n  h = H(log)\n  unit\n", "bind it with 'use h = ...'");
        AssertError(handle + "handler f(log: Cell[Int]) -> Unit\n  use x = 1\n  unit\n", "'use' needs a resource service");
        AssertError(handle + "handler f(h: H) -> Unit\n  use g = h\n  unit\n", "must bind a fresh resource");
        AssertError(handle + "record R\n  h: H\n", "contains mutable state");
        // releasing writes, so a fn cannot use a resource whose release writes
        AssertError(handle + "fn f(log: Cell[Int]) -> Unit\n  use h = H(log)\n  unit\n", "'f' is a fn and cannot write: it calls 'release of h'");
        // returning a resource transfers ownership and is allowed
        AssertClean(handle + "fn open(log: Cell[Int]) -> H\n  H(log)\n");
    }

    [Fact]
    public void CaptiveDependenciesAreRejected() =>
        AssertError("scoped service Tx\n  fn f() -> Int\n    1\nservice Repo(tx: Tx)\n  fn g() -> Int\n    tx.f()\n", "is a singleton but depends on scoped service Tx");

    [Fact]
    public void ExternRules()
    {
        var union = "union E\n  Bad(message: String)\n  Other(code: Int)\n";
        AssertClean(union + "extern fn f(p: String) -> Result[String, E] ! Nondet\n  csharp \"X.Y\"\n  catch csharp \"System.IO.IOException\" -> Bad\n");
        AssertError(union + "extern fn f(p: String) -> Result[String, E] ! Nondet\n  csharp \"X.Y\"\n  catch csharp \"System.IO.IOException\" -> Nope\n", "E has no variant 'Nope'");
        AssertError(union + "extern fn f(p: String) -> Result[String, E] ! Nondet\n  csharp \"X.Y\"\n  catch csharp \"System.IO.IOException\" -> Other\n", "exactly one String field");
        AssertError(union + "extern fn f(p: String) -> String ! Nondet\n  csharp \"X.Y\"\n  catch csharp \"System.IO.IOException\" -> Bad\n", "does not return a Result");
        AssertError(union + "extern fn f(p: String) -> String ! Nondet\n  rust \"x\"\n", "unknown target 'rust'");
        Assert.Throws<Syntax.SyntaxException>(() => Check("extern fn f() -> Int\n  csharp \"X\"\n"));
        // callers are charged the declared effects
        AssertError("extern fn f() -> Int ! Nondet\n  csharp \"X\"\nfn g() -> Int ! Pure\n  f()\n", "'g' is declared ! Pure but its body has the Nondet effect");
    }

    [Fact]
    public void GenericRules()
    {
        AssertError("record Pair[A, B](first: A, second: B)\nfn f(p: Pair[Int]) -> Int\n  1\n", "Pair takes 2 type argument(s) but 1 were given");
        AssertError("fn f[T](x: T) -> Int\n  x + 1\n", "operator '+' is not defined on T and Int");
        AssertError("fn id[T](x: T) -> T\n  x\nfn f() -> String\n  id(1)\n", "expected String but found Int");
        AssertClean("fn id[T](x: T) -> T\n  x\nfn f() -> String\n  id(\"s\")\n");
        AssertClean("union Box[T]\n  Full(value: T)\n  Empty\nfn get[T](b: Box[T], d: T) -> T\n  match b\n    Full(v) => v\n    Empty => d\nfn f() -> Int\n  get(Full(1), 0) + get(Box.Empty, 2)\n");
        AssertError("union Box[T]\n  Full(value: T)\n  Empty\nfn f() -> Int\n  match Full(1)\n    Full(v) => v\n", "missing Empty");
    }

    [Fact]
    public void ModulesResolveAcrossFiles()
    {
        var diags = TypeChecker.Check(ModuleSet.Load(new[]
        {
            ("a.treb", "module a\nrecord Id(v: String)\nfn mk(s: String) -> Id\n  Id(s)\n"),
            ("b.treb", "module b\nuse a\nfn f() -> Id\n  a.mk(\"x\")\nfn g() -> Id\n  mk(\"y\")\n"),
        }));
        Assert.True(diags.Count == 0, string.Join("\n", diags));
    }
}
