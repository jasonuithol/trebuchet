using Trebuchet.Compiler.Runtime;
using Trebuchet.Compiler.Semantics;
using Trebuchet.Compiler.Syntax;
using TrebPanic = Trebuchet.Compiler.Runtime.TrebPanic;
using Vector = Trebuchet.Runtime.Vector<Trebuchet.Compiler.Runtime.Value>;
using Xunit;

namespace Trebuchet.Compiler.Tests;

public class InterpreterTests
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

    private static Value EvalInline(string body)
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", $"fn main() -> Unit\n{Indent(body)}\n") });
        var it = new Interpreter(modules);
        var env = it.EnvOf(modules.Find("t")!);
        env.TryGet("main", out var main);
        return it.Call(main, Array.Empty<Value>());
    }

    private static string Indent(string body) => string.Join("\n", body.Split('\n').Select(l => "  " + l));

    [Fact]
    public void ArithmeticAndComparison()
    {
        Assert.Equal(new IntValue(14), EvalInline("2 + 3 * 4"));
        Assert.Equal(BoolValue.True, EvalInline("1 < 2 and not (3 == 4)"));
        Assert.Equal(new StringValue("a1"), EvalInline("\"a\" + 1"));
    }

    [Fact]
    public void RecordConstructionWithAndEquality()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            record Addr
              city: String
            record P
              name: String
              addr: Addr
            fn main() -> Bool
              p = P("a", Addr("x"))
              q = p with
                addr.city: "y"
              p == P("a", Addr("x")) and q.addr.city == "y" and p.addr.city == "x"
            """) });
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("t")!).TryGet("main", out var main);
        Assert.Equal(BoolValue.True, it.Call(main, Array.Empty<Value>()));
    }

    [Fact]
    public void InitValidationRunsInConstructor()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            record Slot
              start: Int
              end: Int
                init => if value > start then value else fail(ArgumentError("bad slot"))
            fn main() -> Slot
              Slot(2, 1)
            """) });
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("t")!).TryGet("main", out var main);
        var ex = Assert.Throws<TrebPanic>(() => it.Call(main, Array.Empty<Value>()));
        Assert.Equal("bad slot", ex.Message);
    }

    [Fact]
    public void PropagateReturnsErrorFromEnclosingFunction()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            fn inner(x: Int) -> Result[Int, String]
              if x > 0 then ok(x) else error("neg")
            fn outer(x: Int) -> Result[Int, String]
              v = inner(x)?
              ok(v + 1)
            fn main() -> Bool
              outer(1) == ok(2) and outer(-1) == error("neg")
            """) });
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("t")!).TryGet("main", out var main);
        Assert.Equal(BoolValue.True, it.Call(main, Array.Empty<Value>()));
    }

    [Fact]
    public void MatchDestructuresPositionally()
    {
        var modules = ModuleSet.Load(new[] { ("t.treb", """
            union Shape
              Circle(r: Int)
              Rect(w: Int, h: Int)
              Dot
            fn area(s: Shape) -> Int
              match s
                Circle(r) => r * r * 3
                Rect(w, h) => w * h
                Dot => 0
            fn main() -> Int
              area(Circle(2)) + area(Rect(2, 3)) + area(Shape.Dot)
            """) });
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("t")!).TryGet("main", out var main);
        Assert.Equal(new IntValue(18), it.Call(main, Array.Empty<Value>()));
    }

    [Fact]
    public void BookingFlowThroughTestRoot()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "bookings"));
        var it = new Interpreter(modules);
        var (module, root) = modules.FindRoot("test")!.Value;
        var resolved = it.Compose(module, root);
        var api = Assert.IsType<ServiceInstance>(resolved["api"]);
        var env = it.EnvOf(modules.Find("bookings.api.contracts")!);
        env.TryGet("BookingRequest", out var reqCtor);
        env.TryGet("Instant", out var instantNs);

        Value Instant(string s) => it.Member(instantNs, "parse", env, new Value[] { new StringValue(s) }, default);
        Value Request(string room, string guest, string start, string end, string[]? attendees = null, bool waitlist = false) =>
            it.Call(reqCtor, new[] { new StringValue(room), new StringValue(guest), Instant(start), Instant(end),
                new ListValue(Vector.From((attendees ?? Array.Empty<string>()).Select(a => (Value)new StringValue(a)))), BoolValue.Of(waitlist) });
        Value Post(Value req) => it.Member(api, "postBooking", env, new[] { req }, default);
        string ErrorName(Value r) { Assert.True(Builtins.IsError(r, out var e)); return ((RecordValue)e).TypeName; }

        // 1. a booking in the future succeeds
        var first = Post(Request("boardroom", "alice", "2026-09-05T10:00:00Z", "2026-09-05T11:00:00Z"));
        Assert.True(Builtins.IsOk(first, out var response));
        Assert.Equal("bk-1", ((StringValue)((RecordValue)response).Get("id")!).V);

        // 2. an overlapping booking conflicts
        Assert.Equal("Conflict", ErrorName(Post(Request("boardroom", "bob", "2026-09-05T10:30:00Z", "2026-09-05T11:30:00Z"))));

        // 3. a booking in the past (before the fixed clock at 09:00) is rejected
        Assert.Equal("BadRequest", ErrorName(Post(Request("boardroom", "carol", "2026-09-05T07:00:00Z", "2026-09-05T08:00:00Z"))));

        // 4. an unknown room is not found
        Assert.Equal("NotFound", ErrorName(Post(Request("attic", "dave", "2026-09-05T10:00:00Z", "2026-09-05T11:00:00Z"))));

        // 5. more attendees than the room holds is rejected
        Assert.Equal("BadRequest", ErrorName(Post(Request("huddle", "eve", "2026-09-05T12:00:00Z", "2026-09-05T13:00:00Z", new[] { "a", "b", "c", "d", "e" }))));

        // 6. the same overlap with waitlist=true is accepted as waitlisted
        var waiting = Post(Request("boardroom", "bob", "2026-09-05T10:30:00Z", "2026-09-05T11:30:00Z", null, true));
        Assert.True(Builtins.IsOk(waiting, out var waitingResponse));
        Assert.Equal("bk-6", ((StringValue)((RecordValue)waitingResponse).Get("id")!).V);

        // 7. confirm, then cancel, then cancelling again conflicts
        Assert.True(Builtins.IsOk(it.Member(api, "postConfirm", env, new Value[] { new StringValue("boardroom"), new StringValue("bk-1") }, default), out _));
        Assert.True(Builtins.IsOk(it.Member(api, "deleteBooking", env, new Value[] { new StringValue("boardroom"), new StringValue("bk-1"), new StringValue("plans changed") }, default), out _));
        Assert.Equal("Conflict", ErrorName(it.Member(api, "deleteBooking", env, new Value[] { new StringValue("boardroom"), new StringValue("bk-1"), new StringValue("again") }, default)));

        // 8. the cancellation promoted the waitlisted booking
        Assert.True(Builtins.IsOk(it.Member(api, "getBookings", env, new Value[] { new StringValue("boardroom") }, default), out var listed));
        var promoted = ((ListValue)listed).Items.Select(b => (RecordValue)b).Single(b => ((StringValue)((RecordValue)b.Get("id")!).Get("value")!).V == "bk-6");
        Assert.Equal("Held", ((RecordValue)promoted.Get("status")!).TypeName);
        Assert.Equal(2, ((ListValue)listed).Items.Count);

        // 9. a free slot, a page, the stats, and the info route
        Assert.True(Builtins.IsOk(it.Member(api, "getAvailability", env, new Value[] { new StringValue("boardroom"), Instant("2026-09-05T12:00:00Z"), Instant("2026-09-05T13:00:00Z") }, default), out var isFree));
        Assert.Equal(BoolValue.True, isFree);
        Assert.True(Builtins.IsOk(it.Member(api, "getPage", env, new Value[] { new StringValue("boardroom"), new IntValue(0), new IntValue(1) }, default), out var page));
        Assert.Equal(1, ((ListValue)((RecordValue)page).Get("items")!).Items.Count);
        Assert.Equal(new IntValue(2), ((RecordValue)page).Get("total"));
        Assert.True(Builtins.IsOk(it.Member(api, "getStats", env, Array.Empty<Value>(), default), out var stats));
        var boardroom = ((ListValue)stats).Items.Select(r => (RecordValue)r).Single(r => ((StringValue)r.Get("room")!).V == "boardroom");
        Assert.Equal((1L, 1L, 1L), (((IntValue)boardroom.Get("held")!).V, ((IntValue)boardroom.Get("cancelled")!).V, ((IntValue)boardroom.Get("guests")!).V));
        it.Externs["bookings.api.system.hostName"] = _ => new StringValue("test-host");
        Assert.True(Builtins.IsOk(it.Member(api, "getInfo", env, Array.Empty<Value>(), default), out var info));
        Assert.Equal(new IntValue(2), ((RecordValue)info).Get("rooms"));
        Assert.Equal("test-host", ((StringValue)((RecordValue)info).Get("host")!).V);
    }

    public static (string present, string missing) FfiFiles()
    {
        var present = Path.Combine(Path.GetTempPath(), "treb-ffi-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(present, "hello");
        return (present, Path.Combine(Path.GetTempPath(), "treb-ffi-missing-" + Guid.NewGuid().ToString("N")[..8] + ".txt"));
    }

    public const string FfiDemoExpected = "read 5 chars; error; a+b; 1 lines";

    [Fact]
    public void ExternsResolveThroughTheRegistry()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "ffi"));
        var it = new Interpreter(modules);
        it.Externs["ffi.readTextFile"] = args => new StringValue(File.ReadAllText(((StringValue)args[0]).V));
        it.Externs["ffi.urlEncode"] = args => new StringValue(System.Net.WebUtility.UrlEncode(((StringValue)args[0]).V));
        it.Externs["ffi.readLines"] = args => new ListValue(Vector.From(File.ReadAllLines(((StringValue)args[0]).V).Select(l => (Value)new StringValue(l))));
        it.EnvOf(modules.Find("ffi")!).TryGet("demo", out var demo);
        var (present, missing) = FfiFiles();
        Assert.Equal(new StringValue(FfiDemoExpected), it.Call(demo, new Value[] { new StringValue(present), new StringValue(missing) }));

        var bare = new Interpreter(modules);
        bare.EnvOf(modules.Find("ffi")!).TryGet("demo", out var demo2);
        var ex = Assert.Throws<TrebPanic>(() => bare.Call(demo2, new Value[] { new StringValue(present), new StringValue(missing) }));
        Assert.Contains("no implementation registered", ex.Message);
    }

    public const string ResourcesDemoExpected = "used a, used b, closed b, closed a";

    [Fact]
    public void UseReleasesInReverseOrder()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "resources"));
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("resources")!).TryGet("demo", out var demo);
        var entries = Assert.IsType<ListValue>(it.Call(demo, Array.Empty<Value>()));
        Assert.Equal(ResourcesDemoExpected, string.Join(", ", entries.Items.Select(v => ((StringValue)v).V)));
    }

    public const string AsyncDemoExpected = "a, b";

    [Fact]
    public void SleepSuspendsAndDemoIsSequential()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "async"));
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("async")!).TryGet("demo", out var demo);
        var entries = Assert.IsType<ListValue>(it.Call(demo, Array.Empty<Value>()));
        Assert.Equal(AsyncDemoExpected, string.Join(", ", entries.Items.Select(v => ((StringValue)v).V)));
    }

    public const string TreesDemoExpected = "7 15 6 3 same";

    [Fact]
    public void RecursiveUnionsAndRecordsRun()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "trees"));
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("trees")!).TryGet("demo", out var demo);
        Assert.Equal(new StringValue(TreesDemoExpected), it.Call(demo, Array.Empty<Value>()));
    }

    public const string FactoriesExpected = "db:a db:b | closed a closed b";

    [Fact]
    public void RootSynthesisesFactoryLambdas()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "factories"));
        var it = new Interpreter(modules);
        var (module, root) = modules.FindRoot("main")!.Value;
        var client = Assert.IsType<ServiceInstance>(it.Compose(module, root)["client"]);
        var env = it.EnvOf(module);
        string Ping(string n) => ((StringValue)it.Member(client, "ping", env, new Value[] { new StringValue(n) }, default)).V;
        var history = (ListValue)it.Member(client, "history", env, Array.Empty<Value>(), default);
        var a = Ping("a");
        var b = Ping("b");
        history = (ListValue)it.Member(client, "history", env, Array.Empty<Value>(), default);
        Assert.Equal(FactoriesExpected, $"{a} {b} | {string.Join(" ", history.Items.Select(v => ((StringValue)v).V))}");
    }

    public const string SuperviseExpected = "fine 4; crashed negative: -1";

    [Fact]
    public void SuperviseTurnsAPanicIntoAnError()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "supervision"));
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("supervision")!).TryGet("demo", out var demo);
        Assert.Equal(new StringValue(SuperviseExpected), it.Call(demo, Array.Empty<Value>()));
    }

    public const string OrdersExpected = "placed=Ok again=AlreadyPlaced empty=EmptyOrder total=350 status=Placed";

    /// <summary>The milestone-6 acceptance scenario: one command, one handler, one query, one service, one root.</summary>
    [Fact]
    public void OrdersAcceptanceScenario()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "orders"));
        var it = new Interpreter(modules);
        var (module, root) = modules.FindRoot("main")!.Value;
        var svc = Assert.IsType<ServiceInstance>(it.Compose(module, root)["orderService"]);
        var env = it.EnvOf(module);
        Value Ctor(string name, params Value[] args) { env.TryGet(name, out var c); return it.Call(c, args); }
        var id = Ctor("OrderId", new StringValue("o-1"));
        var customer = Ctor("CustomerId", new StringValue("alice"));
        var lines = new ListValue(Vector.From(new Value[] { Ctor("Line", new StringValue("a"), new IntValue(2), new IntValue(100)), Ctor("Line", new StringValue("b"), new IntValue(1), new IntValue(150)) }));
        string Name(Value r) => Builtins.IsOk(r, out _) ? "Ok" : ((RecordValue)((RecordValue)r).FieldValues[0]).TypeName;
        var placed = Name(it.Member(svc, "place", env, new[] { Ctor("PlaceOrder", id, customer, lines) }, default));
        var again = Name(it.Member(svc, "place", env, new[] { Ctor("PlaceOrder", id, customer, lines) }, default));
        var empty = Name(it.Member(svc, "place", env, new[] { Ctor("PlaceOrder", Ctor("OrderId", new StringValue("o-2")), customer, ListValue.Empty) }, default));
        Assert.True(Builtins.IsOk(it.Member(svc, "describe", env, new[] { id, customer }, default), out var described));
        Assert.Equal(OrdersExpected, $"placed={placed} again={again} empty={empty} {((StringValue)described).V}");
    }

    public const string PatternsExpected = "point; circle 2; square 3 area 9; rect 2x5; 10 7,8 5 none 1..9 1a origin x-axis 3 y-axis 4 diagonal plane 2 -1";

    [Fact]
    public void GuardsListsTuplesAndAsPatternsRun()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "patterns"));
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("patterns")!).TryGet("demo", out var demo);
        Assert.Equal(new StringValue(PatternsExpected), it.Call(demo, Array.Empty<Value>()));
    }

    public const string ClassesExpected = "400c abc 10c 20c 30c 50c 9 apple,fig,pear,";

    [Fact]
    public void ShapesOverTypesDispatchThroughInstances()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "classes"));
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("classes")!).TryGet("demo", out var demo);
        Assert.Equal(new StringValue(ClassesExpected), it.Call(demo, Array.Empty<Value>()));
    }

    public const string LazyExpected = "[0, 2, 4, 6, 8] [0, 1, 4, 9, 16, 25, 36, 49] 10000 55 [10, 20, 30]";

    [Fact]
    public void LazySequencesRun()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "lazy"));
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("lazy")!).TryGet("demo", out var demo);
        Assert.Equal(new StringValue(LazyExpected), it.Call(demo, Array.Empty<Value>()));
    }

    public const string CollectionsDemoExpected = "3 unique; both=2 either=4 onlyA=red hasRed=yes afterRemove=2 equal=yes empty=yes vec=yes map=yes";

    [Fact]
    public void SetBuiltinsRun()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "collections"));
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("collections")!).TryGet("demo", out var demo);
        Assert.Equal(new StringValue(CollectionsDemoExpected), it.Call(demo, Array.Empty<Value>()));
    }

    public const string GenericsDemoExpected = "one 1; n=42; lefts=2 rights=3; ok: 42; error: boom; 2";

    [Fact]
    public void GenericsDemoRuns()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "generics"));
        var it = new Interpreter(modules);
        it.EnvOf(modules.Find("generics")!).TryGet("demo", out var demo);
        Assert.Equal(new StringValue(GenericsDemoExpected), it.Call(demo, Array.Empty<Value>()));
    }

    [Fact]
    public void SlotWithEndBeforeStartPanics()
    {
        var modules = ModuleSet.Load(Path.Combine(ExamplesDir(), "bookings"));
        var it = new Interpreter(modules);
        var env = it.EnvOf(modules.Find("bookings.domain.slot")!);
        env.TryGet("Slot", out var slot);
        env.TryGet("Instant", out var instantNs);
        var a = it.Member(instantNs, "parse", env, new Value[] { new StringValue("2026-01-01T10:00:00Z") }, default);
        var b = it.Member(instantNs, "parse", env, new Value[] { new StringValue("2026-01-01T09:00:00Z") }, default);
        var ex = Assert.Throws<TrebPanic>(() => it.Call(slot, new[] { a, b }));
        Assert.Contains("ends before it starts", ex.Message);
    }
}
