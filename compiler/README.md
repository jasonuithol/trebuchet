# Trebuchet compiler (prototype)

C# implementation of the Trebuchet compiler. Milestone 1 of the plan in
`../trebuchet-implementation-strategy.md`.

## Layout

```
src/Trebuchet.Compiler/Syntax/     Token, Lexer, Ast, Parser, Printer
src/Trebuchet.Compiler/Semantics/  ModuleSet (module loading), Types (semantic types, scopes, unifier),
                                   BuiltinSignatures (typed stdlib table), TypeChecker (types + effects)
src/Trebuchet.Compiler/Backends/   CSharpEmitter and CppEmitter: lower a checked program to C# or C++
src/Trebuchet.Runtime.Cpp/         trebuchet.hpp: the C++20 runtime (single header, standard library only)
src/Trebuchet.Runtime/             Vector (persistent trie), Map and Set (HAMT), Option, Result, Cell,
                                   Unit, Prelude (the stdlib as seen by generated C#)
src/Trebuchet.Compiler/Runtime/    Values, Interpreter (tree-walking), Builtins (prototype stdlib)
src/treb/                          command-line driver; Serve.cs is the HTTP host adapter
tests/Trebuchet.Compiler.Tests/    xunit: lexer, parser, round-trip over ../examples, interpreter
```

## Usage

```
dotnet run --project src/treb -- parse ../examples        # parse, report errors
dotnet run --project src/treb -- fmt <file>               # canonical formatting to stdout
dotnet run --project src/treb -- fmt --write <file|dir>   # rewrite in place
dotnet run --project src/treb -- tokens <file>            # dump the token stream
dotnet run --project src/treb -- check ../examples/bookings  # type and effect check, exit 1 on errors
dotnet run --project src/treb -- effects ../examples/bookings # print every function's inferred effects
dotnet run --project src/treb -- emit ../examples/bookings --out /tmp/gen && dotnet build /tmp/gen
dotnet run --project src/treb -- emit ../examples/bookings --out /tmp/gen --host   # + IServiceCollection registration
dotnet run --project src/treb -- emit ../examples/bookings --out /tmp/cpp --target cpp
g++ -std=c++20 -Isrc/Trebuchet.Runtime.Cpp -I/tmp/cpp ../examples/bookings/cpp/driver.cpp -o /tmp/cpp/driver
dotnet run --project src/treb -- serve ../examples/bookings --root dev --port 5080
dotnet test
```

Or install `treb` as a dotnet tool. `dotnet pack` writes `Trebuchet.Cli` and `Trebuchet.Runtime`
to `nupkg/`; the tool carries the C++ runtime header, and projects emitted by an installed tool
take a package reference to the runtime, so the feed must be reachable from them:

```
dotnet pack
dotnet tool install --global --add-source ./nupkg Trebuchet.Cli
treb check examples/bookings
treb emit examples/bookings --out /tmp/gen --host
dotnet nuget add source /path/to/compiler/nupkg --name trebuchet && dotnet build /tmp/gen
```

Generated C# carries `#line` directives, so panics and compiler diagnostics point at the
`.treb` file and line. `--no-lines` turns them off when you want to read the C#.

## Externs in the dev server

`treb serve` binds every `extern fn` with a `csharp` symbol to that static method by
reflection, loading the framework assembly that owns the symbol's namespace if needed, and
converts arguments and results between interpreter values and CLR values. An extern whose
symbol cannot be found keeps the "no implementation registered" panic.

## Running the bookings API

```
dotnet run --project src/treb -- serve ../examples/bookings --root dev --port 5080
curl -X POST localhost:5080/booking -H 'content-type: application/json' \
     -d '{"room":"boardroom","guest":"alice","start":"2026-10-01T10:00:00Z","end":"2026-10-01T11:00:00Z"}'
curl 'localhost:5080/availability?room=boardroom&start=2026-10-01T10:00:00Z&end=2026-10-01T11:00:00Z'
curl -X POST localhost:5080/confirm -H 'content-type: application/json' -d '{"room":"boardroom","id":"<id>"}'
curl -X DELETE 'localhost:5080/booking?room=boardroom&id=<id>&reason=plans%20changed'
curl localhost:5080/            # route listing
```

`serve` composes the named root, takes the entry named by `--api` (default `api`), and maps
each of that service's methods to a route by convention: the verb prefix of the method name
(`get`, `post`, `put`, `delete`) is the HTTP method and the rest, lower-cased, is the path.
A single record parameter binds from the whole JSON body; scalar parameters bind from the
query string or from body fields. `Result` maps to 200 with the Ok payload (204 for Unit),
or to a status chosen by the error variant's name (`BadRequest` 400, `NotFound` 404,
`Conflict` 409, otherwise 500). A panic inside a handler is caught at the request boundary
and returned as 500, which makes the boundary a supervisor point in the sense of the
strategy document. Rooms `boardroom` and `huddle` exist in the `dev` root; state is in memory
and lost on restart.

## What is implemented

- Indentation-aware lexer. Emits INDENT / DEDENT / NEWLINE; newlines inside brackets are
  ignored; tabs are rejected; comment-only and blank lines do not affect layout.
- Parser for every construct in `../trebuchet-syntax-sketch.md`: module and use, record,
  entity, union, fn, handler, service (scoped), shape, root; block and inline forms of
  with, if, match, lambda, list and map literals; trailing indented children as call
  arguments; explicit type arguments on calls; postfix `?`; effect flags.
- Printer that reproduces canonical source. The round-trip tests require that printing
  the parsed examples reproduces them exactly, modulo comments and blank lines, and that
  printing is a fixed point.

## The type checker

`Semantics/TypeChecker.cs` runs over a `ModuleSet` in five passes: declare type shells,
link imports, resolve signatures, check immutability, check bodies. `treb serve` refuses to
start on errors. It enforces:

- Name and type resolution across modules, including qualified access by module short name.
- Expression typing with local inference: literals, calls with arity and argument checks,
  generic builtins instantiated per call, overloads chosen by receiver type, lambdas whose
  parameter types come from the expected function type, `with` paths against record fields.
- `?` requires a `Result` operand and an enclosing function returning a `Result` with the
  same error type.
- Deep immutability: a record or union field may not contain an entity or a `Cell`.
- Exhaustive `match` over unions, `Option`, `Result`, and `Bool`.
- The Write boundary: a `fn` may not call a handler or anything declared `! Write`.
  Service methods and handlers may. Lambdas are unrestricted until the effect checker exists.
- Composition roots: every dependency resolves, shapes are satisfied structurally by
  member name and type, and function-typed dependencies must not have more effects than
  the parameter allows.

## The effect checker

Effects are inferred, not declared. Every function body, service method, lambda, field
initialiser, and root is a frame that records the calls it makes. After type checking, a
fixpoint over frames gives each function its effect set: the union of what it calls.
Rules, all with the offending call named in the message:

- A call is charged the callee's declared effects when it has a `!` clause, otherwise the
  callee's inferred effects. Declared effects are the contract; a body may do less.
- A builtin is charged its table entry plus the effects of any function-typed argument,
  since its body cannot be inspected. That is how a `Nondet` lambda makes `map` `Nondet`.
- A function value whose type gives no effects is assumed to have all three.
- A declared clause is an upper bound; `! Pure` means the body may have none.
- A `fn` may not have `Write`, inferred or declared. Handlers and service methods may.
- Field initialisers must be pure.
- A lambda checked against a function type with declared effects, a named function passed
  where such a type is expected, and an undeclared service method satisfying a shape
  member all become obligations checked after the fixpoint.

Not yet: unused-value warnings.

## The runtime

`Trebuchet.Runtime` is the library both the interpreter and generated C# run on. `Vector<T>`
is a 32-way bit-partitioned trie with a tail buffer (O(log32 n) get, set, append; concat is
O(n) until an RRB tree replaces it). `Map<K,V>` and `Set<T>` are hash array mapped tries with
collision nodes. Both have structural equality, so records holding them compare by value.
`Option<T>` and `Result<T,E>` are abstract records with `Some`/`None` and `Ok`/`Error`
variants, plus untyped `OkValue`/`ErrorValue`/`NoneValue` carriers with implicit conversions
so generated code can write `ok(x)` before the error type is known. `Cell<T>` is the mutable
box. `Prelude` mirrors the interpreter's builtins name for name.

## The C# emitter

`treb emit <dir> --out <outdir>` writes one `.cs` file per module and a project file that
references `Trebuchet.Runtime`. Everything lands in one namespace, `Generated`; each module's
functions live in a static class named after the module path (`Bookings_Domain_Fold`), which
importing modules pull in with `using static`. Records become positional C# records, or
records with an explicit validating constructor when a field has `init`. Entities become
classes. Unions become an abstract record with one sealed record per variant. Shapes become
interfaces, and a service implements every shape it structurally satisfies. Roots become a
factory method returning a record of the resolved entries.

Every function body is emitted as statements: `if` and `match` become `if`/`switch` with a
typed temporary, `?` becomes a pattern test and an early return, lambdas are statement lambdas
with explicit parameter types from the checker.

`Suspend` is lowered to `async ValueTask<T>`. A function whose effects (declared or inferred)
include `Suspend` is emitted async and every call to it is awaited; shape members and
function-typed values follow the same rule, so `fn() -> T ! Suspend` becomes
`Func<ValueTask<T>>`. A service method that implements a suspending shape member is emitted
async even if its own body never suspends, so the interface is satisfied. A lambda whose body
suspends becomes an async lambda, and a higher-order builtin receiving one is called by its
`Async` variant (`forEachAsync`, `mapAsync`, ...). Pure code stays synchronous.
`EmitterTests` emits the bookings program, builds it with `dotnet build`, and runs the
interpreter's scenario against it, unwrapping the `ValueTask` results.

Known limits: one namespace means type names must be unique across the program; hoisted
`?` and `match` temporaries can reorder evaluation relative to sibling arguments; `with` on
an entity is not supported.

## Hosting from C#

`treb emit --host` adds `TrebuchetHost.cs`, with one `AddTrebuchet_<root>()` extension method
per composition root. Every entry becomes a keyed factory registration (keyed by entry name)
plus a `TryAdd` registration under its own type and, for services, under each shape it
satisfies. A service's dependencies resolve from the container by declared type first and
fall back to the root's entry, so anything the host registers *before* calling
`AddTrebuchet_<root>()` replaces the root's default:

```csharp
builder.Services.AddSingleton<Clock>(new MyClock());   // host-supplied leaf wins
builder.Services.AddTrebuchet_dev();                    // everything else from the root
```

`scoped` services register scoped, resources are disposed by the container, and the
`Root_<name>` record is built from the container on first use. `examples/bookings-host/` is
a plain ASP.NET project whose only hand-written file is `Program.cs`: it calls
`AddTrebuchet_dev()`, resolves `BookingApi`, and maps five routes. The DI abstractions come
from the ASP.NET shared framework, so the generated project takes a framework reference when
`--host` is used.

## Resources

```
resource service Handle(name: String, log: Cell[Vector[String]])
  fn touch() -> Unit
    ...
  fn release() -> Unit
    ...

handler openAndClose(log: Cell[Vector[String]]) -> Unit
  use a = Handle("a", log)
  use b = Handle("b", log)
  a.touch()
  b.touch()
```

A `resource service` must define `fn release() -> Unit` (any effects). A fresh resource must
be bound with `use`; it is released at the end of the block in reverse order, including on
`?` early return and on panic. `use` charges `release`'s effects to the block. Records and
unions cannot hold resources. In C# a resource implements `IDisposable` (or
`IAsyncDisposable` when `release` suspends) and `use` becomes `using`/`await using`; in C++
the destructor calls `release`. A singleton depending on a `scoped` service is a compile
error. `examples/resources/` is the sample.

## Local functions

A `fn` (or, inside a handler or service method, a `handler`) may be declared inside a body. It
sees the enclosing parameters and earlier locals, may recurse, and inherits the enclosing
constraints. C# emits a local function; C++ a `std::function` bound to a lambda (a template
lambda when the local is generic, which then cannot recurse).

## Lazy sequences and property tests

`Seq[T]` is lazy and may be infinite: `Seq.iterate(seed, f)`, `Seq.range(from, to)`,
`Seq.from(vector)`; `map`, `filter`, `take`, `drop`, `takeWhile` stay lazy; `toVector`, `first`,
`fold` pull. A function given to a lazy combinator may not `Suspend`. `?` also works on an
`Option` inside a function that returns an `Option`. `examples/lazy/` is the sample.

`treb test <dir> [--cases 100] [--seed N]` runs every function named `prop*` that returns
`Bool`, generating arguments from the parameter types and shrinking a failing case before
reporting it. `examples/properties/` has seven; the runner is `Trebuchet.Compiler.Testing`.

## Shapes over types

A shape with one type parameter is a type class. `instance Shape[Type]` supplies its members
for a record, union, or primitive; `[T: Shape]` constrains a type parameter; `Shape.member(args)`
calls through the shape, with `Shape.member[T]()` when no argument fixes `T`. The checker
resolves every constrained call to an instance after all bodies are checked, and both emitters
pass the instance as a hidden trailing argument: on C# an interface `Shape<T>`, a class
`Shape_Type` with a singleton, and a `Shape<T> __Shape_T` parameter; on C++ a struct and an extra
deduced template parameter. `Ord` is built in (`compare`), with instances for the primitives;
comparison operators on a `T: Ord` parameter lower to it, as do `sort`, `minimum`, and
`maximum`. The interpreter runs the checker at construction to read the same resolution.
`examples/classes/` is the sample. Not yet: multi-parameter classes, superclasses, instances for
generic types, passing a constrained function as a bare value.

## Patterns

Constructor patterns take named fields and a trailing `...`: `Held(guest: g, ...)`,
`BookingCancelled(id, ...)`; records match too, `Point(x: 0, y: 0)`. Match arms take guards (`Some(x) if x > 0 =>`), list patterns (`[]`, `[a, b]`, `[first,
...rest]`, `[a, ..._]`), tuple patterns, and as-patterns (`whole @ Rect(w, h)`). Tuples are
`(A, B)` types with `(a, b)` values and `p.0` access. A binding line may be an irrefutable
pattern: `(lo, hi) = minMax(xs)`. Guarded arms do not count for exhaustiveness; a vector match
is exhaustive when a rest arm covers every length from some k and each shorter length has an
exact arm. `examples/patterns/` is the sample. C# lowers to switch patterns with `when`; C++ to
a matched flag per arm so guards can see bindings.

## Visibility

A declaration marked `private` is not exported: another module cannot name it, unqualified or
qualified, and a private union hides its variants. A `private` service method can be called
only from the service's own methods. Errors are reported at the use site.

## Supervision

`supervise expr` is the supervisor point: a panic raised while evaluating `expr` becomes
`Error(Panic(message))`, so the result is `Result[T, Panic]`. It is allowed in handlers and
service methods only; a `fn` cannot observe a panic. Resources bound with `use` inside are
released during the unwind. C# lowers to `try`/`catch (TrebPanic)`, C++ to `catch (const
treb::Panic&)`, and the interpreter catches the same exception. `examples/supervision/` is the
sample; the bookings API wraps its command in one and maps the panic to `Internal`.

## Factory synthesis

A service dependency of function type whose return type is a service is synthesised by the
root when no entry provides it: each parameter fills the first unfilled constructor
parameter of the target whose type matches, in order, and the remaining constructor
parameters resolve from the root as usual. `examples/factories/` shows a `Client` that opens
`Conn`s by name while the root supplies the config and log `Conn` also needs. The C# host
registration synthesises the same lambda unless the host registered a `Func` first.

## Generics

Type parameters go in square brackets after a name: `record Pair[A, B](...)`, `union
Either[L, R]`, `fn swap[A, B](p: Pair[A, B]) -> Pair[B, A]`. Inside a declaration a
parameter is a rigid type: `x + 1` on `x: T` is an error. Calls infer the arguments, or
state them as `describe[Int](...)`. C# emits generic records, unions, and methods; C++ emits
templates. Recursive generic unions are not supported by the C++ backend yet, because a
`std::variant` cannot contain itself without boxing.

## Effect polymorphism

A function that calls one of its own function-typed parameters, where that parameter's type
has no `!` clause, inherits the parameter's effects from each caller: `mapSecond(p, f)` is
pure when `f` is pure and suspends when `f` suspends. The checker records which parameters a
function calls or passes on, and the fixpoint adds the matching argument's effects at every
call site. `! Pure` on such a function means "adds nothing beyond its arguments". Because C#
and C++ cannot be polymorphic over async, the emitters produce a synchronous version and an
`Async` twin of every such function and choose the twin when a suspending argument is passed,
which is the same split the Prelude already has for `map` and `mapAsync`.

## Externs

```
extern fn readTextFile(path: String) -> Result[String, FileError] ! Nondet Suspend
  csharp "System.IO.File.ReadAllTextAsync"
  cpp "host::readTextFile"
  catch csharp "System.IO.IOException" -> Unreadable
  catch cpp "std::ios_base::failure" -> Unreadable
```

An extern must declare its effects. Each target names the host symbol; a `catch` line maps a
host exception type to a variant of the error type that has exactly one `String` field,
receiving the message. Any other exception becomes a panic. If the extern returns a `Result`,
the host function returns the payload and the wrapper wraps it in `ok`. A `Suspend` extern
awaits the host call in C# (so the host must return a Task or ValueTask) and is a coroutine
in C++. The interpreter resolves externs from `Interpreter.Externs`, keyed by
`module.name`; a host process fills that dictionary before running. `examples/ffi/` shows
the whole thing; its C++ driver declares the `host::` functions before including the header.

## JSON

`TrebuchetJson.Configure(options)` adds a `System.Text.Json` converter with the dev server's
shape: a single-field record such as `RoomId("x")` flattens to `"x"` when nested and stays
`{"value":"x"}` at the top level; a nullary variant is its name (`"Held"`); a variant with
fields is `{"type":"BadRequest","message":"..."}`; `Option` is null or the value; a `Map` is a
JSON object when its key is a string, a number, or a single-field record over one, else an
array of `{key, value}` pairs; `Vector` and `Set` are arrays. Generated records and unions are
tagged with `[TrebuchetRecord]`, `[TrebuchetUnion]`, and `[TrebuchetVariant]` so the converter
only touches Trebuchet types. The host sample registers it with `ConfigureHttpJsonOptions`.

On .NET, every extern result passes through `Boundary.To<T>`: arrays and enumerables become
`Vector`, dictionaries become `Map`, null becomes `None` when the declared type is `Option`
(and a panic otherwise), and integers and dates widen to `long` and `DateTimeOffset`.
`Vector<T>` implements `IReadOnlyList<T>` and `Map<K,V>` implements
`IReadOnlyDictionary<K,V>`, so Trebuchet values pass into host signatures unchanged.

## The C++ backend

`treb emit --target cpp` writes a single `generated.hpp` against `trebuchet.hpp`. Each module
is a namespace under `gen` that imports its dependencies with using-directives; every module
is also re-exported into `gen` so a host can write `using namespace gen;`. Records are
structs with defaulted `operator==` and a `std::hash` specialisation, so they work as map
keys. Unions are `std::variant` over one struct per variant, and a variant value is always
wrapped in its union type. Shapes are abstract classes with pure virtual const methods;
services are classes held by `std::shared_ptr` that inherit every shape they satisfy. Roots
build the object graph with `std::make_shared`. `?` becomes an early return of `ErrorValue`,
which converts to any `Result` with that error type; `match` becomes an if-chain over
`std::holds_alternative` and `std::get`; `with` becomes a copy-and-assign lambda. `Suspend`
is lowered as blocking, which is what milestone 4 specified.

The runtime is one C++20 header, standard library only: a persistent vector trie, a hash
array mapped trie for maps and sets, `Option`, `Result`, `Cell`, `Instant`, and the Prelude
as templates. Sharing is through `treb::Rc`, a non-atomic reference-counted pointer with a
converting constructor from derived to base, which is what the single-threaded prototype
needs. `Suspend` lowers to `treb::Task<T>`, a lazy coroutine with symmetric transfer: a
function whose effects include `Suspend` returns `Task` and is `co_await`ed, exactly the C#
rule. Tasks run on `treb::EventLoop`, one per thread, with a ready queue, a timer heap, and a
thread-safe `post` queue that is the only way work enters from another thread (which is what
keeps the non-atomic refcounts valid). `Task::get()` schedules the task and steps the loop until
it completes, so a driver or a root consumes a task the same way as before; it panics with a
deadlock message if the loop runs dry first. `sleep(ms)` suspends on a timer. For hosts,
`spawn(task)` returns a joinable handle and `Completion<T>` / `pending()` adapt a callback API:
move the resolver into the callback, `co_await` the pending side, and a resolver dropped
unresolved fails the waiter instead of hanging. `examples/async/cpp/driver.cpp` shows both
(the language runs `tick` sequentially, the host spawns two and sees timer order).
A field whose type mentions its own record or union is emitted as `treb::Box<T>`, a
reference-counted value that converts to and from `T`; field access and destructuring
dereference it, so recursive unions and records (`examples/trees/`) need nothing from the
author. `examples/bookings/cpp/driver.cpp` runs the
interpreter's scenario against the generated header and `CppEmitterTests` compares its output
line by line. `src/Trebuchet.Runtime.Cpp/tests/runtime_tests.cpp` checks the runtime itself
and `CppRuntimeTests` compiles and runs it.

Known limits: records within a module must be declared before use (no forward declarations
for by-value members); entities are emitted as plain structs; `json.decode` is unavailable;
a record whose fields include a function cannot be compared; recursive generic unions need
boxing that is not generated yet.

## The interpreter

`Runtime/Interpreter.cs` is a tree-walking evaluator, added ahead of the C++ backend so the
language could be exercised end to end. It is dynamically typed: the type checker does not
exist yet, so a type error surfaces as a runtime panic. Effects are parsed and carried on
signatures but not enforced, and `Suspend` runs synchronously. Values are immutable with
structural equality, except `Cell`, which has reference identity. It runs on the persistent collections in
`Trebuchet.Runtime`, so the collections are exercised by every interpreter test and by the
dev server.

Semantics the interpreter fixed that the documents had left loose:

- `x.f(a)` resolves a real member first (namespace entry, service method, record field) and
  otherwise is sugar for `f(x, a)`. `x.f` with no call is the same, with no extra arguments.
- Variant patterns destructure positionally; binder count must equal field count.
- `?` on an `Error` returns that `Error` from the enclosing function or lambda.
- A composition root resolves a service's dependencies by parameter name against the root's
  entries, then by type name, then by constructing an unlisted service type implicitly.
- `init` validations run in declaration order with `value` bound and earlier fields in scope.

## Not yet implemented

- `fmt` keeps comments but normalises blank lines.
- `json.decode`, database access, and anything the Postgres adapter needs; the `dev` and
  `test` roots use the in-memory store.
- Vector builtins `take`, `drop`, `at`, `sortBy`, `traverse` exist; there is no slicing syntax.
- A set literal; `toSet([...])` builds one. Set builtins: `toSet`, `add`, `remove`, `contains`,
  `length`, `isEmpty`, `items`, `merge`, `intersect`, `difference` (`examples/collections/`).
- Multi-threaded execution on C++; the loop is single-threaded by decision.

## Syntax decisions the parser has taken

- `then` is optional before a newline on a block-form `if`; the printer always emits it.
- `_` is the wildcard pattern.
- A bare name followed by indented children is a call with no parentheses (`ok` + items).
- Named arguments `f(x: 1)` are accepted inline as well as in block form.
