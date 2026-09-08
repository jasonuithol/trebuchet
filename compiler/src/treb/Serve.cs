using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Trebuchet.Compiler.Runtime;
using Trebuchet.Compiler.Semantics;
using Trebuchet.Runtime;
using TrebPanic = Trebuchet.Compiler.Runtime.TrebPanic;
using Trebuchet.Compiler.Syntax;

namespace treb;

/// <summary>
/// HTTP host adapter. Maps the methods of an API service to routes by convention:
/// the method name's verb prefix (get/post/put/delete) is the HTTP method and the
/// remainder, lower-cased, is the path. Record parameters bind from the JSON body;
/// scalar parameters bind from the query string, or from body fields on POST/PUT.
/// A Result is 200 with the Ok payload, or a status chosen by the error variant's name.
/// Panics are caught here, which makes the request boundary a supervisor point.
/// </summary>
public static class Serve
{
    private sealed record Route(string Verb, string Path, FnDecl Method, Value Callable);

    public static int Run(string dir, string rootName, string apiEntry, int port)
    {
        var modules = ModuleSet.Load(dir);
        var diagnostics = TypeChecker.Check(modules);
        if (diagnostics.Count > 0)
        {
            foreach (var d in diagnostics) Console.Error.WriteLine(d);
            throw new TrebPanic($"{diagnostics.Count} type error(s); not starting");
        }
        var it = new Interpreter(modules);
        ReflectionExterns.Bind(it, modules);
        var found = modules.FindRoot(rootName) ?? throw new TrebPanic($"no composition root named '{rootName}' in {dir}");
        var resolved = it.Compose(found.module, found.root);
        if (!resolved.TryGetValue(apiEntry, out var apiValue) || apiValue is not ServiceInstance api)
            throw new TrebPanic($"root '{rootName}' has no service entry named '{apiEntry}'");

        var routes = new List<Route>();
        foreach (var m in api.Type.Decl.Methods)
        {
            var (verb, path) = SplitName(m.Signature.Name);
            api.Env.TryGet(m.Signature.Name, out var callable);
            routes.Add(new Route(verb, path, m, callable));
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://localhost:{port}");
        var app = builder.Build();

        app.MapGet("/", () => Results.Json(new
        {
            root = rootName,
            api = api.Type.Name,
            routes = routes.Select(r => new
            {
                method = r.Verb,
                path = r.Path,
                parameters = r.Method.Signature.Params.Select(p => new { name = p.Name, type = Printer.PrintType(p.Type) }),
                returns = Printer.PrintType(r.Method.Signature.Return),
                effects = r.Method.Signature.Effects is { } fx ? (fx.Count == 0 ? "Pure" : string.Join(" ", fx)) : "(inferred)",
            }),
        }));

        foreach (var route in routes)
        {
            var r = route;
            app.MapMethods(r.Path, new[] { r.Verb }, async (HttpContext ctx) =>
            {
                JsonNode? body = null;
                if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContentType.Any(h => h?.Contains("json") == true))
                {
                    try { body = await JsonNode.ParseAsync(ctx.Request.Body); }
                    catch (JsonException ex) { return Results.Json(new { error = "BadRequest", message = "invalid JSON: " + ex.Message }, statusCode: 400); }
                }

                IReadOnlyList<Value> args;
                try
                {
                    args = BindArguments(it, r.Method, body, ctx.Request.Query);
                }
                catch (TrebPanic ex)
                {
                    return Results.Json(new { error = "BadRequest", message = ex.Message }, statusCode: 400);
                }

                Value result;
                try
                {
                    result = it.Call(r.Callable, args);
                }
                catch (TrebPanic ex)
                {
                    Console.Error.WriteLine($"panic in {r.Method.Signature.Name}: {ex.Message}");
                    return Results.Json(new { panic = ex.Message }, statusCode: 500);
                }

                Console.WriteLine($"{r.Verb} {r.Path} -> {result.Show()}");
                return ToResponse(result);
            });
        }

        var uiDir = Path.Combine(Path.GetFullPath(dir), "ui");
        if (Directory.Exists(uiDir))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uiDir),
                RequestPath = "/ui",
            });
            app.MapGet("/ui", () => Results.Redirect("/ui/index.html"));
        }

        Console.WriteLine($"Trebuchet dev server: root '{rootName}', api {api.Type.Name}, listening on http://localhost:{port}");
        if (Directory.Exists(uiDir)) Console.WriteLine($"  UI     http://localhost:{port}/ui/");
        foreach (var r in routes)
            Console.WriteLine($"  {r.Verb,-6} {r.Path,-16} {string.Join(", ", r.Method.Signature.Params.Select(p => $"{p.Name}: {Printer.PrintType(p.Type)}"))}");
        Console.WriteLine("  GET    /                route listing");
        app.Run();
        return 0;
    }

    private static (string verb, string path) SplitName(string name)
    {
        foreach (var v in new[] { "get", "post", "put", "delete", "patch" })
        {
            if (name.StartsWith(v, StringComparison.Ordinal) && name.Length > v.Length && char.IsUpper(name[v.Length]))
                return (v.ToUpperInvariant(), "/" + char.ToLowerInvariant(name[v.Length]) + name[(v.Length + 1)..]);
        }
        return ("POST", "/" + name);
    }

    // ------------------------------------------------------------ request binding

    private static IReadOnlyList<Value> BindArguments(Interpreter it, FnDecl method, JsonNode? body, IQueryCollection query)
    {
        var ps = method.Signature.Params;
        var args = new List<Value>();
        var recordParams = ps.Where(p => FindConstructor(it, p.Type) is not null).ToList();
        foreach (var p in ps)
        {
            JsonNode? node = null;
            if (query.TryGetValue(p.Name, out var q))
                node = JsonValue.Create(q.ToString());
            else if (body is JsonObject obj && obj.TryGetPropertyValue(p.Name, out var field))
                node = field;
            else if (recordParams.Count == 1 && recordParams[0] == p && body is JsonObject)
                node = body; // a single record parameter is the whole body
            if (node is null)
                throw new TrebPanic($"missing parameter '{p.Name}'");
            args.Add(FromJson(it, p.Type, node, p.Name));
        }
        return args;
    }

    private static ConstructorValue? FindConstructor(Interpreter it, TypeRef type)
    {
        if (type is not NamedType n) return null;
        foreach (var m in it.Modules.Modules)
            if (it.EnvOf(m).TryGet(n.Name, out var v) && v is ConstructorValue c) return c;
        return null;
    }

    private static NamespaceValue? FindUnion(Interpreter it, TypeRef type)
    {
        if (type is not NamedType n) return null;
        foreach (var m in it.Modules.Modules)
            if (it.EnvOf(m).TryGet(n.Name, out var v) && v is NamespaceValue ns) return ns;
        return null;
    }

    private static Value FromJson(Interpreter it, TypeRef type, JsonNode? node, string where)
    {
        var name = (type as NamedType)?.Name ?? throw new TrebPanic($"{where}: cannot bind a function-typed parameter from JSON");
        var args = ((NamedType)type).Args;
        switch (name)
        {
            case "String":
                return new StringValue(node is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? throw Missing(where));
            case "Int":
            {
                if (node is JsonValue iv)
                {
                    if (iv.TryGetValue<long>(out var l)) return new IntValue(l);
                    if (iv.TryGetValue<string>(out var ls) && long.TryParse(ls, out var parsed)) return new IntValue(parsed);
                }
                throw new TrebPanic($"{where}: expected an integer");
            }
            case "Float":
            {
                if (node is JsonValue fv)
                {
                    if (fv.TryGetValue<double>(out var d)) return new FloatValue(d);
                    if (fv.TryGetValue<string>(out var fs) && double.TryParse(fs, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return new FloatValue(parsed);
                }
                throw new TrebPanic($"{where}: expected a number");
            }
            case "Bool":
            {
                if (node is JsonValue bv)
                {
                    if (bv.TryGetValue<bool>(out var b)) return BoolValue.Of(b);
                    if (bv.TryGetValue<string>(out var bs) && bool.TryParse(bs, out var parsed)) return BoolValue.Of(parsed);
                }
                throw new TrebPanic($"{where}: expected true or false");
            }
            case "Instant":
            {
                var text = node is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : throw new TrebPanic($"{where}: expected an ISO-8601 instant string");
                return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                    ? new InstantValue(dt)
                    : throw new TrebPanic($"{where}: cannot parse instant \"{text}\"");
            }
            case "Vector" or "List":
            {
                if (node is not JsonArray arr) throw new TrebPanic($"{where}: expected an array");
                var elem = args.Count == 1 ? args[0] : throw new TrebPanic($"{where}: {name} needs one type argument");
                return new ListValue(Vector<Value>.From(arr.Select((x, i) => FromJson(it, elem, x, $"{where}[{i}]"))));
            }
            case "Map":
            {
                if (node is not JsonObject obj) throw new TrebPanic($"{where}: expected an object");
                if (args.Count != 2) throw new TrebPanic($"{where}: Map needs two type arguments");
                var b = Map<Value, Value>.Empty;
                foreach (var kv in obj) b = b.Set(FromJson(it, args[0], JsonValue.Create(kv.Key), $"{where}.{kv.Key}"), FromJson(it, args[1], kv.Value, $"{where}.{kv.Key}"));
                return new MapValue(b);
            }
            case "Option":
                return node is null || node.GetValueKind() == JsonValueKind.Null ? Builtins.None : Builtins.Some(FromJson(it, args[0], node, where));
        }

        if (FindConstructor(it, type) is { } ctor)
        {
            // a single-field record binds from a bare scalar, e.g. "boardroom" -> RoomId("boardroom")
            if (ctor.Fields.Count == 1 && node is JsonValue)
                return it.Construct(ctor, new[] { FromJson(it, ctor.Fields[0].Type, node, where) }, null, default);
            if (node is not JsonObject obj) throw new TrebPanic($"{where}: expected an object for {ctor.Name}");
            var named = new List<(string, Value)>();
            foreach (var f in ctor.Fields)
            {
                if (!obj.TryGetPropertyValue(f.Name, out var fieldNode)) throw new TrebPanic($"{where}.{f.Name}: missing");
                named.Add((f.Name, FromJson(it, f.Type, fieldNode, $"{where}.{f.Name}")));
            }
            return it.Construct(ctor, Array.Empty<Value>(), named, default);
        }

        if (FindUnion(it, type) is { } union)
        {
            var variantName = node switch
            {
                JsonValue v when v.TryGetValue<string>(out var vs) => vs,
                JsonObject o when o.TryGetPropertyValue("type", out var t) && t is JsonValue tv && tv.TryGetValue<string>(out var tvs) => tvs,
                _ => throw new TrebPanic($"{where}: expected a variant name or an object with a \"type\" field for {name}"),
            };
            if (!union.Members.TryGet(variantName, out var variant)) throw new TrebPanic($"{where}: {name} has no variant '{variantName}'");
            if (variant is RecordValue nullary) return nullary;
            var vctor = (ConstructorValue)variant;
            var named = new List<(string, Value)>();
            var obj = (JsonObject)node!;
            foreach (var f in vctor.Fields)
            {
                if (!obj.TryGetPropertyValue(f.Name, out var fieldNode)) throw new TrebPanic($"{where}.{f.Name}: missing");
                named.Add((f.Name, FromJson(it, f.Type, fieldNode, $"{where}.{f.Name}")));
            }
            return it.Construct(vctor, Array.Empty<Value>(), named, default);
        }

        throw new TrebPanic($"{where}: cannot bind type {Printer.PrintType(type)} from JSON");
    }

    private static TrebPanic Missing(string where) => new($"{where}: missing");

    // ------------------------------------------------------------ responses

    private static IResult ToResponse(Value result)
    {
        if (Builtins.IsOk(result, out var payload))
            return payload is UnitValue ? Results.NoContent() : Results.Json(ToJson(payload));
        if (Builtins.IsError(result, out var error))
        {
            var name = error is RecordValue r ? r.TypeName : "Error";
            var status = name switch
            {
                "BadRequest" => 400,
                "Unauthorized" => 401,
                "Forbidden" => 403,
                "NotFound" => 404,
                "Conflict" => 409,
                _ => 500,
            };
            var message = error is RecordValue { FieldValues.Length: > 0 } er ? er.FieldValues[0] : error;
            return Results.Json(new { error = name, message = ToJson(message) }, statusCode: status);
        }
        return result is UnitValue ? Results.NoContent() : Results.Json(ToJson(result));
    }

    public static JsonNode? ToJson(Value v) => ToJson(v, 0);

    private static JsonNode? ToJson(Value v, int depth) => v switch
    {
        IntValue i => JsonValue.Create(i.V),
        FloatValue f => JsonValue.Create(f.V),
        BoolValue b => JsonValue.Create(b.V),
        StringValue s => JsonValue.Create(s.V),
        UnitValue => null,
        InstantValue t => JsonValue.Create(t.Show()),
        ListValue l => new JsonArray(l.Items.Select(x => ToJson(x, depth + 1)).ToArray()),
        SetValue st => new JsonArray(st.Items.Select(x => ToJson(x, depth + 1)).ToArray()),
        MapValue m => MapToJson(m, depth),
        CellValue c => ToJson(c.Current, depth),
        RecordValue { Union: "Option" } o => o.TypeName == "Some" ? ToJson(o.FieldValues[0], depth) : null,
        RecordValue r when r.IsVariant && r.FieldValues.Length == 0 => JsonValue.Create(r.TypeName),
        // a nested single-field record such as RoomId("x") flattens to its value; the top-level object never does
        RecordValue r when depth > 0 && !r.IsVariant && r.FieldValues.Length == 1 && r.FieldValues[0] is StringValue or IntValue => ToJson(r.FieldValues[0], depth),
        RecordValue r => RecordToJson(r, depth),
        _ => JsonValue.Create(v.Show()),
    };

    private static JsonObject RecordToJson(RecordValue r, int depth)
    {
        var obj = new JsonObject();
        if (r.IsVariant) obj["type"] = r.TypeName;
        for (var i = 0; i < r.FieldValues.Length; i++) obj[r.FieldNames[i]] = ToJson(r.FieldValues[i], depth + 1);
        return obj;
    }

    private static JsonNode MapToJson(MapValue m, int depth)
    {
        var obj = new JsonObject();
        foreach (var kv in m.Entries)
        {
            var key = kv.Key switch
            {
                StringValue s => s.V,
                RecordValue { FieldValues.Length: 1 } r when r.FieldValues[0] is StringValue ks => ks.V,
                var k => k.Show(),
            };
            obj[key] = ToJson(kv.Value, depth + 1);
        }
        return obj;
    }
}
