# bookings-host

A plain C# ASP.NET project that embeds the Trebuchet bookings service. `Generated/` is
produced by the compiler and is safe to delete and regenerate:

```
dotnet run --project ../../compiler/src/treb -- emit ../bookings --out Generated --host
dotnet run --urls http://localhost:5090
```

`Program.cs` is the only hand-written file. It calls `AddTrebuchet_dev()` from the generated
`TrebuchetHost` class, resolves `BookingApi` from the container, and maps five routes.
