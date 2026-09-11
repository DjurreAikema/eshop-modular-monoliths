# Conventions

Rules, ordered so they can be checked mechanically. Each is followed in the existing code unless
marked otherwise.

## 1. Boundaries

| # | Rule |
|---|---|
| 1.1 | A module assembly must never reference another module's **implementation** assembly. Only `<Other>.Contracts`. |
| 1.2 | A module may reference `Shared`, `Shared.Contracts`, `Shared.Messaging` and any `*.Contracts`. |
| 1.3 | `<Module>.Contracts` may reference **only** `Shared.Contracts`. No EF, no Carter, no MassTransit, no implementation assembly. |
| 1.4 | Only `Bootstrapper/Api` may reference module implementation assemblies. |
| 1.5 | A handler may inject only its own module's `DbContext`. |
| 1.6 | No foreign keys, joins or queries across schemas. |
| 1.7 | Entities never leave a module. Contracts expose DTO records only. |

## 2. Projects and namespaces

| # | Rule |
|---|---|
| 2.1 | One module = one project at `src/Modules/<Module>/`, assembly and root namespace `<Module>`. |
| 2.2 | Namespaces mirror folders: `Catalog.Products.Features.CreateProduct`. |
| 2.3 | File-scoped namespace declarations (`namespace X;`), never braces. |
| 2.4 | Contracts namespaces mirror the owner's: `Catalog.Contracts.Products.Features.GetProductById`. |
| 2.5 | Prefer a plural aggregate folder (`Products`, `Orders`). Basket uses the singular `Basket`, creating `Basket.Basket.*` — do not copy that. |

## 3. File layout

| # | Rule |
|---|---|
| 3.1 | One use case = one folder = two files: `<UseCase>Endpoint.cs`, `<UseCase>Handler.cs`. |
| 3.2 | Multiple types per file are expected and correct — the slice is the unit, not the type. |
| 3.3 | Use the section comments `// --- Records`, `// --- Validation`, `// --- Handler`, `// --- Endpoint` in that order. |
| 3.4 | When a record moves to a Contracts assembly, leave a breadcrumb: `// --- Records: In Catalog.Contracts`. |
| 3.5 | One entity configuration per file in `Data/Configurations/`. |
| 3.6 | `GlobalUsings.cs` at module root, for namespaces used by nearly every file. Present in Catalog and Api only — add to new modules for consistency. |

## 4. Naming

Derived mechanically from the use case name (PascalCase verb phrase):

```
CreateProduct →  CreateProductRequest    CreateProductCommand     CreateProductCommandValidator
                 CreateProductResponse   CreateProductResult      CreateProductHandler
                 CreateProductEndpoint
```

| # | Rule |
|---|---|
| 4.1 | Writes are `<UseCase>Command`; reads are `<UseCase>Query`. |
| 4.2 | Handler is always `<UseCase>Handler` — no `CommandHandler`/`QueryHandler` suffix. |
| 4.3 | Domain events are past tense: `ProductCreatedEvent`, `ProductPriceChangedEvent`. |
| 4.4 | Integration events are past tense plus the suffix: `ProductPriceChangedIntegrationEvent`. |
| 4.5 | Event handlers: `<Event>Handler` — `ProductCreatedEventHandler`, `ProductPriceChangedIntegrationEventHandler`. |
| 4.6 | Exceptions: `<Thing>NotFoundException`, deriving from the matching `Shared.Exceptions` base. |
| 4.7 | DbContext: `<Module>DbContext`. Schema: the module name, lowercase. |
| 4.8 | Registration: `Add<Module>Module` / `Use<Module>Module` in `static class <Module>Module`. |

## 5. C# style

| # | Rule |
|---|---|
| 5.1 | Primary constructors for DI: `public class XHandler(XDbContext dbContext) : ...`. |
| 5.2 | `record` for every message, DTO and event. `class` for entities and aggregates. |
| 5.3 | Nullable reference types on; `= null!` for EF-populated required properties. |
| 5.4 | Collection expressions: `= []`, not `= new List<T>()`. |
| 5.5 | Target .NET 10, `ImplicitUsings` on. |
| 5.6 | Async all the way; `CancellationToken` is the last parameter and is always passed on. |
| 5.7 | `ArgumentException.ThrowIfNullOrEmpty` / `ArgumentOutOfRangeException.ThrowIfNegativeOrZero` for guard clauses. |

## 6. Domain model

| # | Rule |
|---|---|
| 6.1 | Aggregate roots derive from `Aggregate<TId>`; child entities from `Entity<TId>`. |
| 6.2 | All property setters are `private` (or `internal` where a sibling entity must write). |
| 6.3 | Construction goes through a static factory: `Create(...)` for entities, `Of(...)` for value objects. |
| 6.4 | Invariants are enforced in the factory and in mutators — never in a handler. |
| 6.5 | Collections: private `List<T> _items`, public `IReadOnlyList<T> Items => _items.AsReadOnly()`. |
| 6.6 | Domain events are raised by the aggregate, inside the method that makes the change. |
| 6.7 | Value objects need a `protected` parameterless constructor for EF, and are mapped with `ComplexProperty`. |

## 7. Endpoints

| # | Rule |
|---|---|
| 7.1 | `public class <UseCase>Endpoint : ICarterModule` with `AddRoutes`. Never registered by hand. |
| 7.2 | Body is exactly: `Adapt` to the message → `sender.Send` → `Adapt` to the response → `Results.*`. |
| 7.3 | No business logic, no data access, no `try/catch`. |
| 7.4 | Always attach `.WithName`, `.Produces<T>`, `.ProducesProblem`, `.WithSummary`, `.WithDescription`. |
| 7.5 | Routes are lowercase plural nouns: `/products`, `/basket/{userName}/items`. |
| 7.6 | Route constraints where they apply: `/products/{id:Guid}`. |
| 7.7 | `Results.Created` for creation, `Results.Ok` otherwise. |
| 7.8 | Protected endpoints end with `.RequireAuthorization()`. **Currently commented out everywhere** — see [06-known-issues.md](06-known-issues.md#1-authentication-is-wired-but-not-enforced). |

## 8. Handlers

| # | Rule |
|---|---|
| 8.1 | Implement `ICommandHandler<TCommand, TResult>` or `IQueryHandler<TQuery, TResult>` from `Shared.Contracts.CQRS`, never MediatR's `IRequestHandler` directly. |
| 8.2 | Throw typed exceptions for error paths. Never catch to shape an HTTP response. |
| 8.3 | Never `try/catch` broadly — it defeats `CustomExceptionHandler`. |
| 8.4 | Reads: `AsNoTracking()`, plus an explicit `OrderBy` whenever paging. |
| 8.5 | A handler orchestrates: load, call domain methods, save. Rules belong in the domain. |
| 8.6 | Map with Mapster `Adapt<T>()`. |

## 9. Validation

| # | Rule |
|---|---|
| 9.1 | One `AbstractValidator<TCommand>` per command, in the handler file. |
| 9.2 | `ValidationBehavior` only runs for `ICommand<T>` — **queries are not validated**. Validate query inputs with route constraints or in the handler. |
| 9.3 | Every rule gets `.WithMessage(...)`. |
| 9.4 | Validation covers shape/format. Business rules that need data go in the domain or the handler. |

## 10. Persistence

| # | Rule |
|---|---|
| 10.1 | `builder.HasDefaultSchema("<module>")` in `OnModelCreating`. |
| 10.2 | `builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly())` — never a hand-written list, never another assembly. |
| 10.3 | `DbSet` as an expression-bodied property: `public DbSet<Product> Products => Set<Product>();`. |
| 10.4 | Migrations live in the module, at `Data/Migrations/`. |
| 10.5 | Apply migrations via `app.UseMigration<TContext>()` in `Use<Module>Module`. |
| 10.6 | Seeders implement `IDataSeeder` and must be idempotent. |
| 10.7 | Explicit lengths and `IsRequired()` in entity configurations; do not rely on defaults. |
| 10.8 | Add a repository only to host a decorator (see `IBasketRepository` + Redis). Otherwise inject `DbContext`. |

## 11. Cross-cutting

| # | Rule |
|---|---|
| 11.1 | New cross-cutting concerns become a MediatR `IPipelineBehavior` in `Shared/Behaviors/`, not code repeated in handlers. |
| 11.2 | New exception types are mapped in `CustomExceptionHandler`. |
| 11.3 | Anything registered once for all modules goes in `Program.cs`; anything module-specific goes in `<Module>Module.cs`. |
| 11.4 | Structured logging with named placeholders — `logger.LogInformation("... {Id}", id)` — never interpolation. |
| 11.5 | Never log a whole request/command object; commands can carry PII. `LoggingBehavior` currently violates this — see [06-known-issues.md](06-known-issues.md#4-loggingbehavior-writes-card-data-to-the-log). |

## 12. Configuration

| # | Rule |
|---|---|
| 12.1 | All configuration lives in `Bootstrapper/Api/appsettings*.json`. Modules read it through the `IConfiguration` passed to `Add<Module>Module`. |
| 12.2 | Modules share `ConnectionStrings:Database` and separate by schema. To split a module's storage, give it its own connection string key. |
| 12.3 | Secrets go in user secrets or environment variables, never in `appsettings.json`. |

---

## Quick reference: where does X go?

| I am adding... | It goes in |
|---|---|
| An endpoint + use case | `Modules/<M>/<Agg>/Features/<UseCase>/` (two files) |
| A domain rule | the aggregate, in `<Agg>/Models/` |
| A validation rule | the validator in the handler file |
| An exception type | `<Agg>/Exceptions/`, plus a case in `CustomExceptionHandler` |
| A DTO used only inside the module | `<Agg>/Dtos/` |
| A DTO another module needs | `<Module>.Contracts/<Agg>/Dtos/` |
| A query another module calls | `<Module>.Contracts/<Agg>/Features/<UseCase>/`, handler stays in the module |
| An in-module reaction | domain event in `<Agg>/Events/` + handler in `<Agg>/EventHandlers/` |
| A cross-module notification | integration event in `Shared.Messaging/Events/` + `IConsumer` in the consuming module |
| A module-wide service registration | `<Module>Module.cs`, under the matching comment section |
| An app-wide concern | `Shared/` + a call in `Program.cs` |
| A DB schema change | a migration in `Modules/<M>/Data/Migrations/` |
