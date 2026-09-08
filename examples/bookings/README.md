# Bookings — onion layout

The single-file sample split into onion-architecture layers. Dependencies point inward only.

```
host/            composition roots            -> api, application, infrastructure
api/             HTTP edge, wire contracts    -> application, domain
infrastructure/  adapters: Postgres, memory   -> application (ports), domain
application/     use cases and ports          -> domain
domain/          records, events, folds,      -> nothing
                 queries, handlers
```

| Layer | Files | Contents |
|---|---|---|
| domain | `ids`, `slot`, `booking`, `commands`, `events`, `fold`, `queries`, `handlers`, `paging` | Pure data and logic. Handlers are here because they take state and return events; the `Write` flag marks them as the only mutation sites, but they touch nothing outside their arguments. `paging` is a generic `Page[T]`. |
| application | `ports`, `booking-service` | The `RoomStore` and `Clock` shapes the use cases need, and the service that loads state, runs a handler, and appends events. |
| infrastructure | `postgres-room-store`, `in-memory-room-store`, `clocks`, `ids` | Adapters. Each satisfies a port structurally; nothing declares `implements`. |
| api | `contracts`, `errors`, `booking-api`, `system` | Request and response records, error mapping, one function per route, and the `hostName` extern the info route uses. |
| host | `main`, `test` | Composition roots. The only place infrastructure and application are named together. |

## Syntax introduced by the split

Neither the design brief nor the syntax sketch had a module system. This sample assumes:

- `module a.b.c` as the first line of a file.
- `use a.b.c` to import a module's public declarations unqualified.
- A qualified reference, `handlers.request(...)`, when a name would otherwise clash.

Also assumed: a stdlib `Cell[T]` with `get ! Nondet` and `set`/`update ! Write`, as the way to hold mutable state inside an otherwise immutable program. The in-memory store and the sequential id generator both use it.
