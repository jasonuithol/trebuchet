# Trebuchet — Language Design Brief

> Immutable-first language. Compiles to .NET and C++. Async, dependency injection, and CQRS are language-level defaults rather than library patterns.

This document captures design decisions and open questions from an initial design discussion. It is intended as a starting brief for an agent building a prototype compiler. Sections marked **Decision** are settled enough to build against; sections marked **Open** need a call before implementation.

---

## 1. Motivation

C# has converged on `record` + `required` + `init` + `with` for immutability, but it is retrofitted:

- Immutability is shallow. `record Foo(List<int> Items)` compiles and gives a mutable collection with reference equality.
- No language-level "deeply immutable" guarantee — only discipline plus immutable collection types.
- `async`/`await` colors every function and leaks (`ConfigureAwait`, sync-over-async deadlocks, `async void`).
- DI, SOLID, and CQRS are conventions enforced by code review, not the compiler.

Trebuchet's pitch: make these things properties of the type system so the compiler enforces the separations developers currently maintain by hand.

---

## 2. Core: Deep Immutability by Default

**Decision.** Every type is immutable unless explicitly declared otherwise. Immutability is transitive and tracked by the type system.

```
record Order(id: OrderId, lines: ImmutableList<Line>)   // OK
record Order(id: OrderId, lines: List<Line>)            // compile error: mutable field in immutable type
```

Consequences:

- Structural (value) equality and hashing are the default for all types.
- Reference identity is opt-in via a dedicated declaration (e.g. `entity` or `Ref<T>`).
- `sealed` is the default; open inheritance is opt-in. Inheritance on records complicates equality, so keep it rare.

### Functional updates (`with`)

**Decision.** `with` expressions are first-class and support **deep paths**:

```
let updated = order with { customer.address.city = "Brisbane" }
let more    = order with { lines: order.lines.append(line) }
```

Deep `with` is the killer feature. If updating a nested field is as ergonomic as mutation, demand for mutation mostly disappears.

### Where mutation lives

**Open.** Pick one or more:

1. **Local `mut` bindings** that cannot escape scope. Simplest; no borrow checker needed if the target is GC'd or refcounted.
2. **Compiler-generated builders**: `Order.builder { ... }.freeze()` produces a mutable twin type that freezes into the immutable one.
3. **Functional updates only** (`with` + persistent collections) — no mutation at all.

Recommendation: (3) as the baseline, (1) as a scoped escape hatch for hot loops. Skip (2) unless deep `with` proves insufficient.

### Validation

Validation belongs in constructors / `init`-style accessors with a `field`-like keyword so no hand-written backing fields are needed:

```
record Person {
  required name: String
    init => field = value.trim() if value.trim().length > 0 else fail ArgumentError
}
```

---

## 3. Runtime: Persistent Collections

**Decision.** The standard library ships persistent collections with structural sharing as the baseline:

- `Vector<T>` — RRB tree or similar (O(log n) append/update, efficient concat/slice)
- `Map<K,V>` / `Set<T>` — HAMT
- `List<T>` — may alias `Vector<T>`

Do **not** build the runtime on `System.Collections.Immutable` (its `ImmutableList` is an AVL tree and slower than needed). Own the runtime; expose .NET interop as conversions at the boundary.

Collection literals (`[a, b, c]`, `{k: v}`) target these types directly.

---

## 4. Two Backends

| Concern | .NET backend | C++ backend |
|---|---|---|
| Memory | GC | Reference counting |
| Why it works | Native | Immutable data with no mutable back-references cannot form cycles, so refcounting is sufficient and cheap |
| DI lowering | `IServiceCollection` registrations | Compile-time resolution only |
| Async lowering | `ValueTask` / `Task` | Coroutines or callback-based; TBD |

**Decision.** The language surface must not leak the memory model. Same semantics on both backends.

**Recommendation.** Prototype against the **C++ backend first**. It forces the hard decisions (ownership, refcounts, interop with mutable C++ data at the boundary) early, even if .NET is the commercial target.

---

## 5. Async: Effects Instead of Function Coloring

**Decision.** Do not inherit `Task<T>` coloring. Use effect annotations in the type:

```
fn fetch(id: UserId) -> User ! IO
fn total(order: Order) -> Money            // pure
```

- A function without effects is pure. The compiler can memoize, cache, parallelize, and reorder pure calls.
- Calling an `! IO` function from a pure context is a compile error.
- Effects propagate by inference within a function body; the signature is the boundary.
- Lowers to plain calls where no suspension is needed and to `ValueTask` where I/O actually occurs. No `ConfigureAwait`, no `async void`, no sync-over-async.

**Open.** Effect granularity. Minimum viable set:

- `IO` — any external interaction
- `Write` — mutates external state (subset of `IO`; needed for CQRS enforcement, see §7)
- Possibly `Time`, `Random`, `Scope` for DI lifetime inference

Start with `IO` and `Write`. Add others only if they earn their keep.

---

## 6. Injectability: `service` as a Language Construct

**Decision.** Dependencies are declared as parameters of a nominal `service` type:

```
service OrderService(repo: OrderRepo, clock: Clock) {
  fn place(cmd: PlaceOrder) -> Result<OrderPlaced, OrderError> ! IO { ... }
}
```

Rules:

- **Constructor injection is the only injection.** No property/field injection.
- Services are immutable; dependencies are captured at construction.
- **Lifetimes are inferred** from captured effects: a service whose dependencies are all pure is a singleton; one that captures a `Scope`-effect dependency is scoped. No hand-written `AddScoped`/`AddSingleton` mistakes.
- **Interfaces are structural.** `Clock` is anything exposing `now() -> Instant`. Extracting an interface for testability requires no ceremony.
- Resolution happens at **composition roots**, at compile time by default.

**Open.** Runtime/plugin composition. Recommendation: compile-time resolution by default, with an explicit `dynamic` composition root as the escape hatch. The C++ backend can then stay reflection-free.

.NET lowering: emit `IServiceCollection` registrations so Trebuchet services slot into existing hosts.

---

## 7. CQRS: Enforced by Purity

**Decision.** The type system polices a three-way split:

| Kind | Construct | Effects allowed | Notes |
|---|---|---|---|
| Pure data | `record` | none | Commands, events, queries results are all records — immutable by default |
| Pure logic | `fn` (query), `apply` (event fold) | none (or read-only `IO`) | Cacheable, parallelizable, trivially testable |
| Effectful edges | `handler`, `service` | `IO`, `Write` | The only places writes can happen |

### Queries

Functions with no `Write` effect. Take state, return data.

```
fn openOrders(state: OrderBook) -> Vector<Order>
```

### Commands and handlers

Commands are records. Handlers are the single place with write effects and return **events**, not mutated state:

```
record PlaceOrder(customer: CustomerId, lines: Vector<Line>)

handler place(cmd: PlaceOrder, state: Order) -> Result<Vector<OrderEvent>, OrderError> ! Write
```

### State transitions

Pure folds over events. Event sourcing becomes the natural shape rather than a bolt-on architecture:

```
fn apply(state: Order, ev: OrderEvent) -> Order
```

### Errors

**Decision.** `Result<T, E>` with exhaustive pattern matching is the default error mechanism. Exceptions, if present at all, are reserved for unrecoverable failures. This decision shapes every stdlib signature — settle it before writing the stdlib.

---

## 8. Interop Boundaries (Critical for Adoption)

Incremental migration from C# is the adoption path. The boundary must be seamless:

- Calling a .NET `Task<T>` method from Trebuchet looks like an ordinary `! IO` call.
- Calling Trebuchet from C# hands back a `Task<T>` / `ValueTask<T>`.
- Mutable .NET collections crossing into Trebuchet are converted (copied or wrapped as frozen) at the boundary; Trebuchet persistent collections crossing out expose `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>`.
- C++ boundary: mutable C++ data is copied into refcounted immutable structures on entry; ownership of outgoing data is explicit.

---

## 9. Suggested Prototype Milestones

1. **Parser + type checker** for records, deep immutability checks, structural equality, `with` (flat, then deep).
2. **Persistent collections** runtime (Vector/Map/Set) — write once, target both backends.
3. **Effect system**: `IO`/`Write` inference and checking. No lowering yet — just errors.
4. **C++ backend** for the pure subset (records, functions, collections, refcounting).
5. **`service` + compile-time composition roots.**
6. **`handler` / `apply` / event sourcing** vertical slice.
7. **.NET backend** with `Task`/`IServiceCollection` lowering and interop conversions.

A good end-to-end acceptance test for milestone 6: a small order-management domain with one command, one handler, one query, one service, one composition root — compiled and run on the C++ backend, then on .NET with a C# host calling into it.

---

## 10. Open Questions Summary

- Mutation model: `mut` locals, builders, or neither? (§2)
- Effect set beyond `IO`/`Write`. (§5)
- Runtime composition roots vs. compile-time only. (§6)
- Async lowering strategy on the C++ backend (coroutines vs. callbacks). (§4)
- Whether exceptions exist at all. (§7)
- Concrete syntax — everything above is illustrative pseudo-syntax and should be revisited once the semantics are locked.

---

## 11. Addendum: Decisions Taken (2026-09-05)

The open questions in §10 were worked through in a follow-up session. Full reasoning, alternatives, and revised milestones are in `trebuchet-implementation-strategy.md`. This section summarises what changed relative to the text above.

| Question | Decision | Changes to the brief |
|---|---|---|
| Mutation model (§2) | Functional updates only. No `mut`, no builders. | The `mut` escape hatch is dropped. Stdlib must ship a fold family (`fold`, `foldRight`, `reduce`, `scan`, an early-exit form). Refcount-uniqueness in-place reuse is a C++ backend optimisation, not a language feature. |
| Effect set (§5) | Three orthogonal flags: `Nondet`, `Write`, `Suspend`. Pure is the empty set. | `IO` no longer exists, not even as an alias. `Time` and `Random` are subsumed by `Nondet`. `Scope` is not an effect. Flags name compiler-checked properties (cacheable, reorderable, needs async lowering), not capabilities. |
| Clock example (§6) | `now()` must be `! Nondet`. | The structural interface example `now() -> Instant` with no effect was a bug: an effect-free `now()` is memoisable. |
| CQRS mapping (§7) | Queries and `apply` may carry anything except `Write`. Handlers and services may carry any flag. | Replaces "none (or read-only `IO`)" in the table. A query may read the clock. |
| Exceptions (§7, §10) | `Result<T, E>` for recoverable errors, panics for unrecoverable ones. Panics are catchable only at supervisor points: handler dispatch, composition root, interop edge. | Settles the "whether exceptions exist" question. A `Result` propagation operator is mandatory. Pure functions may panic; memoisation does not cache a panic. C++ backend enables exceptions solely for supervisor-point unwinding. |
| Composition roots (§6, §10) | Compile-time resolution only. | The `dynamic` root is deferred, to be shaped after milestone 5 and for the .NET backend only. Config selection is a branch in the root; plugins are function-typed dependencies. |
| Lifetimes (§6) | Two lifetimes: singleton (inferred for pure-constructor services) and `scoped` (explicit keyword). Transient is replaced by factory dependencies. | Replaces lifetime inference from a `Scope` effect. A scope is one handler or query invocation. Constructors may carry effects, so the root is an effectful context. Captive-dependency check remains a compile error. |
| Async lowering on C++ (§4, §10) | C++20 coroutines. Single-threaded event loop with non-atomic refcounts for the prototype. | Settles "coroutines vs callbacks". Multi-threaded refcounting (atomic vs thread-confined) is a new open item. Parallelism (`parMap`, `race`) is separate from async and undesigned. |
| Milestones (§9) | Same seven, re-scoped. | Milestone 5 builds singletons and factory dependencies only. Scoped lifetimes, supervisor-point catching, coroutine lowering, and resource release all land in milestone 6 with handlers. |

**New small features required by these decisions:** a resource-ownership marker on types, a `use` block for early release, and the `Result` propagation operator. (Spelled 2026-09-08: `resource service`, `use x = expr`, postfix `?`.)

**Added 2026-09-06 after a worked sample** (`examples/bookings/`): a module system (`module` header, `use` imports, qualified names), a stdlib `Cell[T]` entity as the runtime-level mutable-state escape hatch with `Nondet` reads and `Write` writes, and explicit type arguments on calls. Details in `trebuchet-implementation-strategy.md` §7.1 and `trebuchet-syntax-sketch.md` §6a–6b.

**Added 2026-09-06, effect inference:** the `!` clause on a signature is optional and inferred from the body when absent; when present it is an upper bound. `! Pure` asserts no effects. `shape` members and function types must still declare. The rule "the signature is the boundary" in §5 is superseded. Details in `trebuchet-implementation-strategy.md` §7.3.

**Added 2026-09-08:** the .NET backend emits C# source compiled by Roslyn, not IL. See `trebuchet-implementation-strategy.md` §7.4.

**Added 2026-09-08, effect inference implemented:** milestone 3 is done in the prototype. Handlers in the samples infer as pure; the `Write` effect appears only in store adapters. See `trebuchet-implementation-strategy.md` §7.6.

**Added 2026-09-08, runtime and C# backend:** persistent collections exist for .NET and the interpreter runs on them; `treb emit` lowers a checked program to C# that builds and passes the same scenario as the interpreter. The C# backend was done before the C++ backend. See `trebuchet-implementation-strategy.md` §7.7.

**Added 2026-09-08, async lowering:** `Suspend` lowers to `async ValueTask<T>` in generated C#, decided per function by inferred effects. See `trebuchet-implementation-strategy.md` §7.8.

**Added 2026-09-08, C# host and C++ backend:** `treb emit --host` generates `IServiceCollection` registration and a stock ASP.NET project embeds the service; `treb emit --target cpp` produces a C++20 header that compiles with g++ and passes the same scenario. Milestones 4 and 7 (outbound) are done. See `trebuchet-implementation-strategy.md` §7.9.

**Added 2026-09-08, remaining gaps closed:** comments survive formatting; user-defined generics with type parameters in square brackets; effect polymorphism over function-typed parameters; `extern fn` with per-target host symbols and exception mapping; a non-atomic refcount and coroutine `Suspend` lowering on C++; C++ runtime tests. See `trebuchet-implementation-strategy.md` §7.10.

**Added 2026-09-08, resources and disposal:** `resource service X(...)` must define `fn release() -> Unit`; a fresh resource is bound with `use x = expr` and released at block end in reverse order, also on early return and panic. .NET lowers to `IDisposable`/`using` (async variants when `release` suspends); C++ releases from the destructor. The captive-dependency check is implemented. See `trebuchet-implementation-strategy.md` §7.11.

**Added 2026-09-08, host boundary:** the .NET host container supplies leaf dependencies; the generated `AddTrebuchet_<root>()` registers each entry as an overridable default, so host registrations made first win. `Vector` and `Map` implement the read-only .NET collection interfaces, and every extern result is converted at the edge (arrays to `Vector`, dictionaries to `Map`, null to `None`). Milestone 7 is complete in both directions. See `trebuchet-implementation-strategy.md` §7.12.

**Added 2026-09-08, .NET path closed:** Set builtins on all targets; `==` on collections lowers to structural `Equals` in C# (it was reference equality); `#line` directives map generated C# back to `.treb` lines; a `System.Text.Json` converter gives the C# host the dev server's JSON shape; `dotnet pack` produces the `treb` tool and the runtime package. See `trebuchet-implementation-strategy.md` §7.13.

**Added 2026-09-08, C++ event loop:** the runtime has a single-threaded `EventLoop` per thread with timers and a thread-safe `post` queue as the only cross-thread entry; `Task::get()` drives it; `sleep(ms)` is the first builtin that truly suspends on every target; hosts get `spawn` and a move-only `Completion<T>` for adapting callback APIs. The language has no spawn; parallelism stays undesigned. See `trebuchet-implementation-strategy.md` §7.14.

**Added 2026-09-08, recursive types:** generic instantiation is lazy, so recursive generic unions and records check; locals are no longer generalised (a checker bug that made recursive generic calls instantiate at `object`); C++ boxes a field whose type mentions its own type. `examples/trees/` runs on all three targets. See `trebuchet-implementation-strategy.md` §7.15.

**Added 2026-09-08, factories and supervision:** roots synthesise a function-typed dependency that returns a service (parameters fill the target's constructor by type, the rest resolve from the root); `supervise expr` is the supervisor point, yielding `Result[T, Panic]` and allowed only in handlers and service methods. The bookings API maps a caught panic to `Internal`. See `trebuchet-implementation-strategy.md` §7.16.

**Added 2026-09-08, visibility and acceptance:** `private` is enforced across modules and on service methods; the orders acceptance scenario runs on all three targets; dynamic roots are the host container's job. See `trebuchet-implementation-strategy.md` §7.17.

**Revised 2026-09-08, scoped is only a lifetime:** the earlier note that a scope boundary is where a transaction commits is withdrawn. `scoped` means one instance per handler invocation, as in ASP.NET; a transaction is a resource service with an explicit `commit` and rollback on `release`. See `trebuchet-implementation-strategy.md` §7.18.

**Added 2026-09-08, the demo grows:** attendees with a capacity check (`Set`), a waitlist with promotion on cancel, a generic `Page[T]`, cross-room stats (`Map`, `traverse`), an extern-backed info route, and `private` helpers; stdlib gains `take`, `drop`, `at`, `sortBy`, `traverse`; the dev server binds `csharp` externs by reflection. See `trebuchet-implementation-strategy.md` §7.19.

**Added 2026-09-08, patterns:** guards on match arms, list patterns with `...rest`, tuples as types, values, and patterns with `.0` access, as-patterns, and destructuring bindings. See `trebuchet-implementation-strategy.md` §7.20.

**Added 2026-09-08, shapes over types:** `shape Monoid[T]` is a type class, `instance Monoid[Money]` an instance, `[T: Monoid]` a constraint, `Monoid.combine(a, b)` a call resolved at compile time and lowered to dictionary passing on both targets. `Ord` is built in with comparison operators on constrained parameters and `sort`, `minimum`, `maximum`. See `trebuchet-implementation-strategy.md` §7.21.

**Added 2026-09-08, the rest of the Haskell list:** `?` on `Option`; lazy `Seq[T]` next to strict `Vector[T]`, with functions given to lazy combinators forbidden to `Suspend`; `treb test` runs `prop*` functions with generated, shrunk arguments. See `trebuchet-implementation-strategy.md` §7.22.

**Still open:** concrete syntax details (sketched in `trebuchet-syntax-sketch.md`), multi-threaded refcounting, parallelism primitives, and whether `mut` locals ever return.
