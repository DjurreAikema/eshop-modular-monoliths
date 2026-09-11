# Module anatomy

Every business module has the same internal shape. Learn it once and all three modules — and
every module you create — read identically.

## Canonical folder layout

```
Modules/<Module>/
├── <Module>.csproj
├── <Module>Module.cs                 ← DI + pipeline registration (the module's only "public" wiring)
├── GlobalUsings.cs                   ← optional; namespaces used by nearly every file
│
├── <Aggregate>/                      ← one folder per aggregate root (Products, Basket, Orders)
│   ├── Models/                       ← aggregate root, entities  (the domain)
│   ├── ValueObjects/                 ← immutable value types (Address, Payment)
│   ├── Dtos/                         ← shapes crossing the module boundary
│   ├── Events/                       ← domain events (IDomainEvent)
│   ├── EventHandlers/                ← INotificationHandler (domain) + IConsumer (integration)
│   ├── Exceptions/                   ← module-specific exceptions
│   └── Features/                     ← THE USE CASES
│       └── <UseCase>/
│           ├── <UseCase>Endpoint.cs  ← HTTP shape
│           └── <UseCase>Handler.cs   ← Command/Query + Validator + Handler
│
└── Data/
    ├── <Module>DbContext.cs
    ├── Configurations/               ← IEntityTypeConfiguration per entity
    ├── Migrations/                   ← EF migrations (module-owned history table)
    ├── Seed/                         ← optional IDataSeeder
    ├── Repository/                   ← optional; only when you need a decorator
    ├── Processors/                   ← optional; BackgroundService (e.g. outbox)
    └── JsonConverters/               ← optional; only if you cache domain objects
```

Real examples:

| | Catalog | Basket | Ordering |
|---|---|---|---|
| Aggregate folder | `Products/` | `Basket/` | `Orders/` |
| Aggregate root | `Product` | `ShoppingCart` | `Order` |
| Child entities | — | `ShoppingCartItem` | `OrderItem` |
| Value objects | — | — | `Address`, `Payment` |
| Use cases | 6 | 6 | 4 |
| Repository? | no | yes (for Redis caching) | no |
| Outbox? | no | yes | no |
| Seeder? | yes | no | no |

> Note the doubled name in Basket: the module folder and the aggregate folder are both called
> `Basket`, producing namespaces like `Basket.Basket.Features.CreateBasket`. It is legal but
> awkward — prefer a plural aggregate name (`Baskets/`) in new modules.

---

## The three mandatory pieces

### 1. `<Module>Module.cs` — the registration surface

This is the only file the bootstrapper knows about. Every module implements the same two
extension methods with the same three comment sections, in the same order.

```csharp
namespace Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Api endpoint services
        //   (usually empty — Carter discovers endpoints by assembly scan)

        // Application use case services
        //   e.g. services.AddScoped<IBasketRepository, BasketRepository>();
        //        services.Decorate<IBasketRepository, CachedBasketRepository>();

        // Data/Infrastructure services
        var connectionString = configuration.GetConnectionString("Database");

        services.AddScoped<ISaveChangesInterceptor, AuditableEntityInterceptor>();
        services.AddScoped<ISaveChangesInterceptor, DispatchDomainEventsInterceptor>();

        services.AddDbContext<CatalogDbContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetServices<ISaveChangesInterceptor>());
            options.UseNpgsql(connectionString);
        });

        services.AddScoped<IDataSeeder, CatalogDataSeeder>();

        return services;
    }

    public static IApplicationBuilder UseCatalogModule(this IApplicationBuilder app)
    {
        // Api endpoint services
        // Application use case services

        // Data/Infrastructure services
        app.UseMigration<CatalogDbContext>();

        return app;
    }
}
```

Rules:
- Both methods return their receiver so the bootstrapper can chain them.
- Keep the three comment headings even when a section is empty — they tell the next reader (and
  the next AI) exactly where a new registration belongs.
- A module registers *only its own* services. Anything shared (MediatR, Carter, MassTransit,
  Redis, auth) is registered once in `Program.cs`.

### 2. `<Module>DbContext.cs` — the data boundary

```csharp
public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("catalog");                              // ← isolation
        builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly()); // ← module-local
        base.OnModelCreating(builder);
    }
}
```

Both lines matter. `HasDefaultSchema` keeps the tables in the module's own namespace;
`GetExecutingAssembly()` guarantees the context cannot accidentally pick up another module's
entity configuration.

### 3. `Features/<UseCase>/` — the use case

Two files, always in this shape. See [04-adding-a-feature.md](04-adding-a-feature.md) for the
full templates.

---

## Domain model conventions

Aggregate roots derive from `Aggregate<TId>`; child entities from `Entity<TId>`. Both bring the
audit fields (`CreatedAt`, `CreatedBy`, `LastModified`, `LastModifiedBy`); `Aggregate<TId>` adds
the domain-event collection.

```csharp
public class Product : Aggregate<Guid>
{
    public string Name { get; private set; } = null!;       // ← private setters
    public decimal Price { get; private set; }

    public static Product Create(Guid id, string name, /* ... */ decimal price)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);          // ← invariants at the boundary
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);

        var product = new Product { Id = id, Name = name, Price = price };
        product.AddDomainEvent(new ProductCreatedEvent(product));   // ← event raised here
        return product;
    }

    public void Update(string name, /* ... */ decimal price)
    {
        Name = name;
        if (Price != price)                                   // ← event only on real change
        {
            Price = price;
            AddDomainEvent(new ProductPriceChangedEvent(this));
        }
    }
}
```

The rules this encodes:

1. **All setters are private.** State changes only through domain methods.
2. **Construction goes through a static factory** (`Create` / `Of`), never a public constructor.
3. **Invariants are enforced in the factory and in mutators**, not in the handler.
4. **Domain events are raised by the domain**, not by the handler — so any path that changes
   the price raises the event.
5. **Collections are exposed read-only:** a private `List<T> _items` with a public
   `IReadOnlyList<T> Items => _items.AsReadOnly()`.

Value objects (`Address`, `Payment`) follow the same pattern with a static `Of(...)` factory, a
`protected` parameterless constructor for EF, and get-only properties. They are mapped with
`ComplexProperty` in the entity configuration — they get no table of their own.

---

## Optional pieces, and when to add them

### Repository — only to hang a decorator on

Catalog and Ordering handlers inject `DbContext` directly. Basket has `IBasketRepository`
purely because it needs a Redis caching layer:

```csharp
services.AddScoped<IBasketRepository, BasketRepository>();
services.Decorate<IBasketRepository, CachedBasketRepository>();   // Scrutor
```

`CachedBasketRepository` wraps the real one: read-through on `GetBasket`, write-through on
`CreateBasket`, evict on `DeleteBasket` and `SaveChangesAsync`.

**Do not add a repository by default.** EF's `DbSet` already is one. Add it when you have a
genuine cross-cutting concern to decorate, and note that `UpdateItemPriceInBasketHandler` in
this codebase deliberately bypasses the repository and uses `BasketDbContext` directly, with a
comment explaining why ("no cache is needed").

### Seeder

```csharp
public class CatalogDataSeeder(CatalogDbContext dbContext) : IDataSeeder
{
    public async Task SeedAllAsync()
    {
        if (!await dbContext.Products.AnyAsync())
        {
            await dbContext.Products.AddRangeAsync(InitialData.Products);
            await dbContext.SaveChangesAsync();
        }
    }
}
```

Registered as `IDataSeeder`, executed by `UseMigration<TContext>()`. Must be idempotent.

### Outbox processor

Only Basket has one. It exists so that "empty the basket" and "tell Ordering to create an
order" cannot diverge: both the removal and the outbox row are written in one transaction, and
a background service publishes the message afterwards. Detail in
[03-module-communication.md](03-module-communication.md#pattern-c2--integration-event-with-a-transactional-outbox).

---

## Public surface of a module

A module exposes exactly three things. Everything else is `internal` by intent, even where the
C# accessibility does not say so.

| Surface | Mechanism | Example |
|---|---|---|
| HTTP endpoints | `ICarterModule` | `POST /products` |
| A Contracts assembly | `<Module>.Contracts` | `Catalog.Contracts.Products.Features.GetProductById.GetProductByIdQuery` |
| Integration events | published to RabbitMQ | `ProductPriceChangedIntegrationEvent` |

**Nothing else may be touched from outside.** In particular: no `DbContext`, no entity, no
internal command, no repository, no handler class.
