// valslöngva's host: an ASP.NET process that serves the browser UI and maps the Api service's
// methods to routes, the same convention the Trebuchet dev server uses. Everything under
// Generated/ is emitted from apps/valslongva by treb; this file and Externs/ are the C#.

using System.Diagnostics;
using Generated;
using Trebuchet.Runtime;

// `valslongva install` and `valslongva remove` work without a browser, for scripts and the curious.
if (args.Length > 0 && args[0] is "install" or "remove")
{
    Console.WriteLine(args[0] == "install" ? $"installed to {Valslongva.Setup.Install()}" : $"removed from {Valslongva.Setup.Remove()}");
    return;
}
var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5173;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://localhost:{port}");
builder.Services.AddTrebuchet_main();
builder.Services.ConfigureHttpJsonOptions(o => TrebuchetJson.Configure(o.SerializerOptions));
var app = builder.Build();

var api = app.Services.GetRequiredService<Api>();

app.MapGet("/", () => Results.Redirect("/ui/"));
var ui = Path.Combine(AppContext.BaseDirectory, "ui");
if (Directory.Exists(ui))
{
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(ui), RequestPath = "/ui" });
    app.MapGet("/ui", () => Results.Redirect("/ui/index.html")); // also matches /ui/
}

app.MapGet("/status", async () => Respond(await api.getStatus()));
app.MapGet("/search", async (string? query, string? sort, bool? deprecated, long? offset, long? limit) =>
    Respond(await api.getSearch(query ?? "", sort ?? "downloads", deprecated ?? false, offset ?? 0, limit ?? 50)));
app.MapGet("/package", async (string name) => Respond(await api.getPackage(name)));
app.MapGet("/installed", async () => Respond(await api.getInstalled()));
app.MapPost("/install", async (Named body) => Respond(await api.postInstall(body.name)));
app.MapPost("/loader", async () => Respond(await api.postLoader()));
app.MapPost("/reinstall", async (Named body) => Respond(await api.postReinstall(body.name)));
app.MapPost("/adopt", async (Adopt body) => Respond(await api.postAdopt(body.entry, body.name)));
app.MapPost("/uninstall", async (Named body) => Respond(await api.postUninstall(body.name)));
app.MapPost("/enable", async (Named body) => Respond(await api.postEnable(body.name)));
app.MapPost("/disable", async (Named body) => Respond(await api.postDisable(body.name)));
app.MapPost("/launch", async (Launch body) => Respond(await api.postLaunch(body.mode)));
app.MapPost("/gamepath", async (GamePath body) => Respond(await api.postGamePath(body.path)));
app.MapPost("/refresh", async () => Respond(await api.postRefresh()));
// the application managing its own installation: host business, not the mod manager's
app.MapGet("/setup", () => Results.Json(Valslongva.Setup.Status()));
app.MapPost("/setup", (SetupRequest r) =>
{
    try { return Results.Json(new { path = r.action == "remove" ? Valslongva.Setup.Remove() : Valslongva.Setup.Install(), Valslongva.Setup.IsInstalled }); }
    catch (Exception ex) { return Results.Json(new { error = "Setup", message = ex.Message }, statusCode: 500); }
});

var url = $"http://localhost:{port}/ui/";
Console.WriteLine($"valslöngva at {url}");
if (!args.Contains("--no-browser")) OpenBrowser(url);
app.Run();

static IResult Respond<T>(Result<T, AppError> result) => result switch
{
    Ok<T, AppError> ok => ok.value is Unit ? Results.NoContent() : Results.Json(ok.value),
    Error<T, AppError> err => Results.Json(new { error = err.error.GetType().Name, message = Valslongva_Application_Errors.messageOf(err.error) }, statusCode: Status(err.error)),
    _ => Results.StatusCode(500),
};

static int Status(AppError e) => e switch { BadRequest => 400, NotFound => 404, Conflict => 409, _ => 500 };

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        else Process.Start(new ProcessStartInfo("xdg-open", new[] { url }) { UseShellExecute = false })?.Dispose();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"open {url} in a browser ({ex.Message})");
    }
}

// Request bodies that are not Trebuchet records.
public sealed record Named(string name);
public sealed record Adopt(string entry, string name);
public sealed record Launch(string mode);
public sealed record GamePath(string path);
public sealed record SetupRequest(string action);
