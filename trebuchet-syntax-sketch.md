# Trebuchet — Concrete Syntax Sketch

> Companion to `trebuchet-design-brief.md` and `trebuchet-implementation-strategy.md`. Those documents lock semantics; this one proposes a surface syntax. Everything here is a draft for discussion.

**Status:** sketch, revision 2, 2026-09-05. Amended 2026-09-06 with modules, type arguments on calls, and `Cell` after the bookings sample.

**Revision note.** Revision 1 also banned the shift key. That constraint removed colons, parentheses, uppercase, `->`, `!`, `?`, and symbolic operators, and the result was markedly less readable for the target audience. It has been dropped. The one rule that survives is the tree rule below. A record of what the shift ban cost is kept in §9 for reference.

---

## 1. The rule

**Source is a tree.** A file is a tree of nodes written with indentation, the way YAML writes nested data. A record declaration, a function body, a `with` update, a `match`, and a composition root are all the same shape: a head line, then indented children. There are no braces and no statement terminators.

Everything else is conventional. Where a C-family or ML-family spelling exists, use it.

---

## 2. Lexical conventions

| Element | Form | Example |
|---|---|---|
| Type names | UpperCamel | `Order`, `OrderEvent` |
| Values, functions, fields | lowerCamel | `order`, `placeOrder`, `lines` |
| Type arguments | square brackets | `Vector[Line]`, `Result[User, FetchError]` |
| Call | parentheses | `f(a, b)` |
| Return type | `->` | `fn total(order: Order) -> Money` |
| Effects | `!` then flags, optional; inferred when absent | `-> User ! Nondet Suspend` |
| Purity assertion | `! Pure` | `-> Money ! Pure` |
| Lambda | `\params -> body` | `\acc, line -> acc + line.total` |
| Match arm | `pattern => body` | `OrderPlaced(id, _, _) => ...` |
| Guard | `pattern if cond => body` | `Some(x) if x > 0 => ...` |
| Named-field pattern | `Ctor(field: pattern, ...)`; positional first; `...` ignores the rest | `BookingHeld(id: id, slot: s)`, `BookingCancelled(id, ...)` |
| List pattern | `[]`, `[a, b]`, `[first, ...rest]`, `[a, ..._]` | `[x, ...rest] => x + total(rest)` |
| Tuple | `(A, B)` type, `(a, b)` value or pattern, `p.0` item | `(lo, hi) = minMax(xs)` |
| As-pattern | `name @ pattern` | `whole @ Rect(w, h) if w == h => ...` |
| Result propagation | postfix `?`, on a `Result` or an `Option` | `repo.load(id)?`, `at(xs, 0)?` |
| Lazy sequence | `Seq[T]` from `Seq.iterate`, `Seq.range`, `Seq.from` | `take(Seq.iterate(0, \n -> n + 1), 5)` |
| Property | a fn named `prop...` returning `Bool`, run by `treb test` | `fn prop_reverseTwice(xs: Vector[Int]) -> Bool` |
| Binding | `=` | `state = fold(history, Order.empty, apply)` |
| Strings | double quotes | `"Brisbane"` |
| Comment | `//` | `// replay events` |
| Sequence item | `-` at line start | `- OrderPlaced(...)` |
| Type arguments on a call | square brackets before the parentheses | `json.decode[RoomEvent](payload)` |
| Zero-argument lambda | `\-> body` | `\-> BookingId(uuid())` |
| Type parameters | square brackets after a name | `record Pair[A, B](...)`, `fn swap[A, B](...)` |
| Constraint | `T: Shape` inside the brackets, several as `T: Ord Show` | `fn sort[T: Ord](xs: Vector[T])` |
| Shape over a type | `shape Name[T]` with members mentioning `T` | `shape Monoid[T]` |
| Instance | `instance Shape[Type]` with the members as fns | `instance Monoid[Money]` |
| Class call | `Shape.member(args)`, or `Shape.member[T]()` when no argument fixes `T` | `Monoid.combine(a, b)` |
| Extern | `extern fn` with a required `!` clause and host bindings | `extern fn f(x: String) -> Int ! Nondet` |
| Resource service | `resource service`, must define `release` | `resource service Conn(cfg: DbConfig)` |
| Resource binding | `use name = expr`, released at block end | `use conn = Conn(cfg)` |
| Supervisor point | `supervise expr`, yields `Result[T, Panic]`; handlers and service methods only | `supervise store.save(order)` |
| Module header | `module a.b.c`, first line | `module bookings.domain.fold` |
| Import | `use a.b.c` | `use bookings.domain.events` |

Square brackets for type arguments rather than angle brackets is deliberate. `<` and `>` are also comparison operators, and every language that uses them for generics pays a parsing cost for it. Scala and Python both use square brackets without trouble.

Operators are the usual set: `+ - * / %`, `== != < <= > >=`, `and or not`. Word forms for boolean operators because `&&` and `||` are noise.

---

## 3. Nodes, lines, and children

A line is an expression. Its indented children are further arguments to it. These are equivalent:

```
append(state.lines, e.line)
```

```
append
  state.lines
  e.line
```

```
append(state.lines)
  e.line
```

The last form, some arguments inline and the rest as children, is the common one for calls whose last argument is a lambda or a block:

```
fold(order.lines, Money.zero)
  \acc, line -> acc + line.price * line.qty
```

A `-` at the start of a line makes it a sequence item rather than an argument:

```
lines =
  - Line("sku-1", 2, price)
  - Line("sku-2", 1, price)
```

Inline form: `lines = [Line("sku-1", 2, price), Line("sku-2", 1, price)]`.

---

## 4. Declarations

### Records

```
record Order
  id: OrderId
  customer: CustomerId
  lines: Vector[Line]
  status: OrderStatus
```

Deep immutability, structural equality, and sealed are defaults. There is nothing to write for them.

Field validation is a child of the field:

```
record Person
  name: String
    init => if value.trim().length > 0 then value.trim() else fail(ArgumentError)
```

Reference identity is opt-in:

```
entity Session
  id: SessionId
  openedAt: Instant
```

### Unions

```
union OrderEvent
  OrderPlaced(id: OrderId, customer: CustomerId, lines: Vector[Line])
  OrderCancelled(id: OrderId, reason: String)
```

Each variant is a record. A variant with no fields is written bare:

```
union OrderStatus
  New
  Placed
  Cancelled
```

### Functions

```
fn total(order: Order) -> Money
  fold(order.lines, Money.zero)
    \acc, line -> acc + line.price * line.qty

fn fetch(id: UserId) -> Result[User, FetchError] ! Nondet Suspend
  http.get(userUrl(id))

fn total(order: Order) -> Money ! Pure
  fold(order.lines, Money.zero)
    \acc, line -> acc + line.price * line.qty
```

Effect flags follow `!` in any order. The clause is optional: without it, effects are inferred from the body. With it, the compiler checks the body needs no more than declared. `! Pure` asserts that a function has no effects; it cannot be combined with other flags. `shape` members and function types have no body and must carry a clause.

The body is the children, and the last expression is the value.

### Event folds

```
fn apply(state: Order, ev: OrderEvent) -> Order
  match ev
    OrderPlaced(_, customer, lines) =>
      state with
        customer: customer
        lines: lines
        status: OrderStatus.Placed
    OrderCancelled(_, _) =>
      state with
        status: OrderStatus.Cancelled
```

Arms are children of `match`. Exhaustiveness is checked. A one-line arm puts the body after `=>`.

Variant patterns destructure positionally: `OrderPlaced(_, customer, lines)` binds the second and third fields and ignores the first. The number of binders must equal the number of fields, or be zero for a bare variant name. There is no way yet to bind the whole variant as a value; see §8.

### Services

```
service OrderService(repo: OrderRepo, clock: Clock)
  fn place(cmd: PlaceOrder) -> Result[Unit, OrderError]
    now = clock.now()
    history = repo.load(cmd.id)?
    ...
```

Lifetime is inferred as singleton unless declared:

```
scoped service OrderTx(conn: DbConnection)
  ...
```

A service that owns something to give back is a `resource`, must define `release`, and is bound with `use` wherever it is created. Resources release at the end of the block in reverse order, on early return and on panic too:

```
resource service DbConnection(config: DbConfig)
  fn exec(sql: String) -> Result[Unit, DbError] ! Write Suspend
    ...
  fn release() -> Unit ! Suspend
    ...

handler migrate(config: DbConfig) -> Result[Unit, DbError]
  use conn = DbConnection(config)
  conn.exec("create table ...")?
  ok(unit)
```

Modifiers combine in any order: `scoped resource service`. A record or union may not hold a resource. Binding a fresh resource with `=` instead of `use` is an error.

`scoped` is only a lifetime: one instance per handler or query invocation. A transaction is a resource with an explicit commit; nothing commits at a scope boundary:

```
handler place(cmd: PlaceOrder) -> Result[Unit, OrderError]
  use tx = db.begin()
  tx.exec("insert ...", [])?
  tx.commit()
```

A factory dependency is a function type. When the root has no entry for it and the function returns a service, the root synthesises the lambda: its parameters fill the target's constructor by type in order, the rest resolve from the root.

```
service Exporter(openOutput: fn(Path) -> FileStream ! Nondet Suspend)
  fn export(orders: Vector[Order], path: Path) -> Result[Unit, ExportError]
    stream = openOutput(path)?
    ...
```

### Handlers

A handler or a service method may contain `supervise expr`: a panic raised while evaluating the expression becomes `Error(Panic(message))`, and resources released on the way out. A `fn` may not, so pure code never observes a panic.

```
handler placeOrder(cmd: PlaceOrder) -> Result[OrderId, ApiError]
  outcome = supervise orders.place(cmd)
  outcome.mapError(\p -> Internal(p.message))?
```

```
handler place(cmd: PlaceOrder, state: Order) -> Result[Vector[OrderEvent], OrderError] ! Write
  if state.status != OrderStatus.New then
    error(OrderError.AlreadyPlaced)
  else if cmd.lines.isEmpty then
    error(OrderError.EmptyOrder)
  else
    ok
      - OrderPlaced(cmd.id, cmd.customer, cmd.lines)
```

Same shape as `fn`. The keyword marks a `Write` site and a scope boundary.

### Structural interfaces

None need declaring. `Clock` above is satisfied by anything with `now() -> Instant ! Nondet`. A `shape` declaration gives a name to a set of members when one is wanted:

```
shape Clock
  fn now() -> Instant ! Nondet
```

---

## 5. Expressions

### Shapes over types

```
shape Monoid[T]
  fn empty() -> T ! Pure
  fn combine(a: T, b: T) -> T ! Pure

instance Monoid[Money]
  fn empty() -> Money
    Money(0)
  fn combine(a: Money, b: Money) -> Money
    Money(a.cents + b.cents)

fn concatAll[T: Monoid](xs: Vector[T]) -> T ! Pure
  fold(xs, Monoid.empty[T](), \a, b -> Monoid.combine(a, b))
```

`Ord` is built in, with instances for Int, Float, String, Bool, and Instant; `instance Ord[Money]` adds one with a `compare`. On a parameter declared `T: Ord`, `<` and friends work, and `sort`, `minimum`, and `maximum` accept the vector. Equality and `toString` need no constraint: every type has them.

### Patterns

```
fn describe(s: Shape) -> String ! Pure
  match s
    Circle(r) if r == 0 => "point"
    whole @ Rect(w, h) if w == h => "square " + toString(area(whole))
    Rect(w, h) => "rect"
    _ => "circle"

fn total(xs: Vector[Int]) -> Int ! Pure
  match xs
    [] => 0
    [x, ...rest] => x + total(rest)

fn swap[A, B](p: (A, B)) -> (B, A) ! Pure
  (a, b) = p
  (b, a)
```

A function may be declared inside a body, with the same syntax as at the top level. It sees the parameters and the locals above it, may call itself, and inherits the enclosing constraints; a local `handler` is allowed only inside a handler or a service method. Lambdas remain for the nameless case.

```
fn largest[T: Ord](xs: Vector[T]) -> Option[T] ! Pure
  fn keep(acc: Option[T], x: T) -> Option[T]
    match acc
      None => Some(x)
      Some(m) => if x > m then Some(x) else acc
  fold(xs, None, \acc, x -> keep(acc, x))
```

A constructor pattern may name its fields, `Held(guest: g, ...)`, and may end with `...` to ignore the rest; positional entries come first and, without `...`, cover every field. Records match the same way: `Point(x: 0, y: 0)`. There is no punning: `Room(capacity)` is a positional bind, not a field.

A guarded arm never counts towards exhaustiveness. A match on a vector is exhaustive when some `[..., ...rest]` arm takes every length from k up and each shorter length has an exact arm. A binding line may be any pattern that cannot fail; one that can is an error that points at `match`.

### Collections

Literals: `[a, b]` is a `Vector`, `{k: v}` is a `Map`; there is no set literal, `toSet([...])` builds one. The builtin families are `map`, `filter`, `fold`, `any`, `all`, `find`, `forEach`, `append`, `concat`, `first`, `last`, `reverse`, `contains`, `length`, `isEmpty` on vectors; `take`, `drop`, `at`, `sortBy`, `traverse` (stops at the first `Error`) on vectors; `get`, `set`, `remove`, `keys`, `values`, `contains`, `getOr` on maps; `toSet`, `add`, `remove`, `contains`, `items`, `merge`, `intersect`, `difference` on sets. `merge` rather than `union` because `union` declares a type. `==` is structural on every collection.

Records and unions may be recursive, directly (`Node(left: Tree[T], right: Tree[T])`) or through `Option` or a collection; nothing marks the recursion, the C++ backend boxes it.

`Seq[T]` is lazy: `map`, `filter`, `take`, `drop`, `takeWhile` on a `Seq` return a `Seq` without pulling; `toVector`, `first`, `fold` pull. A function given to a lazy combinator may not `Suspend`. A `Seq` is not compared with `==`.

`sleep(ms)` is `! Suspend` and is the reference builtin for suspension; a function that calls it infers `Suspend`. There is no `spawn`; a Trebuchet body is sequential and concurrency is the host's, until parallelism is designed.

### Deep update

```
updated = order with
  customer.address.city: "Brisbane"
  lines: append(order.lines, line)
```

Children are `path: value`. An update reads like a partial document. This is the construct where the tree rule pays off most.

Inline form for a single field: `order with { status: OrderStatus.Placed }`.

### Result propagation

```
history = repo.load(cmd.id)?
```

Postfix `?` unwraps `ok` or returns the `error` from the enclosing function.

### Conditionals

```
if cond then
  thenExpr
else
  elseExpr
```

Inline: `if x > 0 then "pos" else "neg"`.

### Lambdas

```
\acc, line -> acc + line.total
```

Multi-line body as children:

```
fold(events, Order.empty)
  \state, ev ->
    apply(state, ev)
```

Parameter types are inferred. Annotate with `\(acc: Money, line: Line) -> ...` when needed.

### Method-style calls

`x.f(a)` is sugar for `f(x, a)`. `order.lines`, `clock.now()`, and `state.lines.append(line)` all resolve to plain functions.

---

## 6. Composition roots

```
root main
  clock: SystemClock
  repo: PgOrderRepo
    config: DbConfig.fromEnv()
  orderService: OrderService
```

Each child is `name: provider`, with the provider's own arguments as children. Unlisted dependencies resolve by type. This is the most YAML-like part of the language and deliberately so: a root is configuration.

---

## 6a. Modules

One module per file. The header is the first line.

```
module bookings.domain.fold

use bookings.domain.booking
use bookings.domain.events
```

`use` brings a module's public declarations into scope unqualified. When two imported modules export the same name, or a local name shadows an imported one, qualify by the last segment of the module path:

```
events = handlers.request(cmd, room, clock.now())?
```

Declarations are public unless marked `private`. A private declaration is invisible to `use`, unqualified or qualified; a private union hides its variants. A `private` method of a service is callable only from that service's other methods. One file is one module, named by its `module` header.

A worked example of a multi-file program is in `examples/bookings/`, split into domain, application, infrastructure, api, and host layers.

---

## 6b. Mutable state

There is no mutation in the language. Where a program genuinely needs state that changes, such as an in-memory store or a counter, it holds a `Cell[T]` from the stdlib:

```
service InMemoryRoomStore(log: Cell[Map[RoomId, Vector[RoomEvent]]])
  fn load(id: RoomId) -> Result[Vector[RoomEvent], BookingError] ! Nondet Suspend
    ok(log.get().getOr(id, []))

  fn append(id: RoomId, events: Vector[RoomEvent]) -> Result[Unit, BookingError] ! Write Suspend
    log.update(\m -> m.set(id, concat(m.getOr(id, []), events)))
    ok(unit)
```

`Cell.new` and `get` are `Nondet`; `set` and `update` are `Write`. A `Cell` is an `entity`. Pure code cannot reach one.

---

## 7. The acceptance domain, end to end

The milestone 6 test domain.

```
// --- data ---

record OrderId(value: String)
record CustomerId(value: String)

record Line
  sku: String
  qty: Int
  price: Int

union OrderStatus
  New
  Placed
  Cancelled

record Order
  id: OrderId
  customer: CustomerId
  lines: Vector[Line]
  status: OrderStatus

union OrderEvent
  OrderPlaced(id: OrderId, customer: CustomerId, lines: Vector[Line])
  OrderCancelled(id: OrderId, reason: String)

union OrderError
  AlreadyPlaced
  EmptyOrder

record PlaceOrder
  id: OrderId
  customer: CustomerId
  lines: Vector[Line]

// --- pure logic ---

fn apply(state: Order, ev: OrderEvent) -> Order ! Pure
  match ev
    OrderPlaced(_, customer, lines) =>
      state with
        customer: customer
        lines: lines
        status: OrderStatus.Placed
    OrderCancelled(_, _) =>
      state with
        status: OrderStatus.Cancelled

fn total(order: Order) -> Int ! Pure
  fold(order.lines, 0)
    \acc, line -> acc + line.price * line.qty

fn openOrders(orders: Vector[Order]) -> Vector[Order]
  filter(orders, \o -> o.status == OrderStatus.Placed)

// --- ports ---

shape OrderRepo
  fn load(id: OrderId) -> Result[Vector[OrderEvent], OrderError] ! Nondet Suspend
  fn append(id: OrderId, events: Vector[OrderEvent]) -> Result[Unit, OrderError] ! Write Suspend

// --- effectful edge ---

handler placeOrder(cmd: PlaceOrder, state: Order) -> Result[Vector[OrderEvent], OrderError]
  if state.status != OrderStatus.New then
    error(OrderError.AlreadyPlaced)
  else if cmd.lines.isEmpty then
    error(OrderError.EmptyOrder)
  else
    ok
      - OrderPlaced(cmd.id, cmd.customer, cmd.lines)

service OrderService(repo: OrderRepo)
  fn place(cmd: PlaceOrder) -> Result[Unit, OrderError]
    history = repo.load(cmd.id)?
    state = fold(history, Order(cmd.id, cmd.customer, [], OrderStatus.New), apply)
    events = placeOrder(cmd, state)?
    repo.append(cmd.id, events)

// --- infrastructure ---

service InMemoryOrderRepo(log: Cell[Map[OrderId, Vector[OrderEvent]]])
  fn load(id: OrderId) -> Result[Vector[OrderEvent], OrderError]
    ok(log.get().getOr(id, []))

  fn append(id: OrderId, events: Vector[OrderEvent]) -> Result[Unit, OrderError]
    log.update(\m -> m.set(id, concat(m.getOr(id, []), events)))
    ok(unit)

// --- wiring ---

root main
  log: Cell.new({})
  repo: InMemoryOrderRepo
  orderService: OrderService
```

Note that a short record can be declared inline, `record OrderId(value: String)`, and a longer one as a tree. Both are the same declaration.

---

## 8. Open choices

1. Whether `then` is required on `if` lines or the newline suffices.
2. Whether `=>` for match arms and `->` for return types and lambdas should collapse to one arrow.
3. Whether the inline `with { ... }` form is needed or the tree form is always used.
4. Whether `shape` declarations exist or structural interfaces stay anonymous.
5. Whether `x.f(a)` sugar is worth the ambiguity between field access and method call.
6. Indentation width and whether tabs are permitted.
7. String interpolation syntax.
8. ~~Module visibility~~: settled as public-by-default with `private`, enforced (§6a).
9. Whether a service method may share a name with a top-level function, as in the bookings sample where the method `request` calls the handler `request` by qualification.
10. Whether `\->` is the right spelling for a zero-argument lambda.
11. Whether a service method may shadow a top-level function of the same name at all. The type checker caught a case where the shadow silently changed which function was called; forbidding it, or requiring qualification, may be better than the nearest-scope rule.
12. ~~Whether a variant pattern needs an as-binding or named-field form~~: both exist (§5).

---

## 9. Record: what the shift-key ban cost

Kept for reference. Revision 1 required every character to be typeable without shift on a US ANSI layout, leaving only `` ` - = [ ] \ ; ' , . / `` plus digits and lowercase. The consequences were:

- No colon, so declarations could not be `name: type`.
- No parentheses, so calls were juxtaposition only.
- No uppercase, so types and values shared one namespace of hyphenated words, producing parameters like `order order`.
- No `->`, `!`, `?`, `<`, `>`, `+`, `*`, so return types, effects, propagation, and arithmetic all became words or repurposed `=`.

The tree rule was never dependent on any of this and is retained. The shift-key rule is dropped.
