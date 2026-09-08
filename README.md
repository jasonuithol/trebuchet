# Trebuchet

An immutable-first language that compiles to C# and to C++. Effects are inferred and checked
(`Nondet`, `Write`, `Suspend`), errors are values (`Result[T, E]` with `?`), state lives in
event folds, and services are composed at compile time. The prototype is a C# compiler with a
tree-walking interpreter, a C# backend, a C++20 backend, and one rule: every sample prints the
same line on all three.

```
service BookingService(store: RoomStore, clock: Clock)
  fn request(cmd: RequestBooking) -> Result[BookingId, BookingError]
    room = current(cmd.room)?
    events = handlers.request(cmd, room, clock.now())?
    store.append(cmd.room, events)?
    ok(cmd.id)
```

No `!` clause on `request`: the checker infers `Nondet Write Suspend` from the store. On .NET
it becomes `async ValueTask<Result<BookingId, BookingError>>`; on C++ a `Task<>` coroutine on
the runtime's event loop.

## Layout

| Path | What |
|---|---|
| `trebuchet-design-brief.md` | The original brief plus a dated addendum of every decision. |
| `trebuchet-implementation-strategy.md` | Decisions with reasoning, and findings from building the prototype (§7). Start here. |
| `trebuchet-syntax-sketch.md` | The tree-structured syntax, with an end-to-end acceptance domain. |
| `compiler/` | The prototype: lexer, parser, type and effect checker, interpreter, C# and C++ emitters, both runtimes, the `treb` CLI, and 176 tests. See `compiler/README.md`. |
| `examples/` | Samples that run on all three targets: `bookings` (an onion-layered API with a React UI), `orders` (the acceptance domain), `ffi`, `async`, `resources`, `factories`, `trees`, `supervision`, `collections`, `generics`. |
| `examples/bookings-host/` | A stock ASP.NET project embedding the generated C#. |
| `vscode-trebuchet/` | Syntax highlighting for `.treb` files. |
| `docs/` | The published strategy page and the wallpaper generator. |

## Quick start

Requires .NET 8 and, for the C++ target, g++ 13 with `-std=c++20`.

```
cd compiler
dotnet test                                                      # everything, on all three targets
dotnet run --project src/treb -- check ../examples/bookings      # type and effect check
dotnet run --project src/treb -- serve ../examples/bookings --root dev --port 5080
#   then open http://localhost:5080/ui/
dotnet run --project src/treb -- emit ../examples/bookings --out /tmp/gen --host && dotnet build /tmp/gen
dotnet run --project src/treb -- emit ../examples/bookings --out /tmp/cpp --target cpp
```

`dotnet pack` in `compiler/` produces the `treb` tool and the runtime package; `compiler/README.md`
has the install and the per-feature notes.

## Status

Milestones 1 to 7 of the brief are done in the prototype: parser and checker with generics,
persistent collections, effect inference, both backends, compile-time composition roots with
singleton, scoped, and resource lifetimes, handlers with `supervise` as the supervisor point,
and a C# host whose DI container supplies leaf dependencies. Open by decision: multi-threaded
execution on C++ and parallelism primitives. Everything else that is open is listed in §9 of the
strategy document.
