# Trebuchet — Implementation Strategy

> Companion to `trebuchet-design-brief.md`. The brief set out the goals and listed the open questions. This document records the decisions taken on those questions, the reasoning behind each, and the revised milestone plan. It is intended for external review before a prototype compiler is started.

**Status:** draft for feedback. Revised 2026-09-06 with findings from a worked sample (§7.1) and from a running prototype (§7.2).

**How to read this.** Each section states a decision, the alternatives that were considered, and why the decision was taken. Section 9 lists what remains deliberately undecided. Section 10 lists the specific questions on which feedback is sought. All syntax is illustrative; concrete syntax is not yet designed.

---

## 1. Decisions at a glance

| Question from the brief | Decision |
|---|---|
| Mutation model | Functional updates only. No `mut`, no builders. Stdlib ships a fold family. |
| Effect set | Three orthogonal flags: `Nondet`, `Write`, `Suspend`. No aliases. `Scope` is not an effect. Inferred; annotations optional and checked as bounds; `! Pure` asserts none. |
| Exceptions | `Result<T, E>` for recoverable errors. Panics for unrecoverable ones, catchable only at supervisor points. |
| Composition roots | Compile-time resolution only. Runtime and plugin roots deferred. |
| Service lifetimes | Singleton and scoped only. Transient replaced by factory dependencies. Scope is one handler or query invocation. |
| Async lowering on C++ | C++20 coroutines. Single-threaded event loop and non-atomic refcounts for the prototype. |
| .NET backend form | Emits C# source, compiled by Roslyn. Direct IL is a possible later replacement. |
| Concrete syntax | Sketched in `trebuchet-syntax-sketch.md`. Tree-structured, conventional symbols. |
| Module system | Proposed: one module per file, `use` imports, qualified names for clashes. |
| Mutable state | No language-level mutation. A stdlib `Cell[T]` entity with effectful operations is the runtime-level escape hatch. |

---

## 2. Mutation model

**Decision.** There is no mutation. `with` expressions with deep paths, plus persistent collections, are the only way to derive a new value from an old one. Loops that accumulate are written as folds.

**Alternatives considered.**

- *Scoped `mut` locals.* Rejected for now. Cheap to add later and painful to remove, and adding it before deep `with` and persistent collections exist means never learning whether it was needed.
- *Compiler-generated builders.* Rejected. Doubles the type surface and duplicates what deep `with` provides.
- *In-place reuse via refcount uniqueness (Perceus-style).* Accepted as a backend optimisation, not a language feature. When a value's refcount is one, `with` and collection updates may mutate in place with no observable difference. This is free on the C++ backend and unavailable on .NET, which has no refcounts.

**Stdlib obligation.** Since `for` does not exist, the collection API must make its absence painless. Minimum fold family:

- `fold`, `foldRight`, `reduce`, `scan`
- an early-exit form (`foldWhile` or equivalent)
- `map`, `filter`, `flatMap`, `zip`, `groupBy`, `partition`

Event replay is a fold over events with `apply` as the combining function, which is why event sourcing is the natural shape rather than a bolt-on.

**Known pain points.** Loops with early exit, and loops with several accumulators. The first needs the early-exit fold. The second becomes a fold over a small record, which is acceptable but noisier than three locals.

---

## 3. Effect system

**Decision.** A function signature carries a set of zero or more of three independent flags. Pure is the empty set.

| Flag | Meaning | Compiler consequence |
|---|---|---|
| `Nondet` | The result may depend on state not in the arguments | Cannot be memoised, merged, or reordered |
| `Write` | Something other than the return value depends on the call having happened | Cannot be dropped, duplicated, or reordered relative to other `Write` calls |
| `Suspend` | May not complete synchronously | Lowered as a coroutine / `ValueTask` |

**Rules.**

- Effects are inferred from the body. A `!` clause on a signature is optional; when present it is checked as an upper bound (declaring more than the body needs is allowed, less is an error at that function). `! Pure` asserts the empty set. Declarations without bodies, meaning `shape` members, function types, and interop declarations, must carry a clause. (Revised 2026-09-06; see §7.3.)
- A function may only call functions whose flags are a subset of its own.
- Memoisation, reordering, and parallelisation require neither `Nondet` nor `Write`. `Suspend` alone is memoisable.
- The flags are independent. There is no lattice. `Write` does not imply `Nondet`.
- No aliases. `IO` does not exist.

**Classification examples.**

| Function | Flags |
|---|---|
| `apply(state, event)` | none |
| `now()` | `Nondet` |
| `random()` | `Nondet` |
| `sleep(duration)` | `Suspend` |
| `parMap(xs, pureFn)` | `Suspend` |
| Content-addressed fetch returning `Bytes` | `Suspend` |
| Content-addressed fetch returning `Result<Bytes, E>` | `Nondet Suspend` |
| HTTP GET | `Nondet Suspend` |
| Database insert | `Nondet Write Suspend` |
| `race(a, b)` over pure suspending work | `Nondet Suspend` |
| Allocating or comparing an `entity` by reference | `Nondet` |

**Why properties rather than capabilities.** The brief proposed `IO`, `Write`, and possibly `Time`, `Random`, `Scope`. Those are capabilities: they name what a function accesses. Every decision the compiler needs to make is about a property: can this be cached, can this be reordered, does this need async lowering. Tracking properties directly means the taxonomy stops growing. A new source of non-determinism is simply `Nondet`; it does not need a new named effect.

**Why `Nondet` and not `WorldRead`.** "World read" invites the reading "touches something outside the process", which is both too narrow (scheduler timing and allocator identity are non-deterministic without leaving the process) and too broad (a content-addressed fetch reads the world but is deterministic on success). `Nondet` names the property the compiler checks.

**Time and randomness.** There is no ambient clock or generator. A function that needs the current time either takes an `Instant` as an argument (preferred, and pure) or is handed a `Clock` service whose `now()` is `! Nondet`. Tests inject a fixed clock. Substitution is done by injection, not by effect handlers.

**CQRS mapping.**

| Construct | Allowed flags |
|---|---|
| `record` | none (data has no behaviour) |
| `fn` (query) and `apply` (event fold) | anything except `Write` |
| `handler` and `service` | any |

A query may read the clock. Only a handler may write.

---

## 4. Errors and panics

**Decision.** Two channels, cleanly separated by recoverability.

- **`Result<T, E>`** for anything a caller might handle. `E` is a record or sealed union, matched exhaustively. A propagation operator (spelling to be decided; `?` is the placeholder) returns early on the error case. This operator is mandatory: without it every call site is a match expression and the `Result` decision will not hold.
- **Panics** for bugs and invariant violations: division by zero, index out of range, a content-addressed fetch that cannot be satisfied. A panic is bottom, not an effect, so pure functions may panic. Memoisation does not cache a panic.

**Catching.** Ordinary code cannot catch a panic. The language names a small set of supervisor points that can: handler dispatch, the composition root, and the interop edge. This lets a server log one failed request and continue serving.

**Alternatives considered.**

- *No catch at all.* Rejected. Forces every handler to wrap everything in `Result` to get robustness back.
- *Full exceptions in effectful code.* Rejected. Recreates C#'s two competing error channels.
- *Exceptions as an effect (`Throws<E>`).* Rejected. This is `Result` with different spelling and only pays off with general effect handlers, which are out of scope.

**Backend consequences.**

- C++: catching at supervisor points requires unwinding so refcounts are released. C++ exceptions are enabled for this purpose only. Generated code never throws or catches otherwise.
- .NET: calling into .NET, undeclared exceptions become panics; the boundary declaration may map named exception types to `Result` errors. Calling out to a C# host, a panic surfaces as a thrown exception.

---

## 5. Services, composition roots, and lifetimes

### 5.1 Composition roots

**Decision.** Compile-time resolution only. The root names concrete types. The compiler walks the constructor graph, checks that every dependency has exactly one provider, checks lifetimes, and emits direct construction code. Missing dependencies, cycles, and captive dependencies are compile errors. No container, no reflection.

**Cases that appear to need runtime resolution, and how they are handled without it.**

- *Configuration-driven selection:* a branch in the root.
- *Plugins:* a function-typed or record-of-functions dependency. Structural typing means a record of functions already satisfies a service interface.
- *Existing .NET hosts:* deferred to milestone 7. Trebuchet services register into `IServiceCollection` but resolve internally, treating the host container as a source of leaf dependencies. A `dynamic` root for true host-side resolution is a planned extension for the .NET backend only, and its shape will be decided once a working compile-time root exists to compare against.

**Why not decide the dynamic root now.** A dynamic root needs runtime identity for structural interfaces: the container must decide at runtime whether a plugin type satisfies `Clock`. That means either reflection or compiler-emitted witness tables, and it leaks into the C++ backend design. Deferring keeps the C++ backend reflection-free through the prototype.

### 5.2 Lifetimes

**Decision.** Two lifetimes.

- **Singleton.** Shared by everyone. Inferred for any service whose constructor is pure and whose dependencies are all singletons. External-state singletons (connection pools, caches) must be safe for concurrent use; the compiler cannot check this.
- **Scoped.** One instance per handler or query invocation. Declared with a keyword. Resources bound in the scope are released at its end. A scope is not a transaction: a transaction is a resource with an explicit commit (revised 2026-09-08; see §7.18).

**Transient is replaced by factory dependencies.** Per-use instantiation is a fact about a call site, not a type. A service that needs a fresh resource each time takes a function:

```
service Exporter(openOutput: fn(Path) -> FileStream ! Nondet Suspend)
  fn export(orders: Vector[Order], path: Path) -> Result[Unit, ExportError] ! Write Suspend
    stream = openOutput(path)?
    ...
```

`Exporter` is a singleton holding a function. Each `export` call gets a fresh stream. The root supplies the function either by hand or by synthesis: if a dependency is function-typed, returns a service type, and that service's remaining constructor parameters are resolvable from the root, the compiler generates the lambda.

**Effectful construction.** Opening a connection is `Nondet Suspend`, so constructors may carry effects and the root is an effectful context. Singletons construct at startup; scoped services at scope start.

**Release.** A resource-owning type is marked `resource service` and must define `fn release() -> Unit`. A fresh resource is bound with `use x = expr`, which releases it at the end of the enclosing block in reverse binding order. On C++ release also happens when the refcount reaches zero. (Spelled and implemented 2026-09-08; see §7.11.)

**Checks.** A singleton capturing a scoped service is a compile error.

---

## 6. Async lowering and the C++ memory model

**Decision.** `Suspend` functions lower to C++20 coroutines. A `Suspend` function returns a task type; each call to a `Suspend` function is emitted as `co_await`; results are `co_return`. Everything else is a plain call. This is the same shape as the .NET lowering to `ValueTask`, so both backends share one design.

**Why coroutines.**

- The `Suspend` flag already identifies exactly the functions that need transforming, so the lowering is mechanical.
- Coroutine frames hold their locals on the heap, so refcounted values captured across a suspension stay alive without extra work.
- Matches how modern C++ networking libraries expose async, so the interop boundary is natural.

**Alternatives considered.**

- *Blocking.* `Suspend` checked but lowered synchronously. Correct and sufficient for milestone 4, which is the pure subset. Used until the first `Suspend` function exists.
- *CPS transform in the Trebuchet compiler.* Rejected. Full control but unreadable generated code and harder debugging.
- *Stackful fibres.* Rejected. Zero compiler work, but per-fibre stacks and no composition with coroutine-based C++ libraries.

**Refcounting under concurrency.** Once code runs on more than one thread, refcounts must be atomic, at roughly five to twenty times the cost of a plain increment. Immutability makes sharing safe; it does not make counting cheap. Options are atomic everywhere (Swift), a single-threaded event loop with non-atomic counts (Node), or thread-confined values with atomic handoff at boundaries.

**Prototype choice.** Single-threaded event loop, non-atomic refcounts. Multi-threaded execution is deferred and the atomic-versus-confined choice is recorded as an open item (section 9).

**Parallelism is separate from async.** `parMap` over pure functions is `Suspend` but not `Nondet`. On a single-threaded loop it is `map`. The parallelism story can be designed independently of the async story and should not be conflated with it.

---

## 7. Cross-cutting consequences

Decisions above that change something elsewhere in the brief.

- The brief's structural interface example `Clock { now() -> Instant }` is wrong: `now()` must be `! Nondet` or the compiler is entitled to memoise it.
- The brief's lifetime inference from a `Scope` effect is replaced by inference for pure-constructor services plus an explicit `scoped` keyword. Same errors, less magic.
- The interop boundary must default to all three flags for .NET calls, with the user narrowing declarations where they know better.
- A resource-ownership marker on types is a new small language feature required by lifetimes.
- A `use` block or equivalent for early release is required.
- C++ exception support must be enabled in generated code for supervisor-point catching.

### 7.1 Findings from a worked sample

A room-booking API was written in the sketch syntax and split into onion-architecture layers (`examples/bookings/`, 19 files across domain, application, infrastructure, api, and host). Writing it exposed four gaps that neither the brief nor the earlier sections covered.

**Module system.** Splitting one file into twenty needs one, and nothing had been said about it. Proposal:

- `module a.b.c` is the first line of every file. One module per file.
- `use a.b.c` imports the module's public declarations unqualified.
- A qualified reference by the last segment, `handlers.request(...)`, resolves a clash. The sample hit one immediately: the service method `request` calls the domain handler `request`.
- All declarations are public by default. A `private` keyword hides one. This is tentative.
- Milestone 1 grows to include module headers, imports, and name resolution.

**Mutable state.** An in-memory test store and a sequential id generator both need somewhere to keep state, and §2 removed every language-level way to do that. The sample assumes a stdlib `Cell[T]`:

- `Cell.new(x)` is `! Nondet` because it allocates reference identity.
- `get()` is `! Nondet`. `set(x)` and `update(f)` are `! Write`.
- A `Cell` is an `entity`, so it has reference identity and is excluded from structural equality.

This does not reopen §2. There is still no mutation in the language: a `Cell` cannot be touched from pure code, and services remain immutable since they capture the cell, not its contents. It is the runtime-level escape hatch, and it is the shape every test double, cache, and in-memory adapter will use. On the single-threaded loop it is trivially safe; multi-threaded execution adds it to the atomic-versus-confined question in §9.

**Explicit type arguments on calls.** Decoding a row needs `json.decode[RoomEvent](payload)`. The syntax sketch uses square brackets for type arguments in types but had not said they can appear on a call. They can, with the same brackets.

**Small syntax gaps.** A zero-argument lambda is written `\-> expr`. A service method and a top-level function may share a name, with the nearest scope winning and qualification available. Both are recorded in the syntax sketch's open choices.

### 7.2 Findings from the running prototype

Milestone 1's parser and a tree-walking interpreter were built in C# (`compiler/`), and the bookings sample runs as an HTTP service through them (`treb serve`). The interpreter was pulled forward from its "optional" status because it turned the sample into something that can be exercised, and it is not a replacement for the C++ backend. Observations:

- **The tree rule held without special cases.** A line's indented children always belong to the last expression on that line. The parser reproduces all twenty sample files exactly.
- **Positional destructuring in patterns.** The sample had used `OrderPlaced(e)` to bind the whole variant. That is ambiguous with binding a single field. Decision: variant patterns destructure positionally, `OrderPlaced(_, customer, lines)`, with `_` as wildcard. An as-binding form is a syntax open choice.
- **Method-call sugar needs a priority rule.** `x.f(a)` first looks for a real member on `x` (a namespace entry, a service method, a record field) and only then falls back to `f(x, a)`. Without that order, a record field named the same as a stdlib function would be unreachable.
- **`Cell` behaves as specified.** Reads are `Nondet`, writes are `Write`, and the in-memory store and id generator needed nothing else. The interpreter does not yet enforce the flags, so this is a semantic check, not a compiler one.
- **Panic at the request boundary is the right supervisor point.** A `fail` inside a handler surfaces as an HTTP 500 and the server keeps serving. It also showed that validation of user input must be a `Result`, not a panic, or every bad request is a 500. The sample's API now checks its inputs before constructing domain records.
- **Composition-root resolution rules.** Dependencies resolve by parameter name against root entries, then by type name, then by implicit construction of an unlisted service. Cycles are an error. This matches §5.1 and needed no runtime container.
- **JSON at the edge is a host-adapter concern.** Single-field records such as `RoomId("x")` flatten to their value when nested and stay objects at the top level. Unions serialise with a `type` field. This belongs in the .NET interop layer of milestone 7, not the language.

### 7.3 Effect annotations are optional

The bookings sample showed every service method wearing `! Nondet Suspend` or `! Write Suspend`, inherited from the store's shape, the same way every C# method touching a repository ends up `async Task`. The brief's "the signature is the boundary" rule was for readability, not necessity: effects are a set union over callees, so the compiler can infer them, with a fixpoint for recursive groups. Koka does exactly this.

Decision:

- Annotations are optional and inferred when omitted.
- When present they are an upper bound, checked at that function.
- `! Pure` asserts the empty set, and cannot be combined with other flags. A `Pure` handler is rejected, since handlers are the `Write` sites.
- `shape` members, function types, and interop declarations must carry a clause, because there is no body.

What this gives up is the contract: a change deep in an implementation can widen the effects of everything above it, and the error appears wherever a pure caller finally objects. Two things limit the damage. `Write` cannot leak by accident, because a `fn` is never allowed to carry it, so the error appears at the function that started writing. And `Nondet` or `Suspend` leaking upward only costs optimisation, not correctness. For published packages, a lint requiring annotations on exported declarations restores the contract where it matters; that is a package-level rule for later, not a language rule.

The samples now carry effects only on shapes and function types, plus `! Pure` on a few domain functions as the explicit assertion. `Pure` lives in the `!` clause rather than as a keyword so that all effect syntax is in one place.

### 7.4 The .NET backend emits C# source

Decided 2026-09-08. Milestone 7 lowers Trebuchet to C# source and hands it to Roslyn, rather than emitting IL directly. Records, `with`, pattern matching, `async`, and structural equality all have close C# equivalents, so the generated code stays readable and debuggable, and Roslyn does the IL generation and optimisation. Direct IL emission would buy faster compile times and independence from the C# compiler at the cost of reimplementing what Roslyn already does; it can replace the source backend later once the semantics are settled, without changing anything above the lowering. The interop story with a C# host is also easier to verify when the output can be read.

The interpreter keeps its role as the executable specification: the same programs run through it and through each backend, and any difference in result is a bug in one of them.

### 7.5 Findings from the type checker

Milestone 1's checker exists (`treb check`), with a test suite of negative cases, and both samples check clean. Building it settled three things.

- **A variant expression has its union's type.** `error(E.A(x))` and `error(E.B)` in the two branches of an `if` must unify, so a variant constructor returns the union, not the variant. Patterns still name variants. Two variants of the same union also unify with each other for the same reason.
- **Name shadowing between a service method and a top-level function is a real hazard.** The orders sample had a method `place` calling a handler `place`; the method shadowed the handler and the call had the wrong arity. The checker caught it; the interpreter would have failed at runtime. The sample now names the handler `placeOrder`. Whether the language should forbid the shadow outright is added to the syntax sketch's open choices.
- **Generic builtins carry the whole inference load.** The samples never declare a generic function, so the checker instantiates generic builtins per call and infers lambda parameter types from the expected function type, and that was enough. User-defined generics are deferred until a sample needs one.

The checker also runs before `treb serve` starts, which turned a class of startup panics into compile errors with positions.

### 7.6 Findings from the effect checker

Milestone 3 is built on top of the type checker: every body is a frame recording its calls, and a fixpoint over frames gives each function its effect set. `treb effects` prints the result. Three observations from running it over the samples.

- **Every handler in both samples is pure.** Handlers take state and return events; the only `Write` in the bookings program is `InMemoryRoomStore.append`, through the shape the service depends on. The brief's table calls handlers "the only places writes can happen", and the checker says the truth is one level down: handlers are where writes are *decided*, and the store adapter is where they *happen*. The `handler` keyword still matters as the CQRS marker and as the scope boundary, but the rule "a fn cannot write" is what does the enforcement, and it is checked on inferred effects, so it catches indirect writes through helpers and lambdas.
- **Declared effects are the contract at call sites.** A first version charged callers the callee's inferred body effects even when a clause was declared, so the Postgres stub, whose methods only `fail`, looked pure to its callers. That is wrong: a declaration is an upper bound the implementation may change within. Callers now see declared effects when present and inferred effects otherwise.
- **Higher-order builtins need their function arguments' effects.** `map(xs, f)` has whatever effects `f` has. The checker cannot see inside a builtin, so it adds the effects of every function-typed argument. When the stdlib is written in Trebuchet this special case disappears, since inference will see the bodies.

The interop rule from §7 is now concrete: a function-typed value whose type carries no `!` clause is assumed to have all three effects.

### 7.7 Findings from the runtime and the C# backend

Milestone 2 (persistent collections) and the C# source half of milestone 7 exist. `Trebuchet.Runtime` holds a bit-partitioned vector trie, a hash array mapped trie for maps and sets, Option, Result, Cell, and a Prelude mirroring the builtins. The interpreter runs on it, so every existing test exercises the collections. `treb emit` lowers a checked program to C#, and an integration test builds the bookings output with `dotnet build` and runs the interpreter's scenario against it with identical results.

- **The type checker's side tables were sufficient for lowering.** The emitter needed the type of every expression, how each member access resolved (field, method, shape member, namespace, or call sugar), and which instantiated signature each call chose. Nothing else. That is a good sign for the C++ backend, which will consume the same tables.
- **Untyped Result and Option constructors need target typing.** `ok(x)` does not know its error type. The runtime carries `OkValue<T>` with an implicit conversion to any `Result<T, E>`, and the emitter casts at returns and arguments. C++ will need the same trick or explicit type arguments.
- **A variant's static type must be its union in the target language too.** C# infers `error(new SlotInPast(...))` as `ErrorValue<SlotInPast>`; the emitter casts every variant construction to its union. This is the same decision the checker made in §7.5, surfacing one level down.
- **Structural shapes lower to nominal interfaces by computing satisfaction at compile time.** The emitter asks the checker which shapes each service satisfies and adds those interfaces. Composition roots then type-check in C# without reflection, which is the outcome §5.1 wanted.
- **Expression-oriented source lowers to statement-oriented C# without a general block expression.** Every `if`, `match`, and `?` becomes statements with a typed temporary, and lambdas are statement lambdas. The cost is that a hoisted temporary can be evaluated before a sibling argument; the fix is to hoist all arguments when any needs it, which is a small follow-up.

**Milestone order.** The C# backend was done before the C++ backend, reversing the brief's recommendation. The reasoning: with the checker complete, emitting C# was the shortest path to compiled, host-embeddable output, and it validated the side tables the C++ backend will reuse. The C++ backend remains the one that forces the ownership and refcount decisions, and nothing here changes that; it has simply not been reached yet.

### 7.8 Findings from async lowering

The `Suspend` flag now lowers to `async ValueTask<T>` in generated C#. The rule is one line: a function is emitted async exactly when its effects, declared or inferred, include `Suspend`, and every call to such a function is awaited. Shape members, function-typed values, lambdas, and roots follow the same rule. Pure code and `Nondet`-only code stay synchronous. The bookings program builds and passes its scenario with the store methods async and the domain untouched.

- **Effect inference is what makes this mechanical.** Without inference every service method would need a manual `async` decision; with it, the emitter asks the checker one question per function. This is the payoff §5 of the brief promised, and it needed nothing beyond the effect checker from milestone 3.
- **Structural shapes and async interact.** A shape member declared `! Suspend` becomes an interface method returning `ValueTask`. An implementing service method whose body never suspends, such as the in-memory store, is emitted async anyway so the interface is satisfied. The checker already allows a method to do less than the shape declares; the emitter has to honour the declaration, not the body.
- **Suspending lambdas need async-aware higher-order functions.** A builtin cannot be inspected, so when a lambda argument suspends the emitter calls an `Async` variant of the builtin. That special case will disappear when the stdlib is written in Trebuchet, since inference will then see both bodies. The same two-variant split will be needed in the C++ runtime, where coroutines make it more visible.
- **`ValueTask` versus `Task`.** `ValueTask` was chosen because most suspending calls in a CQRS service complete synchronously against a cache or an in-memory adapter, and `ValueTask` avoids an allocation in that case. A C# host that needs `Task` can call `AsTask()` at the boundary, which is where milestone 7's remaining interop work lives.

Milestone 7 now lacks only the host-facing pieces: `IServiceCollection` registration, boundary conversions, and exception mapping.

### 7.9 Findings from the C# host and the C++ backend

Milestone 7 is complete on the host side, and milestone 4 exists: the bookings program compiles to C++ and passes the same scenario, line for line, as the interpreter and the C# output.

**C# host.** `treb emit --host` generates one `AddTrebuchet_<root>()` per composition root. The root is built once at startup and each entry is registered under its own type and under every shape it satisfies, so a C# controller can ask the container for `RoomStore` and receive the in-memory adapter. `examples/bookings-host` is a stock ASP.NET project whose only hand-written file maps five routes; it was exercised over HTTP with the same results as the interpreter's server. What this settled: compile-time composition and a runtime container coexist without conflict, because the container only ever sees finished objects. The `dynamic` root from §5.1 is still not needed.

**C++ backend.** One header, C++20, standard library only. Findings:

- **The same side tables lowered to a second language without additions.** Expression types, member resolution kinds, and instantiated callees were enough for C++ as they were for C#. The emitters share their statement-lowering structure almost line for line; the differences are all in type spelling and pattern access.
- **Target typing works in C++ through converting constructors.** `ok(x)` returns an `OkValue<T>` with a converting constructor on `Result<T, E>`, so `return ok(x)` and typed assignments need no casts. A variant value is wrapped in its union at construction, the same rule as in C#, because `std::variant` comparison is not implicit.
- **Argument evaluation order bit the runtime, not the generated code.** `Map::set` hashed the key and moved it in the same argument list; GCC evaluated the move first, so every stored hash was the hash of an empty string. The whole program "worked" and every lookup missed. This is precisely the class of bug the immutable-by-default language exists to rule out, and it lived in the one place written in a mutable language. The fix is one line; the lesson is that the runtime needs its own property tests, which it now has on the .NET side and needs on the C++ side.
- **Records as map keys need generated hashes.** C++ has no structural hashing, so the emitter writes a `std::hash` specialisation per record and variant, placed between a module's types and its functions so it precedes any instantiation.
- **`std::shared_ptr` is the refcount for now.** Its counts are atomic, which §6 said would be the cost of the simple choice. The runtime header says so. A non-atomic intrusive count is an optimisation for the single-threaded loop and does not change the generated code.
- **Blocking, not coroutines.** Milestone 4 asked for the pure subset with `Suspend` as blocking, and that is what exists. The C# backend shows the shape the coroutine lowering will take: one predicate per function from the effect checker.

### 7.10 Findings from closing the remaining gaps

Six items were finished together: comment preservation in the formatter, user-defined generics, an FFI, a non-atomic refcount, coroutine lowering on C++, and property tests for the C++ runtime. Three of them produced decisions.

- **Effect polymorphism is necessary, not optional.** The first generic sample failed the checker: a `fn` that calls a function-typed parameter with no `!` clause was charged all three effects, so every higher-order function was a Write violation. The rule now is that such a parameter's effects come from the argument at each call site: the checker records which parameters a function calls or passes on, and the fixpoint adds the matching argument's effects. `! Pure` on a higher-order function therefore means "adds nothing beyond its arguments", which is what it should mean. C# and C++ cannot be polymorphic over async, so the emitters generate a synchronous version and an `Async` twin, chosen per call site, the same split the Prelude already had. When the stdlib is written in Trebuchet, the Prelude's hand-written twins become generated ones.
- **The FFI is a declaration, not a mechanism.** `extern fn` carries a Trebuchet signature with mandatory effects, one host symbol per target, and `catch` lines mapping host exception types to error variants. Everything else is the same as any other function: callers are charged the declared effects, `Suspend` externs are awaited or become coroutines, and unmapped exceptions become panics at the wrapper. The interop rule from §7 ("a function value with no clause has all effects") never applies to externs because the clause is required. The interpreter resolves externs from a registry the host fills, so `treb serve` cannot run a program with externs unless the server registers them; that is correct and deliberate.
- **Generic instantiation must always go back to the definition.** Instantiating an already-instantiated record by position re-mapped its fields, so `swap[A, B]` returned the wrong types. Records and unions now carry a reference to their definition and every instantiation substitutes from it. Recursive generic unions were believed supported by the checker, the interpreter, and C#, but not by C++; §7.15 records that the checker was wrong too, and how both were fixed.

The C++ runtime now counts references without atomics, and `Suspend` lowers to a lazy coroutine `Task<T>` with symmetric transfer, decided per function by the same effect predicate the C# backend uses. There is no event loop, so every task completes synchronously and `get()` drives one from a host; the coroutine machinery is real and the scheduling is not. The runtime's own test program covers the collections, the counting, the coroutines, and time parsing, and the .NET suite compiles and runs it with g++.

### 7.11 Findings from resources and disposal

The ownership marker and the early-release block from §5.2 are now spelled and implemented: `resource service X(...)` marks a service as owning something that must be given back, and `use x = expr` binds it for the rest of the enclosing block.

- **A resource is a service with a `release` method, nothing more.** The checker requires `fn release() -> Unit` on a resource service and charges the method's effects to every `use` site, so a resource whose release suspends makes the block `Suspend` without further annotation. Records and unions may not hold resources, for the same reason they may not hold `Cell`; a resource is not a value.
- **A fresh resource must be bound with `use`.** Constructing a resource service in a plain binding is an error ("bind it with `use x = ...`"), and `use` on a non-resource is also an error. There is no way to leak one by forgetting. Passing a resource to a function is fine because the callee borrows it; only the `use` site owns it.
- **Release order is reverse binding order, and it survives early exit.** The interpreter and both backends release at the end of the enclosing block, last-bound first, including when `?` returns early or a panic unwinds. On .NET this is `IDisposable` and `using`, or `IAsyncDisposable` and `await using` when `release` suspends. On C++ the destructor of the service calls `release` when the last `Rc` drops, and `use` is a plain binding; `release` is wrapped so a throwing release cannot escape a destructor.
- **Roots may hold resources.** An entry such as `db: PgConnection` is a singleton owned by the root; the DI container disposes it on shutdown on .NET, and the root record's destructor does on C++. The captive-dependency check (§5.2) is implemented alongside: a singleton depending on a `scoped` service is a compile error.

`examples/resources/` is the acceptance sample; `used a, used b, closed b, closed a` is the expected trace on all three targets.

### 7.12 Findings from the host boundary

The two remaining .NET interop items from §7.9 were done together: host-supplied leaf dependencies and value conversion at the edge.

- **The container is the source of truth for leaves.** `AddTrebuchet_<root>()` no longer builds the root eagerly. Each entry becomes a keyed factory registration, and each dependency of a service resolves from the container by its declared type first, falling back to the root's own entry. Registrations the host makes before the call therefore win, and the root's entries are the defaults, via `TryAdd`. A host can replace the clock, the room table, or a store adapter with two lines of ordinary C#, and the test suite does exactly that. Scoped services register scoped, so the container owns lifetimes and disposal; the `Root_<name>` record is built from the container on first use.
- **The boundary converts, the language does not.** `Vector[T]` now implements `IReadOnlyList<T>` and `Map[K, V]` implements `IReadOnlyDictionary<K, V>`, so Trebuchet values pass into host signatures with no copying. In the other direction every extern result passes through `Boundary.To<T>`, which turns arrays and enumerables into `Vector`, dictionaries into `Map`, null into `None` when the declared type is `Option`, and widens integers and dates. A null where the declaration is not `Option` is a panic at the wrapper, which is the same rule as an unmapped exception. Nothing in the language changed; the sample extern `readLines` just declares `Result[Vector[String], FileError]` and binds to `File.ReadAllLinesAsync`.

With these, milestone 7 is complete in both directions. JSON at the edge remains a host-adapter concern, as decided in §7.1.

### 7.13 Closing the .NET path

Four small items closed the .NET path, and one of them found a bug.

- **Set builtins.** `Set[T]` had a runtime on both targets but no surface. It now has `toSet`, `add`, `remove`, `contains`, `length`, `isEmpty`, `items`, `merge`, `intersect`, and `difference`, on the interpreter, C#, and C++. The union operation is spelled `merge` because `union` is the declaration keyword. `examples/collections/` is the sample and runs identically on all three targets.
- **Structural equality was wrong on .NET.** Writing the sample exposed that `==` on a `Vector`, `Map`, or `Set` lowered to the C# operator, which is reference equality on a class. Records were unaffected because C# records overload `==`. The emitter now lowers `==` and `!=` on any non-primitive type to `Equals`, which every runtime type overrides structurally. This is exactly the kind of bug the "same scenario on three targets" rule exists to catch; the interpreter had it right.
- **Source mapping.** Generated C# carries a `#line` directive before every statement, naming the `.treb` file and line, and `#line default` after each block. A panic's stack trace and Roslyn's diagnostics therefore point at Trebuchet source; the test asserts the trace of a `fail` names `boom.treb:line 4`. `treb emit --no-lines` turns it off for reading the generated code.
- **JSON at the host boundary.** The runtime gains a `System.Text.Json` converter factory implementing the shape the interpreter's dev server already used: a single-field record flattens to its value when nested and stays an object at the top level; a nullary variant is its name; a variant with fields is an object with a `type` field; `Option` is null or the value; a `Map` is an object when its key is scalar or a single-field record over a scalar, and an array of pairs otherwise. Generated records and unions carry marker attributes so the converter never guesses about host types. The host sample adds it with one line and its responses are now byte-for-byte the dev server's.
- **Packaging.** `dotnet pack` produces `Trebuchet.Cli`, a dotnet tool exposing `treb`, and `Trebuchet.Runtime`. The tool carries the C++ runtime header, and a generated project takes a package reference to the runtime when the tool is not running from a source checkout. Verified by installing the tool to a scratch path and building emitted C# and C++ from it.

### 7.14 The C++ event loop

The scheduling half of §6 is now real. `trebuchet.hpp` has an `EventLoop`, one per thread, with a ready queue, a timer heap, and a mutex-protected `post` queue. Everything Trebuchet-generated runs on the loop thread, which is the invariant that keeps the non-atomic refcounts valid; `post` is the only way work enters from another thread.

- **`Task::get()` drives the loop.** It schedules the task if it has not started and steps the loop until the task completes, so a suspension on a timer or an external completion really suspends and other scheduled work runs meanwhile. Drivers and roots did not change; `get()` still means "run this to completion". If nothing is ready, no timer is armed, and nothing external is pending, `get()` panics with a deadlock message rather than hanging.
- **`sleep(ms)` is the first builtin that truly suspends** on all three targets: `Task.Delay` in C#, a timer on the loop in C++, `Thread.Sleep` in the interpreter. Writing it found that both emitters only awaited a builtin when a function argument suspended, never when the builtin's own declared effects did; they now do. This is the second bug the three-target rule has caught in a day.
- **Host-side concurrency, not language-side.** `spawn(task)` starts a task on the loop and returns a joinable handle; `Completion<T>` is a move-only resolver that any thread may `resolve` or `fail`, with `pending()` giving the awaitable side. Dropping a resolver unresolved fails its waiter with a panic instead of hanging the loop. Between them they are the shape an extern binding uses to adapt a callback-based host API to a `Task`. The language itself still has no spawn, because parallelism is undesigned (§6) and `Suspend` alone must not imply concurrency.
- **The C++ driver demonstrates both.** The language runs two `tick` handlers sequentially and logs `a, b` whatever the delays; the host spawns the same two and gets `b, a`, because they complete in timer order. `examples/async/` is the sample; the runtime tests cover timers, spawn, completions resolved from a worker thread, nested `get()` inside the loop, and the dropped-resolver case.

What this does not do: multi-threaded execution. The atomic-versus-confined refcount question in §9 is unchanged, and the loop makes the confined option concrete: a value stays on the thread whose loop created it, and crossing to another loop would need a handoff at the `post` boundary.

### 7.15 Recursive types on every target

The last C++ gap from §7.10 was recursive generic unions. Closing it found two checker bugs first.

- **Instantiation looped.** Instantiating `Tree[Int]` substituted into every variant field eagerly, and a field of type `Tree[T]` instantiated `Tree[Int]` again, without end. Instantiated records and variants now compute their fields on first access from the definition; a recursive generic instantiates in constant work and each field is substituted once, when someone looks at it. The mutability walk that forbids `Cell` in records also had to visit by definition rather than by instance, since every instantiation is a fresh object.
- **Locals were being generalised.** Every name lookup freshened a generic type's parameters, which is right for the nullary variant of a generic union and wrong for a local whose type mentions the enclosing function's type parameter: inside `chainLength[T]`, the parameter `c: Chain[T]` became `Chain[?]`, and the recursive call was instantiated at an unbound variable that C# printed as `object` and C++ as `Unit`. It went unnoticed because every earlier generic sample bound the variable through its return type. Only nullary variants are marked for freshening now.
- **C++ boxes the recursive field, nothing else changes.** A field whose type mentions its own record or union is emitted as `Box<T>`, a value behind a reference-counted pointer that converts to and from `T`, compares structurally, and hashes through. Variant structs are forward-declared so the `std::variant` alias can come first and a variant can hold `Box<Union>`. Field access and pattern destructuring dereference the box, so generated code and the language are unaware of it.

`examples/trees/` has a recursive expression union, a generic tree, and a generic record recursive through `Option`, with `mirror`, `leftmost`, and a length function that exercise the recursive generic calls; it prints the same line on all three targets. With this, the C++ backend has no known gap against the interpreter other than in-place reuse when a refcount is one, which is an optimisation.

### 7.16 Factory synthesis and the supervisor point

The two language items left on milestones 5 and 6 are done.

- **Synthesised factory lambdas** (§5.2) work as specified. When a service depends on a function that returns a service and the root has no entry for it, the compiler builds the lambda: each parameter of the function type fills the first unfilled constructor parameter of the target whose type unifies, in order, and the remaining constructor parameters resolve from the root exactly as a direct dependency would, by name, then by type, then implicitly. `examples/factories/` has a `Client(open: fn(String) -> Conn ! Pure)` whose root names only a config and a log; the root wires `Conn`'s other two parameters and `Client` never learns of them. The same synthesis runs in the checker, the interpreter, both emitters, and the C# host registration, where the host may still replace the factory by registering its own `Func`.
- **`supervise expr` is the supervisor point** from §4, made a language construct. It yields `Result[T, Panic]`, where `Panic` is a builtin record with the message, and it is allowed only in a handler or a service method. A `fn` cannot contain one, so pure code still cannot observe a panic and memoisation stays sound. Resources bound with `use` inside the supervised expression are released during the unwind on every target, because release is `finally`, `using`, or a destructor. The bookings API now wraps the command in `supervise` and maps a caught panic to a new `Internal` error, so a bug in the service is a 500 from the language rather than from the host's catch-all; the dev server's and the ASP.NET host's catch-alls remain as the outermost net.

The construct answers reviewer question 4 in §10 in the negative for now: nothing in the samples needed a catch in ordinary code, and the request boundary is the right place. Should a legitimate case appear, widening the allowed frames is a one-line change in the checker, and the effect system does not care either way because a panic is not an effect.

### 7.17 Visibility, the acceptance scenario, and two items closed by decision

- **`private` is enforced.** A module exports every declaration not marked `private`; `use` sees the rest neither unqualified nor qualified, and a private union hides its variants too. A `private` service method is callable from the service's own methods only. Whether modules map to files or directories stays as it is: one file, one module, named by its header. Nothing in the samples needed opt-in export lists, so public-by-default stands.
- **The milestone-6 acceptance scenario runs on all three targets.** `examples/orders/` has one command, one handler, one query, one service, and one root; the same scenario (place, place again, place empty, describe) prints one line on the interpreter, on .NET, and on C++. Until now the emitter tests only compiled it.
- **Dynamic composition roots are the host container.** On .NET the generated registration already makes every root entry an overridable default (§7.12), which is what a plugin needs: register the plugin's implementation first and the root picks it up. No language feature is needed, and on C++ the same effect is a different root. Closed.
- **In-place reuse when a refcount is one stays deferred, with a reason.** Generated code passes values as lvalues (`r = append(r, x)`), so the runtime cannot tell at `append` whether the caller still needs the old vector; safe reuse needs a last-use analysis in the emitter that turns the final use of a binding into a move. That is an optimisation pass, not a runtime change, and it waits for a benchmark that needs it.

### 7.18 Scoped is a lifetime, not a transaction

§5.2 said the scope boundary is where a transaction commits. That sentence is withdrawn (2026-09-08). The reasoning behind it was the unit-of-work pattern: handlers are the only `Write` sites, a scoped service lives for one handler invocation, so committing at scope end would make application code unable to forget a commit and would roll back on panic for free. It does not survive contact with the rest of the design.

- It hides the most important side effect a command has. The effect system exists to make world-writes visible; an implicit commit at a block end is the opposite.
- "The scope ended successfully" is not well defined. A handler returning `Error(SlotTaken)` is a domain outcome, not a failure, and sometimes a rejection should be committed. Tying commit to `Ok` conflates the domain result with the transactional one.
- The samples never needed it. With an event store the unit of atomicity is the one `append` of the events a command produced, which is what bookings and orders already do. Transactions spanning aggregates are what event sourcing advises against.
- The language already has the right primitive. A transaction is a resource: `use tx = db.begin()`, an explicit `tx.commit()?`, and `release` rolls back if nothing committed. Commit is visible, rollback on error or panic comes from the existing unwind, and nothing new enters the language.
- C# developers read `scoped` as one instance per request, which is what it means in ASP.NET. Giving it commit semantics would surprise the people the .NET backend is for.

**Decision.** `scoped` means one instance per handler or query invocation and nothing more. Transactions are resource services with an explicit commit and rollback on release. A unit-of-work convenience, if a project wants one, is a library pattern: a scoped resource whose `release` commits when a flag was set. Milestone 6 has nothing remaining.

### 7.19 The demo grows into the language

The bookings sample and its React front end were extended to touch more of the language, and the exercise produced five stdlib additions and one dev-server feature rather than any language change.

- **Attendees and capacity.** A booking carries `attendees: Set[GuestId]`; the handler rejects a request whose attendee count exceeds the room's capacity with a new `OverCapacity` error. The API takes a `Vector[String]` and converts it with `toSet(map(...))`, so `Set` is a domain type and never a wire type.
- **A waitlist with promotion.** A request that would conflict may ask to be waitlisted; it becomes a `BookingWaitlisted` event and a `Waitlisted` status. Cancelling a booking emits `BookingCancelled` and, when the oldest waitlisted booking now fits, `BookingPromoted`. The handler decides this by applying the cancellation to the room first and asking the `promotable` query, which is the event-sourcing idiom in two lines: fold, then query the folded state.
- **Paging and sorting.** `record Page[T]` with `page[T](xs, offset, limit)` is the first user-defined generic in the sample; the API sorts by slot start with `sortBy` before paging.
- **Cross-room stats.** `overview()` on the service asks the store for every room id, rebuilds each room with `traverse`, and maps a pure `stats` query over them; the query counts statuses into a `Map[String, Int]` and distinct guests into a `Set`. `traverse` stops at the first store error, which is the behaviour the `?` operator would give a loop if the language had one.
- **An extern in the API layer.** `getInfo` reports the host name through `extern fn hostName()` bound to `System.Net.Dns.GetHostName` on .NET and `host::hostName` on C++. The dev server now binds every `csharp` extern symbol by reflection, loading the framework assembly that owns the namespace when it is not loaded yet, so a program with BCL-bound externs runs in the interpreter without host code.
- **`private`** marks the fold's `setStatus` helper and the service's `current`, the first uses since visibility was enforced (§7.17).

The stdlib gained `take`, `drop`, `at`, `sortBy`, and `traverse`, each on all three targets with Async twins where a function argument may suspend. The three-target scenario grew from nine steps to fifteen and still prints one line per step identically on the interpreter, on .NET, and on C++; the C# host gained the three routes and the UI shows capacity, attendees, waitlist status, a pager, a stats table, and the host line.

### 7.20 Patterns: guards, lists, tuples, as-patterns, and destructuring

The "Haskell for .NET" pitch needed the matcher to look the part. Four additions, all sugar over what the checker and both emitters already did, and none of them touches effects.

- **Guards.** `pattern if cond => body`. The guard sees the pattern's bindings. A guarded arm covers nothing for exhaustiveness, because the guard may be false, so `Some(x) if x > 0` still needs a `Some` arm or a `_` behind it. C# lowers a guard to `when`; C++ evaluates it after the bindings inside the arm and falls through to the next arm when it fails, which required the `if`/`else if` chain to become a `matched` flag.
- **List patterns.** `[]`, `[a, b]`, `[first, ...rest]`, and `[a, ..._]` match a `Vector`. Exhaustiveness has its own rule: a match on a vector is complete when some arm takes every length from k up and each length below k has an exact arm, so `[]` with `[x, ...rest]` is complete without a `_`. C# has list patterns natively; the runtime `Vector` gained `Slice` and a `Range` indexer so `.. var rest` works. C++ lowers to a size test plus `get` and `drop`.
- **Tuples.** `(A, B)` is a type, `(a, b)` a value, `(x, y)` a pattern, and `p.0` an item. Structural equality and hashing come for free: `ValueTuple` on .NET, `std::tuple` on C++ with a hash specialisation the runtime provides. Tuples are the one place the language borrows from F# rather than from records: a pair that needs no name.
- **As-patterns and destructuring bindings.** `whole @ Rect(w, h)` binds the value and its parts. A binding line may be an irrefutable pattern: `(lo, hi) = minMax(xs)` or `[...xs] = v`; a pattern that can fail is rejected with a pointer to `match`. On C# an as-pattern is a designation after the pattern where the grammar allows one and `var` plus a `when` otherwise; the emitter keeps a small prelude list for the case a name is bound twice.

`examples/patterns/` runs the four features through the same line on all three targets. Writing it found one C# lowering bug: a top-level `_` arm emitted `case _:`, which a switch statement reads as an identifier, not a discard.

### 7.21 Shapes over types: the type-class story

The pitch "Haskell for .NET" needs one thing above all: generic code that can ask something of its type parameter. Trebuchet now has it, and it reuses a word the language already had.

- **A shape with a type parameter is a type class.** `shape Monoid[T]` declares members that mention `T`; `instance Monoid[Money]` supplies them for one type; `fn concatAll[T: Monoid](xs: Vector[T])` admits only types with an instance; and a call goes through the shape, `Monoid.combine(a, b)` or `Monoid.empty[T]()` when no argument fixes `T`. Instances may be for records, unions, or primitives, so `instance Monoid[String]` is fine; they may not be for generic or structural types yet, so no `Monoid[Vector[T]]`. Instance methods must match the shape member's type with `T` substituted, and their inferred effects must stay within the member's declared effects, checked by the same obligation the service-satisfies-shape rule uses.
- **`Ord` is built in.** Int, Float, String, Bool, and Instant have instances the runtime supplies; a record declares its own with `instance Ord[Money]` and a `compare`. On a parameter declared `T: Ord` the comparison operators work and lower to `compare`, and the new builtins `sort`, `minimum`, and `maximum` are constrained the same way. Equality, hashing, and `toString` stay universal and structural, which is why there is no `Eq`, `Hash`, or `Show`: every Trebuchet type already derives them, and a constraint that every type satisfies would say nothing.
- **Constraints resolve at compile time and lower to dictionary passing.** After every body is checked, so inference variables have their final bindings, the checker resolves each constrained call and each class call to a concrete type and records which instance it needs. Both emitters then pass the instance as a hidden trailing argument: on .NET a class shape is an interface `Monoid<T>`, an instance is a class `Monoid_Money` with a singleton, a constrained function takes `Monoid<T> __Monoid_T`, and a class call is a method call on the dictionary; on C++ an instance is a struct, a constrained function takes an extra deduced template parameter, and everything else is the same. The generated C# is what a C# developer would write by hand for the same abstraction, which was the point. The interpreter, being dynamically typed, cannot tell `Monoid.empty[T]()` which instance to use from values alone, so it now runs the checker at start-up and reads the same resolution tables.
- **What is not there, on purpose.** No multi-parameter classes, no superclass relations, no instances for generic types, no passing a constrained function as a bare value (wrap it in a lambda), and no default members. Each is a real feature with a real cost, and none of the samples has asked for it yet. The dictionary lowering leaves room for all of them.

`examples/classes/` runs a `Monoid` with two instances, an `Ord` instance, a constrained fold, and the constrained builtins through the same line on all three targets. Writing it found that the built-in `Ord` shape was being re-populated per checker instance, which the parallel test runner turned into a corrupted dictionary; it is now initialised once.

### 7.22 The rest of the Haskell list: `?` on Option, lazy sequences, property tests

- **`?` on `Option`.** In a function that returns an `Option`, `x = at(xs, 0)?` leaves with `None` when there is nothing there, the same shape `?` already gave `Result`. Mixing is an error with a pointer to the return type. C# lowers it to an `is None<T>` test and an early return; C++ to `has_value`; the interpreter to the same signal `Result` uses.
- **`Seq[T]` is lazy; `Vector[T]` stays strict.** `Seq.iterate`, `Seq.range`, and `Seq.from` build one; `map`, `filter`, `take`, `drop`, and `takeWhile` return another without pulling; `toVector`, `first`, and `fold` pull. A `Seq` is a computation rather than a value, so it has reference identity and no structural `==`, and a function handed to a lazy combinator may not `Suspend`, because it runs whenever the sequence is pulled; the checker enforces that through the same obligation mechanism that checks a lambda against a declared function type. Laziness by default was rejected in §7 of the brief's addenda and stays rejected: this is the useful part of it, and .NET developers already know it as deferred LINQ. The three runtimes implement it as a re-iterable enumerable factory (.NET), a pull function factory (C++), and the same in the interpreter.
- **Property tests come for free from purity.** `treb test <dir>` runs every function whose name starts with `prop` and returns `Bool`, generating arguments from the parameter types: primitives, vectors, sets, maps, options, results, tuples, records through their validating constructors, and unions by variant. A failing case is shrunk greedily toward zero, empty, and shorter before it is reported, so a property that a vector of length three breaks reports `xs = [0, 0, 0]`. The runner is in the compiler library, so the test suite uses it too. Nothing about it is specific to the interpreter except that the interpreter is what runs the property; a pure function has no setup to fake.

Running the properties over the sample found a bug in the type-class work of §7.21: the built-in `Ord` shape's members were not registered as class members, so `Ord.compare(a, b)` on a concrete type was not resolved and the interpreter tried to evaluate `Ord` as a value. The first property that compared two `Money` values caught it on its first case.

### 7.23 Named-field patterns and `...`

Positional patterns stopped scaling the moment variants grew past two fields: adding `attendees` to `BookingHeld` broke every pattern on it, and `BookingCancelled(_, _)` says nothing. A constructor pattern now takes named entries, `BookingHeld(id: id, slot: s)`, which may omit any field, and a trailing `...` that ignores the fields not mentioned, `BookingCancelled(id, ...)`. Positional entries still come first and, without `...`, still cover every field. A record is matchable the same way, `Point(x: 0, y: 0)`, since it is a one-constructor type; a record pattern whose parts are total is itself total.

Punning, `Room(capacity)` binding a variable named after the field, was considered and dropped: `Room(capacity)` already means a positional bind of the first field, and no spelling for the pun read well next to the `name: value` shape construction and `with` use. `...` covers the common case in fewer keystrokes anyway. C# property patterns are named by nature, so the lowering got simpler rather than harder; C++ indexes struct members either way. The samples that padded with underscores now name their fields.

### 7.24 Local named functions

A function may now be declared inside a body with the same syntax as at the top level. It sees the enclosing parameters and the locals bound above it, it is in scope in its own body so it may recurse, it carries a full signature with type parameters, constraints, and an optional `!` clause, and it inherits the enclosing function's constraints, so a local `keep` inside `largest[T: Ord]` may compare two `T`s. A `handler` may be local only inside a handler or a service method, since a local `fn` is still a fn and may not write. A block may not end with a declaration.

The nameless form was already there: `\x -> ...` captures, is bound to names, and is passed along. The named form exists for the three things a lambda cannot do: recurse, state a signature, and announce itself before its body. The two coexist on the same split C# draws between lambdas and local functions. Lowering follows that split too: a C# local function, with an `Async` twin when it is effect-polymorphic; on C++ a `std::function` assigned from a lambda that captures by value and itself by reference, or a template lambda when the local is generic, in which case it cannot recurse. The interpreter needed only a closure whose environment is the block that defines its name.

The classes, properties, and patterns samples moved their helpers inside the functions that use them, which is where they were always meant to be.

---

## 8. Revised milestones

Milestones from the brief, with the decisions above folded in.

1. **Parser and type checker.** Done, including user-defined generics (§7.10). Records, deep immutability, structural equality, flat then deep `with`. Sealed unions and exhaustive matching, since `Result` depends on them. Module headers, `use` imports, and name resolution (§7.1). Shape satisfaction at roots (§7.5).
2. **Persistent collections runtime.** Done for .NET and C++ (§7.7, §7.13): Vector as a bit-partitioned trie (RRB deferred), Map and Set as HAMT with builtins on all three targets, fold family, `Cell[T]`.
3. **Effect system.** Done (§7.6). `Nondet`, `Write`, `Suspend` inference and checking. Errors only, no lowering.
4. **C++ backend.** Done (§7.9, §7.10, §7.14, §7.15): records, unions, generics, recursive types via `Box`, functions, services, shapes, roots, externs, collections, non-atomic `Rc` refcounting, `Suspend` as `Task` coroutines on a single-threaded event loop with timers, `spawn`, and cross-thread completions. Remaining: refcount-uniqueness in-place reuse.
5. **`service` and compile-time composition roots.** Done: singletons, singleton inference, factory dependencies with synthesised lambdas (§7.16), effectful constructors, `scoped` services, the captive-dependency check, and `resource` services (§7.11).
6. **`handler`, `apply`, and the event-sourcing slice.** Done: handlers, `use` release at block end, coroutine lowering, and `supervise` as the supervisor point (§7.16). Scope-boundary commits were withdrawn (§7.18); nothing remains.
7. **.NET backend.** Done (§7.4, §7.8 to §7.13): C# source with `#line` mapping, `async ValueTask`, container-driven `IServiceCollection` registration with host overrides, `IDisposable` for resources, panics as exceptions, externs with exception mapping and boundary conversion, JSON converters, and a packaged `treb` tool plus runtime package.

**Interpreter.** A C# tree-walking interpreter now exists alongside milestone 1. It runs the samples and the bookings HTTP service, and serves as the executable specification the type checker and the C++ backend are tested against. It stays dynamically typed; the type checker is the next piece of milestone 1.

**Acceptance test for milestone 6** is met (§7.17): `examples/orders/` has one command, one handler, one query, one service, and one composition root, and the same scenario runs on the interpreter, on C++, and on .NET.

---

## 9. Deliberately open

- **Concrete syntax.** Sketched in `trebuchet-syntax-sketch.md`; open choices listed there.
- **Multi-threaded refcounting.** Atomic everywhere versus thread-confined with handoff. Decide when the single-threaded loop becomes a limit.
- **Parallelism primitives.** `parMap` and `race` exist conceptually; their runtime is undesigned.
- **Whether `mut` locals ever return.** Revisit only with evidence from real code after milestone 4.

---

## 10. Questions for reviewers

1. Does the three-flag effect model (`Nondet`, `Write`, `Suspend`) miss any property the compiler would need for the stated goals? In particular, is there a compiler decision that needs to distinguish kinds of non-determinism?
2. Is treating `Suspend`-only functions as memoisable sound in the presence of cancellation or timeouts?
3. Is the "no `mut` at all" position going to be a serious adoption barrier for C# developers, even with in-place reuse on the C++ backend? Is there evidence from Clojure, Elm, or Roc either way?
4. Panics catchable only at supervisor points: are there cases in a typical service where a catch is legitimately needed in ordinary code?
5. Replacing transient with factory dependencies: any lifetime pattern from real .NET DI use that this cannot express?
6. Is defining a scope as one handler or query invocation too narrow? Batch jobs and long-running streams do not fit this shape obviously.
7. Single-threaded event loop for the prototype: does this risk baking in assumptions that make multi-threading hard later, particularly around the refcount representation?
8. Is C++20 coroutine support mature enough across the target toolchains to depend on for generated code?
9. `Cell[T]` as the only mutable primitive: does an entity with effectful `get` and `set` undermine the immutability story in practice, or is it the right size of escape hatch?
10. Module system: is one module per file with `use` imports and public-by-default the right granularity, or should visibility be opt-in?
