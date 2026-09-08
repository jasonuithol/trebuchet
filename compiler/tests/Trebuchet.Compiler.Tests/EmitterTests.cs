using System.Diagnostics;
using System.Reflection;
using Trebuchet.Compiler.Backends;
using Trebuchet.Compiler.Semantics;
using Xunit;

namespace Trebuchet.Compiler.Tests;

/// <summary>
/// Emits the bookings sample to C#, builds it with dotnet, loads the assembly, and runs
/// the same scenario the interpreter test runs. The interpreter is the oracle.
/// </summary>
public class EmitterTests
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

    private static Assembly EmitAndBuild(string sample, bool host = false) => EmitAndBuildDir(Path.Combine(ExamplesDir(), sample), sample, host);

    /// <summary>Writes one inline module to a temp directory and builds it.</summary>
    private static Assembly EmitAndBuildSource(string name, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "treb-src-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".treb"), source);
        return EmitAndBuildDir(dir, name, false);
    }

    private static Assembly EmitAndBuildDir(string dir, string sample, bool host)
    {
        var modules = ModuleSet.Load(dir);
        var checker = TypeChecker.CheckWithEffects(modules);
        Assert.Empty(checker.Diagnostics);
        var files = new CSharpEmitter(modules, checker).Emit(CSharpEmitter.FindRuntimeProject(), host);
        var outDir = Path.Combine(Path.GetTempPath(), "treb-emit-" + sample + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        foreach (var (name, content) in files) File.WriteAllText(Path.Combine(outDir, name), content);

        var psi = new ProcessStartInfo("dotnet", "build -nologo -v q") { WorkingDirectory = outDir, RedirectStandardOutput = true, RedirectStandardError = true };
        var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, "generated C# failed to build:\n" + output);
        // Each test gets its own context: the generated assemblies all share the simple name "Generated".
        var context = new System.Runtime.Loader.AssemblyLoadContext("gen-" + sample, isCollectible: false);
        return context.LoadFromAssemblyPath(Path.Combine(outDir, "bin", "Debug", "net8.0", "Generated.dll"));
    }

    private static object Call(object target, string method, params object[] args) =>
        Unwrap(target.GetType().GetMethod(method)!.Invoke(target, args)!);

    /// <summary>Generated methods that suspend return ValueTask&lt;T&gt;; block on it for the test.</summary>
    private static object Unwrap(object value)
    {
        var t = value.GetType();
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var task = (System.Threading.Tasks.Task)t.GetMethod("AsTask")!.Invoke(value, null)!;
            task.Wait();
            return task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        return value;
    }

    private static object Prop(object target, string name) =>
        target.GetType().GetProperty(name)!.GetValue(target)!;

    private static string Kind(object result) => result.GetType().Name.Split('`')[0];

    [Fact]
    public void BookingsCompilesAndRunsTheInterpreterScenario()
    {
        var asm = EmitAndBuild("bookings");
        var host = asm.GetType("Generated.Bookings_Host_Test")!;
        var root = Unwrap(host.GetMethod("test")!.Invoke(null, null)!);
        var api = Prop(root, "api");
        var reqType = asm.GetType("Generated.BookingRequest")!;
        object Req(string room, string guest, string start, string end, string[]? attendees = null, bool waitlist = false) =>
            Activator.CreateInstance(reqType, room, guest, DateTimeOffset.Parse(start), DateTimeOffset.Parse(end), Trebuchet.Runtime.Vector<string>.Of(attendees ?? Array.Empty<string>()), waitlist)!;
        DateTimeOffset At(string s) => DateTimeOffset.Parse(s);
        string ErrorName(object r) { Assert.Equal("Error", Kind(r)); return Prop(r, "error").GetType().Name; }

        var first = Call(api, "postBooking", Req("boardroom", "alice", "2026-09-05T10:00:00Z", "2026-09-05T11:00:00Z"));
        Assert.Equal("Ok", Kind(first));
        Assert.Equal("bk-1", (string)Prop(Prop(first, "value"), "id"));
        Assert.Equal("Conflict", ErrorName(Call(api, "postBooking", Req("boardroom", "bob", "2026-09-05T10:30:00Z", "2026-09-05T11:30:00Z"))));
        Assert.Equal("BadRequest", ErrorName(Call(api, "postBooking", Req("boardroom", "carol", "2026-09-05T07:00:00Z", "2026-09-05T08:00:00Z"))));
        Assert.Equal("NotFound", ErrorName(Call(api, "postBooking", Req("attic", "dave", "2026-09-05T10:00:00Z", "2026-09-05T11:00:00Z"))));
        Assert.Equal("BadRequest", ErrorName(Call(api, "postBooking", Req("huddle", "eve", "2026-09-05T12:00:00Z", "2026-09-05T13:00:00Z", new[] { "a", "b", "c", "d", "e" }))));
        var waiting = Call(api, "postBooking", Req("boardroom", "bob", "2026-09-05T10:30:00Z", "2026-09-05T11:30:00Z", null, true));
        Assert.Equal("bk-6", (string)Prop(Prop(waiting, "value"), "id"));

        Assert.Equal("Ok", Kind(Call(api, "postConfirm", "boardroom", "bk-1")));
        Assert.Equal("Ok", Kind(Call(api, "deleteBooking", "boardroom", "bk-1", "plans changed")));
        Assert.Equal("Conflict", ErrorName(Call(api, "deleteBooking", "boardroom", "bk-1", "again")));

        var listed = ((System.Collections.IEnumerable)Prop(Call(api, "getBookings", "boardroom"), "value")).Cast<object>().ToList();
        Assert.Equal(2, listed.Count);
        Assert.Equal("Held", Prop(listed.Single(b => (string)Prop(Prop(b, "id"), "value") == "bk-6"), "status").ToString());

        var free = Call(api, "getAvailability", "boardroom", At("2026-09-05T12:00:00Z"), At("2026-09-05T13:00:00Z"));
        Assert.Equal("Ok", Kind(free));
        Assert.True((bool)Prop(free, "value"));

        var page = Prop(Call(api, "getPage", "boardroom", 0L, 1L), "value");
        Assert.Equal(1, (int)Prop(Prop(page, "items"), "Count"));
        Assert.Equal(2L, (long)Prop(page, "total"));

        var stats = ((System.Collections.IEnumerable)Prop(Call(api, "getStats"), "value")).Cast<object>().Single(r => (string)Prop(r, "room") == "boardroom");
        Assert.Equal((1L, 1L, 1L), ((long)Prop(stats, "held"), (long)Prop(stats, "cancelled"), (long)Prop(stats, "guests")));

        var info = Prop(Call(api, "getInfo"), "value");
        Assert.Equal(2L, (long)Prop(info, "rooms"));
        Assert.Equal(System.Net.Dns.GetHostName(), (string)Prop(info, "host"));
    }

    [Fact]
    public void HostRegistrationCompilesAndResolvesShapes()
    {
        var asm = EmitAndBuild("bookings", host: true);
        var hostType = asm.GetType("Generated.TrebuchetHost")!;
        var add = hostType.GetMethod("AddTrebuchet_test")!;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        add.Invoke(null, new object[] { services });
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var api = provider.GetService(asm.GetType("Generated.BookingApi")!);
        Assert.NotNull(api);
        var store = provider.GetService(asm.GetType("Generated.RoomStore")!);
        Assert.NotNull(store);
        Assert.Equal("InMemoryRoomStore", store!.GetType().Name);
    }

    [Fact]
    public void HostRegistrationsTakePrecedenceOverRootEntries()
    {
        var asm = EmitAndBuild("bookings", host: true);
        var roomIdType = asm.GetType("Generated.RoomId")!;
        var roomType = asm.GetType("Generated.Room")!;
        var mapType = typeof(Trebuchet.Runtime.Map<,>).MakeGenericType(roomIdType, roomType);
        // the host supplies its own room table, with a room the root does not know about
        var lab = Activator.CreateInstance(roomIdType, "lab")!;
        var vectorType = typeof(Trebuchet.Runtime.Vector<>).MakeGenericType(asm.GetType("Generated.Booking")!);
        var emptyBookings = vectorType.GetField("Empty")!.GetValue(null)!;
        var room = Activator.CreateInstance(roomType, lab, "Lab", 3L, emptyBookings)!;
        var rooms = mapType.GetMethod("Set")!.Invoke(mapType.GetField("Empty")!.GetValue(null), new[] { lab, room })!;

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, mapType, rooms);
        asm.GetType("Generated.TrebuchetHost")!.GetMethod("AddTrebuchet_dev")!.Invoke(null, new object[] { services });
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var api = provider.GetService(asm.GetType("Generated.BookingApi")!)!;
        var listed = Call(api, "getBookings", "lab");
        Assert.Equal("Ok", Kind(listed));
        var unknown = Call(api, "getBookings", "boardroom");
        Assert.Equal("Error", Kind(unknown));
    }

    [Fact]
    public void SetBuiltinsCompileAndRun()
    {
        var asm = EmitAndBuild("collections");
        var demo = asm.GetType("Generated.Collections")!.GetMethod("demo")!;
        Assert.Equal(InterpreterTests.CollectionsDemoExpected, (string)demo.Invoke(null, null)!);
    }

    [Fact]
    public void PanicStackTraceNamesTheTrebuchetSource()
    {
        var asm = EmitAndBuildSource("boom", """
            module boom
            fn boom(x: Int) -> Int
              y = x + 1
              fail(ArgumentError("bang " + toString(y)))
            """);
        var boom = asm.GetType("Generated.Boom")!.GetMethod("boom")!;
        var ex = Assert.Throws<TargetInvocationException>(() => boom.Invoke(null, new object[] { 1L }));
        var panic = Assert.IsType<Trebuchet.Runtime.TrebPanic>(ex.InnerException);
        Assert.Equal("bang 2", panic.Message);
        Assert.Contains("boom.treb:line 4", panic.StackTrace);
    }

    [Fact]
    public void JsonBoundaryFlattensIdsAndTagsVariants()
    {
        var asm = EmitAndBuild("bookings");
        object New(string type, params object[] args) => Activator.CreateInstance(asm.GetType("Generated." + type)!, args)!;
        var start = DateTimeOffset.Parse("2026-09-05T10:00:00Z");
        var end = DateTimeOffset.Parse("2026-09-05T11:00:00Z");
        var held = asm.GetType("Generated.Held")!.GetField("Instance")!.GetValue(null)!;
        var noAttendees = typeof(Trebuchet.Runtime.Set<>).MakeGenericType(asm.GetType("Generated.GuestId")!).GetField("Empty")!.GetValue(null)!;
        var booking = New("Booking", New("BookingId", "bk-1"), New("RoomId", "boardroom"), New("GuestId", "alice"), New("Slot", start, end), held, noAttendees);
        var options = Trebuchet.Runtime.TrebuchetJson.Options;

        var json = System.Text.Json.JsonSerializer.Serialize(booking, booking.GetType(), options);
        Assert.Equal("""{"id":"bk-1","room":"boardroom","guest":"alice","slot":{"start":"2026-09-05T10:00:00+00:00","end":"2026-09-05T11:00:00+00:00"},"status":"Held","attendees":[]}""", json);
        Assert.Equal(booking, System.Text.Json.JsonSerializer.Deserialize(json, booking.GetType(), options));

        // a single-field record stays an object at the top level
        Assert.Equal("""{"value":"boardroom"}""", System.Text.Json.JsonSerializer.Serialize(New("RoomId", "boardroom"), asm.GetType("Generated.RoomId")!, options));

        // a variant with fields carries its type; a map over an id key is an object
        var err = New("BadRequest", "no");
        Assert.Equal("""{"type":"BadRequest","message":"no"}""", System.Text.Json.JsonSerializer.Serialize(err, asm.GetType("Generated.ApiError")!, options));
        var mapType = typeof(Trebuchet.Runtime.Map<,>).MakeGenericType(asm.GetType("Generated.RoomId")!, typeof(long));
        var rooms = mapType.GetMethod("Set")!.Invoke(mapType.GetField("Empty")!.GetValue(null), new[] { New("RoomId", "lab"), 3L })!;
        var roomsJson = System.Text.Json.JsonSerializer.Serialize(rooms, mapType, options);
        Assert.Equal("""{"lab":3}""", roomsJson);
        Assert.Equal(rooms, System.Text.Json.JsonSerializer.Deserialize(roomsJson, mapType, options));
    }

    [Fact]
    public void GenericsCompileAndRun()
    {
        var asm = EmitAndBuild("generics");
        var demo = asm.GetType("Generated.Generics")!.GetMethod("demo")!;
        Assert.Equal(InterpreterTests.GenericsDemoExpected, (string)demo.Invoke(null, null)!);
    }

    [Fact]
    public void ExternsCallTheHostAndMapExceptions()
    {
        var asm = EmitAndBuild("ffi");
        var demo = asm.GetType("Generated.Ffi")!.GetMethod("demo")!;
        var (present, missing) = InterpreterTests.FfiFiles();
        Assert.Equal(InterpreterTests.FfiDemoExpected, (string)Unwrap(demo.Invoke(null, new object[] { present, missing })!));
    }

    [Fact]
    public void ResourcesDisposeInReverseOrder()
    {
        var asm = EmitAndBuild("resources");
        var demo = asm.GetType("Generated.Resources")!.GetMethod("demo")!;
        var entries = (System.Collections.IEnumerable)Unwrap(demo.Invoke(null, null)!);
        Assert.Equal(InterpreterTests.ResourcesDemoExpected, string.Join(", ", entries.Cast<object>()));
        Assert.Contains(typeof(IDisposable), asm.GetType("Generated.Handle")!.GetInterfaces());
    }

    [Fact]
    public void SleepLowersToTaskDelay()
    {
        var asm = EmitAndBuild("async");
        var demo = asm.GetType("Generated.Async")!.GetMethod("demo")!;
        Assert.Equal(typeof(ValueTask<>), demo.ReturnType.GetGenericTypeDefinition());
        var entries = (System.Collections.IEnumerable)Unwrap(demo.Invoke(null, null)!);
        Assert.Equal(InterpreterTests.AsyncDemoExpected, string.Join(", ", entries.Cast<object>()));
    }

    [Fact]
    public void RecursiveUnionsCompileAndRun()
    {
        var asm = EmitAndBuild("trees");
        var demo = asm.GetType("Generated.Trees")!.GetMethod("demo")!;
        Assert.Equal(InterpreterTests.TreesDemoExpected, (string)demo.Invoke(null, null)!);
    }

    [Fact]
    public void RootSynthesisesFactoryLambdas()
    {
        var asm = EmitAndBuild("factories", host: true);
        var root = Unwrap(asm.GetType("Generated.Factories")!.GetMethod("main")!.Invoke(null, null)!);
        var client = Prop(root, "client");
        var a = (string)Call(client, "ping", "a");
        var b = (string)Call(client, "ping", "b");
        var history = ((System.Collections.IEnumerable)Call(client, "history")).Cast<object>();
        Assert.Equal(InterpreterTests.FactoriesExpected, $"{a} {b} | {string.Join(" ", history)}");

        // the same synthesis through the host container
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        asm.GetType("Generated.TrebuchetHost")!.GetMethod("AddTrebuchet_main")!.Invoke(null, new object[] { services });
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var hosted = provider.GetService(asm.GetType("Generated.Client")!)!;
        Assert.Equal("db:x", (string)Call(hosted, "ping", "x"));
    }

    [Fact]
    public void SuperviseCatchesPanicsInGeneratedCode()
    {
        var asm = EmitAndBuild("supervision");
        var demo = asm.GetType("Generated.Supervision")!.GetMethod("demo")!;
        Assert.Equal(InterpreterTests.SuperviseExpected, (string)demo.Invoke(null, null)!);
    }

    [Fact]
    public void OrdersAcceptanceScenario()
    {
        var asm = EmitAndBuild("orders");
        var root = Unwrap(asm.GetType("Generated.Orders")!.GetMethod("main")!.Invoke(null, null)!);
        var svc = Prop(root, "orderService");
        object New(string type, params object[] args) => Activator.CreateInstance(asm.GetType("Generated." + type)!, args)!;
        var id = New("OrderId", "o-1");
        var customer = New("CustomerId", "alice");
        var lineType = asm.GetType("Generated.Line")!;
        var lines = typeof(Trebuchet.Runtime.Vector<>).MakeGenericType(lineType).GetMethod("Of")!.Invoke(null, new object[] { Array.CreateInstance(lineType, 0) })!;
        lines = lines.GetType().GetMethod("Append")!.Invoke(lines, new[] { New("Line", "a", 2L, 100L) })!;
        lines = lines.GetType().GetMethod("Append")!.Invoke(lines, new[] { New("Line", "b", 1L, 150L) })!;
        var emptyLines = typeof(Trebuchet.Runtime.Vector<>).MakeGenericType(lineType).GetField("Empty")!.GetValue(null)!;
        string Name(object r) => Kind(r) == "Ok" ? "Ok" : Prop(r, "error").GetType().Name;
        var placed = Name(Call(svc, "place", New("PlaceOrder", id, customer, lines)));
        var again = Name(Call(svc, "place", New("PlaceOrder", id, customer, lines)));
        var empty = Name(Call(svc, "place", New("PlaceOrder", New("OrderId", "o-2"), customer, emptyLines)));
        var described = Call(svc, "describe", id, customer);
        Assert.Equal(InterpreterTests.OrdersExpected, $"placed={placed} again={again} empty={empty} {Prop(described, "value")}");
    }
}
