// A plain C# ASP.NET host for the Trebuchet bookings service, compiled by `treb emit --host`.
// Everything under Generated/ is produced by the compiler; this file is what a C# team would
// write to embed it. The route mapping mirrors the interpreter's dev server.

using Generated;
using Trebuchet.Runtime;

var builder = WebApplication.CreateBuilder(args);
// Anything registered here, before AddTrebuchet_dev(), replaces the root's default of the same
// type. For example a host-owned clock:  builder.Services.AddSingleton<Clock>(new HostClock());
builder.Services.AddTrebuchet_dev();
// Trebuchet JSON shape: single-field ids flatten, nullary variants are strings, variants with fields carry "type".
builder.Services.ConfigureHttpJsonOptions(o => TrebuchetJson.Configure(o.SerializerOptions));
var app = builder.Build();

var api = app.Services.GetRequiredService<BookingApi>();

app.MapGet("/", () => Results.Json(new { host = "C# ASP.NET", api = nameof(BookingApi), routes = new[] { "POST /booking", "POST /confirm", "DELETE /booking", "GET /availability", "GET /bookings", "GET /page", "GET /stats", "GET /info" } }));
app.MapPost("/booking", async (BookingRequest req) => Respond(await api.postBooking(req)));
app.MapPost("/confirm", async (ConfirmRequest r) => Respond(await api.postConfirm(r.room, r.id)));
app.MapDelete("/booking", async (string room, string id, string reason) => Respond(await api.deleteBooking(room, id, reason)));
app.MapGet("/availability", async (string room, DateTimeOffset start, DateTimeOffset end) => Respond(await api.getAvailability(room, start, end)));
app.MapGet("/bookings", async (string room) => Respond(await api.getBookings(room)));
app.MapGet("/page", async (string room, long offset, long limit) => Respond(await api.getPage(room, offset, limit)));
app.MapGet("/stats", async () => Respond(await api.getStats()));
app.MapGet("/info", async () => Respond(await api.getInfo()));

app.Run();

static IResult Respond<T>(Result<T, ApiError> result) => result switch
{
    Ok<T, ApiError> ok => ok.value is Unit ? Results.NoContent() : Results.Json(ok.value),
    Error<T, ApiError> err => Results.Json(new { error = err.error.GetType().Name, message = Message(err.error) }, statusCode: Status(err.error)),
    _ => Results.StatusCode(500),
};

static string Message(ApiError e) => e switch
{
    BadRequest b => b.message,
    NotFound n => n.message,
    Conflict c => c.message,
    Internal i => i.message,
    _ => e.ToString(),
};

static int Status(ApiError e) => e switch { BadRequest => 400, NotFound => 404, Conflict => 409, _ => 500 };

// Request bodies that are not Trebuchet records.
public sealed record ConfirmRequest(string room, string id);
