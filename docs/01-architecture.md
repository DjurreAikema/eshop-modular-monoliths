# Architecture

## The one-paragraph version

One ASP.NET Core process (`Bootstrapper/Api`) hosts three business modules (`Catalog`,
`Basket`, `Ordering`). Each module is a separate assembly with its own `DbContext`, its own
Postgres schema and its own migration history. Modules never reference each other's
implementation assembly — they communicate through published contract assemblies (synchronous,
in-process) or integration events on RabbitMQ (asynchronous). Inside a module, code is organised
by *use case* (vertical slices), not by technical layer.

---

## Physical layout

```
src/
├── eshop-modular-monolith.sln
├── docker-compose.yml / .override.yml     Infrastructure only (no app container)
│
├── Bootstrapper/
│   └── Api/                               THE ONLY EXECUTABLE
│       ├── Program.cs                     Composition root
│       └── appsettings.json               All configuration for all modules
│
├── Modules/
│   ├── Catalog/                           Business module
│   ├── Catalog.Contracts/                 Catalog's public API for other modules
│   ├── Basket/                            Business module
│   └── Ordering/                          Business module
│
├── Shared/                                Shared kernel: DDD base types, behaviors,
│                                          EF interceptors, exceptions, pagination
├── Shared.Contracts/                      CQRS marker interfaces (ICommand/IQuery/...)
└── Shared.Messaging/                      MassTransit setup + integration event contracts
```

## Project reference graph

This graph *is* the architecture. Everything else is convention; this is enforced by the
compiler.

```
                      ┌─────────────────┐
                      │       Api       │   (only executable)
                      └────────┬────────┘
              ┌────────────────┼────────────────┐
              ▼                ▼                ▼
        ┌──────────┐     ┌──────────┐     ┌──────────┐
        │  Basket  │     │ Catalog  │     │ Ordering │
        └────┬─────┘     └────┬─────┘     └────┬─────┘
             │                │                │
             └───────┬────────┘                │
                     ▼                         │
           ┌───────────────────┐               │
           │ Catalog.Contracts │               │
           └─────────┬─────────┘               │
                     │                         │
       ┌─────────────┴───────┬─────────────────┘
       ▼                     ▼
┌──────────────┐    ┌──────────────────┐
│    Shared    │    │ Shared.Messaging │
└──────┬───────┘    └──────────────────┘
       ▼
┌──────────────────┐
│ Shared.Contracts │   (MediatR only — no infrastructure)
└──────────────────┘
```

**The rule in one line:** an arrow may never point from one module to another module's
implementation assembly. Only to a `*.Contracts` assembly.

| Project | Depends on | Purpose |
|---|---|---|
| `Api` | Basket, Catalog, Ordering | Composition root. Owns configuration and the HTTP pipeline. |
| `Catalog` | Shared, Shared.Messaging, Catalog.Contracts | Product catalog. |
| `Basket` | Shared, Shared.Messaging, **Catalog.Contracts** | Shopping cart. Reads product data via Catalog's published query. |
| `Ordering` | Shared, Shared.Messaging | Orders. Reacts to checkout events; publishes nothing yet. |
| `Catalog.Contracts` | Shared.Contracts | `ProductDto`, `GetProductByIdQuery`/`Result`. The *only* Catalog types other modules may use. |
| `Shared` | Shared.Contracts | DDD base classes, pipeline behaviors, EF interceptors, exception handler, pagination, DI extensions. |
| `Shared.Contracts` | (MediatR) | `ICommand`, `ICommandHandler`, `IQuery`, `IQueryHandler`. No infrastructure. |
| `Shared.Messaging` | (MassTransit) | MassTransit registration + integration event records. |

> **Deviation to be aware of:** `Ordering` and `Basket` have no Contracts assembly, and
> integration events live in the shared `Shared.Messaging` rather than in each publisher's
> Contracts assembly. See [06-known-issues.md](06-known-issues.md#7-integration-event-contracts-live-in-a-shared-assembly).

---

## The composition root

`src/Bootstrapper/Api/Program.cs` is the whole of the application wiring. It does exactly four
things:

```csharp
// 1. Collect the module assemblies
var basketAssembly   = typeof(BasketModule).Assembly;
var catalogAssembly  = typeof(CatalogModule).Assembly;
var orderingAssembly = typeof(OrderingModule).Assembly;

// 2. Register cross-cutting infrastructure ONCE, scanning all module assemblies
builder.Services.AddCarterWithAssemblies(basketAssembly, catalogAssembly, orderingAssembly);
builder.Services.AddMassTransitWithAssemblies(builder.Configuration, basketAssembly, ...);
builder.Services.AddMediatRWithAssemblies(basketAssembly, catalogAssembly, orderingAssembly);
builder.Services.AddStackExchangeRedisCache(...);
builder.Services.AddKeycloakWebApiAuthentication(builder.Configuration);

// 3. Let each module register its own services
builder.Services
    .AddBasketModule(builder.Configuration)
    .AddCatalogModule(builder.Configuration)
    .AddOrderingModule(builder.Configuration);

// 4. Build the pipeline, then let each module configure itself
app.MapCarter();
app.UseSerilogRequestLogging();
app.UseExceptionHandler(_ => { });
app.UseAuthentication();
app.UseAuthorization();

app.UseBasketModule().UseCatalogModule().UseOrderingModule();
```

Note what is *not* there: no endpoint mapping, no handler registration, no validator
registration. All discovered by assembly scan.

### Consequence for refactoring

Adding a **feature** → no change to `Program.cs`.
Adding a **module** → add the assembly variable, the `AddXModule` call, the `UseXModule` call,
and a `ProjectReference` in `Api.csproj`. Four edits, all mechanical.

---

## The three runtime "spines"

Three independently-scanned registries run through the process. Understanding which one a piece
of code plugs into explains most of the codebase.

### 1. Carter → HTTP

Any `public class X : ICarterModule` in a scanned assembly has its `AddRoutes` called at
startup. This is how endpoints reach the router without a central map file.

### 2. MediatR → in-process dispatch

Carries three distinct things over one bus:

| Kind | Interface | Dispatched by | Crosses modules? |
|---|---|---|---|
| Command | `ICommand<TResult>` | `ISender.Send` from an endpoint | No (except via Contracts) |
| Query | `IQuery<TResult>` | `ISender.Send` from an endpoint or another module | Yes, when the type lives in a Contracts assembly |
| Domain event | `IDomainEvent : INotification` | `DispatchDomainEventsInterceptor` on `SaveChanges` | No — stays inside the owning module |

Two open behaviors wrap every `Send`:

```
Request ─► ValidationBehavior ─► LoggingBehavior ─► Handler
```

`ValidationBehavior` is constrained to `where TRequest : ICommand<TResponse>`, so **queries are
never validated** — a deliberate choice worth knowing.

### 3. MassTransit → RabbitMQ

Any `IConsumer<TEvent>` in a scanned assembly is bound to a queue. This is the only mechanism
that survives a module being extracted into a separate service, which is why it is used for
anything that must not be a synchronous dependency.

---

## Data architecture

```
Postgres: EShopDb
├── schema "catalog"    CatalogDbContext   → Products, __EFMigrationsHistory
├── schema "basket"     BasketDbContext    → ShoppingCarts, ShoppingCartItems,
│                                            OutboxMessages, __EFMigrationsHistory
├── schema "ordering"   OrderingDbContext  → Orders, OrderItems, __EFMigrationsHistory
└── schema "identity"   Keycloak's own storage
```

Each `DbContext` pins its schema in `OnModelCreating`:

```csharp
protected override void OnModelCreating(ModelBuilder builder)
{
    builder.HasDefaultSchema("catalog");
    builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    base.OnModelCreating(builder);
}
```

`Assembly.GetExecutingAssembly()` is what keeps configurations module-local: a module can only
pick up `IEntityTypeConfiguration` classes from its own assembly.

**Rules:**

- One `DbContext` per module. A handler may only inject its own module's context.
- No foreign keys across schemas. `ShoppingCartItem.ProductId` is a plain `Guid`, not a
  navigation property to `Product`.
- Data another module owns is either fetched on demand (Contracts query) or **copied** on an
  event. `ShoppingCartItem` stores `Price` and `ProductName` as its own columns — a deliberate
  denormalised copy, kept fresh by `ProductPriceChangedIntegrationEvent`.
- Migrations are applied at startup by `UseMigration<TContext>()` inside each `UseXModule`.

### Shared EF infrastructure

Two `ISaveChangesInterceptor`s are registered by every module:

| Interceptor | Effect |
|---|---|
| `AuditableEntityInterceptor` | Fills `CreatedAt`/`CreatedBy`/`LastModified`/`LastModifiedBy` on any `IEntity`. |
| `DispatchDomainEventsInterceptor` | Publishes each aggregate's `DomainEvents` through MediatR, then clears them. |

> Both run on `SavingChanges` — i.e. **before** the transaction commits. See
> [06-known-issues.md](06-known-issues.md#2-domain-events-are-dispatched-before-commit).

---

## Request lifecycle, end to end

Taking `POST /basket/{userName}/items`:

```
HTTP POST
  │
  ▼
AddItemIntoBasketEndpoint (ICarterModule)           Basket module
  │  builds AddItemIntoBasketCommand
  ▼
ISender.Send
  ├─► ValidationBehavior      FluentValidation → throws ValidationException → 400
  ├─► LoggingBehavior         Serilog start/stop + slow-request warning
  ▼
AddItemIntoBasketHandler
  ├─► IBasketRepository.GetBasket           (CachedBasketRepository → Redis → BasketRepository → EF)
  ├─► ISender.Send(GetProductByIdQuery)     ──────► CROSSES INTO CATALOG
  │                                                 GetProductByIdHandler → CatalogDbContext
  ├─► shoppingCart.AddItem(...)             domain method, invariants enforced
  └─► repository.SaveChangesAsync
        ├─► AuditableEntityInterceptor
        ├─► DispatchDomainEventsInterceptor
        └─► CachedBasketRepository evicts the Redis key
  ▼
201 Created
```

Any exception escaping the handler is caught by `CustomExceptionHandler` and rendered as RFC
7807 `ProblemDetails`:

| Exception | Status |
|---|---|
| `ValidationException` (FluentValidation) | 400 — with a `ValidationErrors` extension |
| `BadRequestException` | 400 |
| `NotFoundException` (e.g. `ProductNotFoundException`, `BasketNotFoundException`) | 404 |
| `InternalServerException` | 500 |
| anything else | 500 |

---

## What runs in the background

| Component | Module | What it does |
|---|---|---|
| `OutboxProcessor` (`BackgroundService`) | Basket | Every 10s, reads unprocessed `OutboxMessages`, publishes each to RabbitMQ, marks processed. |
| MassTransit consumers | Basket, Ordering | `ProductPriceChangedIntegrationEventHandler`, `BasketCheckoutIntegrationEventHandler`. |
| Migration + seeding | all | Runs once at startup inside `UseXModule`. |

---

## Design decisions and their trade-offs

| Decision | Benefit | Cost | Exit path if it stops fitting |
|---|---|---|---|
| Project per module | Compiler-enforced boundary | More projects, slower build | — |
| Schema per module, one database | Isolation with cheap local transactions | Not true physical isolation; nothing *stops* a cross-schema query | Point one module's connection string at a new database |
| Vertical slices | Change locality, easy deletion | Some duplication between slices | — |
| MediatR for in-module dispatch | Uniform pipeline for validation/logging | Indirection; licensing on v12+ | Handlers are plain classes; could be called directly |
| Contracts assembly for sync calls | Explicit, compile-checked public API | Extra project per module | Replace with an integration event when you want the call to be async |
| Integration events over RabbitMQ | Survives module extraction | Eventual consistency, needs outbox + idempotency | — |
| Carter for endpoints | No central route file | Another dependency | Minimal APIs directly, with a per-module `MapXEndpoints()` |
| Outbox (Basket only) | Atomic "save + publish intent" | Polling latency, needs dedupe | — |
