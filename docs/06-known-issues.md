# Known issues — do not copy these

This codebase is a course project. The **structure** is sound; several **implementations**
inside it are not. If you use this repo as a refactoring template, fix these first — otherwise
every API you refactor inherits them.

Ordered by severity. Each entry: what it is, why it matters, how to fix it.

---

## Severity: high

### 1. Authentication is wired but not enforced

`Program.cs` registers Keycloak and calls `UseAuthentication()`/`UseAuthorization()`, but
`.RequireAuthorization()` is **commented out on every endpoint** (commit `e98a1dd`,
"No auth for testing"), and no Catalog or Ordering endpoint ever had it:

```csharp
// src/Modules/Basket/Basket/Features/GetBasket/GetBasketEndpoint.cs:33
// .RequireAuthorization();
```

The audit interceptor compounds it:

```csharp
// src/Shared/Data/Interceptors/AuditableEntityInterceptor.cs:31
entry.Entity.CreatedBy = "me"; // TODO
```

**Why it matters:** the API is fully open while *looking* secured. A template that ships this
produces unauthenticated APIs by default — the worst kind of failure, because the auth
machinery being present suppresses the question.

**Fix:** uncomment `.RequireAuthorization()`; better, make it the default with
`app.MapCarter().RequireAuthorization()` and opt *out* per endpoint with `.AllowAnonymous()`.
Replace `"me"` with the current principal via `IHttpContextAccessor` or a small
`ICurrentUser` abstraction in `Shared`.

### 2. Domain events are dispatched before commit

`DispatchDomainEventsInterceptor` overrides `SavingChangesAsync`, so handlers run **inside** the
still-open transaction:

```csharp
// src/Shared/Data/Interceptors/DispatchDomainEventsInterceptor.cs
public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(...)
{
    await DispatchDomainEvents(eventData.Context);     // ← before the write completes
    return await base.SavingChangesAsync(...);
}
```

Combined with issue 6, this means Catalog can publish `ProductPriceChangedIntegrationEvent` to
RabbitMQ for a price change that then fails to commit. Basket updates its cached prices to a
value that does not exist in Catalog.

**Why it matters:** silent cross-module data divergence, unreproducible in testing.

**Fix:** two options.
- *Simple:* move dispatch to `SavedChangesAsync` — handlers then run after a successful commit,
  at the cost of losing "same transaction" semantics for in-module handlers.
- *Correct:* keep `SavingChanges` for purely in-module handlers, and route everything that
  leaves the module through the outbox (issue 6).

### 3. Event ids are regenerated on every read

```csharp
// src/Shared.Messaging/Events/IntegrationEvent.cs:5-6
public Guid EventId => Guid.NewGuid();       // ← new value EVERY access
public DateTime OccuredOn => DateTime.Now;   // ← also: not UTC
```

```csharp
// src/Shared/DDD/IDomainEvent.cs:7-8
Guid EventId => Guid.NewGuid();
public DateTime OccurredOn => DateTime.Now;
```

These are expression-bodied get-only properties, not initialised fields. Reading `EventId` twice
gives two different GUIDs; serialising and deserialising gives a third.

**Why it matters:** the outbox delivers **at-least-once**, so consumers must deduplicate — and
the only sane dedupe key is the event id. With this bug, deduplication is impossible. It quietly
removes the safety net the outbox exists to provide.

**Fix:**

```csharp
public record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredOn { get; init; } = DateTime.UtcNow;
    public string EventType => GetType().AssemblyQualifiedName!;
}
```

Note also the misspelling `OccuredOn` (one `r`) in `IntegrationEvent` and `OutboxMessage`, vs
the correct `OccurredOn` in `IDomainEvent`. Pick one spelling before this becomes a wire format.

### 4. LoggingBehavior writes card data to the log

```csharp
// src/Shared/Behaviors/LoggingBehavior.cs:15
logger.LogInformation("[START] Handle request={Request} - Response={Response} - RequestData={RequestData}",
    typeof(TRequest).Name, typeof(TResponse).Name, request);
```

`request` is serialised in full. `CheckoutBasketCommand` wraps `BasketCheckoutDto`, which
carries `CardName`, `CardNumber`, `Expiration`, `Cvv`.

**Why it matters:** full PAN and CVV land in Seq in plaintext on every checkout. That is a
PCI-DSS violation and a GDPR problem, and it is inherited by every API refactored from this
template.

**Fix:** log the request *type* only, or introduce an opt-in marker (`ILoggableRequest`) or
`[SensitiveData]` property attributes honoured by a Serilog destructuring policy. The cheapest
correct change is to drop `RequestData` from the message template.

---

## Severity: medium

### 5. No tests, and no architecture tests

There is no test project of any kind. For the boundary rules in
[05-conventions.md](05-conventions.md) nothing is enforced — a `ProjectReference` from
`Basket.csproj` to `Catalog.csproj` compiles happily.

**Why it matters for AI refactoring specifically:** architecture tests are the AI's feedback
loop. Without them, "did the refactor preserve the boundaries?" is a question only a human code
review can answer, which does not scale.

**Fix:** add `tests/ArchitectureTests` using NetArchTest.Rules:

```csharp
[Fact]
public void Modules_should_not_reference_each_other()
{
    var moduleAssemblies = new[] { typeof(CatalogModule).Assembly,
                                   typeof(BasketModule).Assembly,
                                   typeof(OrderingModule).Assembly };

    foreach (var assembly in moduleAssemblies)
    {
        var otherModuleNamespaces = moduleAssemblies
            .Where(a => a != assembly)
            .Select(a => a.GetName().Name!)
            .ToArray();

        var result = Types.InAssembly(assembly)
            .Should().NotHaveDependencyOnAny(otherModuleNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            $"{assembly.GetName().Name} must not depend on another module. " +
            $"Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
```

Then add: contracts assemblies must not depend on EF Core; handlers must not reference another
module's `DbContext`; every `ICommand` must have a validator; every `ICarterModule` must live
under a `Features` namespace. Add integration tests with Testcontainers +
`WebApplicationFactory` per module.

### 6. Catalog publishes integration events without an outbox

Basket has a full transactional outbox. Catalog publishes straight to the bus from a domain
event handler:

```csharp
// src/Modules/Catalog/Products/EventHandlers/ProductPriceChangedEventHandler.cs
await bus.Publish(integrationEvent, cancellationToken);
```

Combined with issue 2, this is publish-before-commit with no recovery. Two modules, two
different reliability levels, no documented reason.

**Fix:** give every module publishing integration events the same outbox. Promote
`OutboxMessage` + `OutboxProcessor` into `Shared` (parameterised by `DbContext`) rather than
copying Basket's implementation three times.

### 7. Integration event contracts live in a shared assembly

`Shared.Messaging/Events/` holds `ProductPriceChangedIntegrationEvent` (owned by Catalog) and
`BasketCheckoutIntegrationEvent` (owned by Basket). Every module references `Shared.Messaging`,
so **every module compiles against every other module's integration contracts**.

This contradicts the pattern the project already uses correctly for synchronous calls: Catalog's
query contract lives in `Catalog.Contracts`, referenced only by consumers who need it.

**Why it matters:** changing Basket's checkout event forces a recompile of Catalog and Ordering,
which have nothing to do with it. It also makes the contract's owner ambiguous.

**Fix:** move each integration event into its publisher's contracts assembly —
`Catalog.Contracts.IntegrationEvents.ProductPriceChangedIntegrationEvent`,
`Basket.Contracts.IntegrationEvents.BasketCheckoutIntegrationEvent`. Leave only the
`IntegrationEvent` base record and the MassTransit registration in `Shared.Messaging`. Create the
missing `Basket.Contracts` and `Ordering.Contracts` projects.

### 8. `Shared` is a fat shared kernel

`Shared.csproj` references Carter, FluentValidation, Mapster, EF Core, EF Tools, Npgsql, Redis,
Scrutor and `Microsoft.AspNetCore.Http.Abstractions`. Every module inherits all of it
transitively.

**Why it matters:** a module cannot choose a different store, a different mapper, or no web
framework at all. The shared kernel dictates the entire technology stack of every module —
exactly the coupling modularity is supposed to remove.

**Fix:** split into
`Shared.Abstractions` (DDD base types, no package references) ·
`Shared.Web` (Carter, exception handler, pagination) ·
`Shared.Persistence.Ef` (interceptors, migration extensions, Npgsql) ·
`Shared.Application` (behaviors, FluentValidation, Mapster).
Each module then references only what it uses.

### 9. `CheckoutBasketHandler` swallows every exception

```csharp
// src/Modules/Basket/Basket/Features/CheckoutBasket/CheckoutBasketHandler.cs:69-73
catch
{
    await transaction.RollbackAsync(cancellationToken);
    return new CheckoutBasketResult(false);
}
```

A missing basket throws `BasketNotFoundException`, which should surface as 404. Instead the
catch-all converts it to `200 OK { "isSuccess": false }`. Database failures, serialisation
failures and cancellations are hidden the same way.

**Why it matters:** it defeats `CustomExceptionHandler`, produces wrong status codes, and
discards diagnostics. It also directly contradicts convention 8.2/8.3.

**Fix:** delete the `try/catch`. `await using var transaction` rolls back on dispose if
`CommitAsync` was never reached. Let the exception reach `CustomExceptionHandler`.

### 10. `BasketCheckoutIntegrationEvent` carries no line items

```csharp
// src/Modules/Ordering/Orders/EventHandlers/BasketCheckoutIntegrationEventHandler.cs:38-39
new OrderItemDto(orderId, new Guid("5334c996-8457-4cf0-815c-ed2b77c4ff61"), 2, 500),
new OrderItemDto(orderId, new Guid("c67d6323-e8b1-4bdf-9a75-b0d0d2e7e914"), 1, 400)
```

The event has `TotalPrice` but no items, so Ordering invents two hardcoded products. Every order
in the system is fabricated.

**Why it matters:** the flagship cross-module flow does not actually work. Anyone treating this
as a reference will copy the shape of an integration event that omits the data its consumer
needs.

**Fix:** add `List<BasketCheckoutItem> Items` to the event, populate it in
`CheckoutBasketHandler` from `basket.Items`, and map it in the consumer.

### 11. The outbox processor is fragile

`src/Modules/Basket/Data/Processors/OutboxProcessor.cs`:

| Problem | Consequence |
|---|---|
| `Type.GetType(message.Type)` on an `AssemblyQualifiedName` | Renaming or versioning the assembly orphans unprocessed messages; the outbox cannot be read by an extracted service. |
| No `Take(n)` | A backlog loads entirely into memory. |
| `SaveChangesAsync` only after the whole loop | A crash mid-batch republishes everything already sent in that batch. |
| No retry count, no dead-letter | A permanently unpublishable message blocks and re-logs forever. |
| Fixed 10s `Task.Delay` | Up to 10s latency on every checkout, and constant polling when idle. |
| `Where(m => m.ProcessedOn == null)` with no locking | Two app instances publish every message twice. |

**Fix:** store a stable logical event name and map it to a type; `OrderBy(OccurredOn).Take(50)`;
save after each message; add `RetryCount` + `Error` columns; use
`FOR UPDATE SKIP LOCKED` (Npgsql supports it) for multi-instance safety. Or adopt MassTransit's
built-in EF outbox and delete this class.

---

## Severity: low

### 12. `BasketRepository.GetBasket` ignores `asNoTracking`

```csharp
// src/Modules/Basket/Data/Repository/BasketRepository.cs:15-18
if (asNoTracking)
{
    query.AsNoTracking();     // ← return value discarded; this is a no-op
}
```

`AsNoTracking()` returns a *new* `IQueryable`. **Fix:** `query = query.AsNoTracking();`

### 13. Seeders run once per module

`UseMigration<TContext>()` calls `SeedDataAsync`, which resolves **all** registered
`IDataSeeder`s. With three modules calling `UseMigration`, every seeder runs three times. It is
harmless here only because `CatalogDataSeeder` guards with `AnyAsync()`.

**Fix:** separate seeding from migration — `app.UseMigration<T>()` then a single
`app.SeedData()` in `Program.cs`, or resolve only `IDataSeeder<TContext>`.

### 14. Startup blocks on async work

```csharp
// src/Shared/Data/Extensions.cs:12-14
MigrateDatabaseAsync<TContext>(app.ApplicationServices).GetAwaiter().GetResult();
SeedDataAsync(app.ApplicationServices).GetAwaiter().GetResult();
```

Sync-over-async. Tolerable at startup, but it is the pattern people copy into request paths.
Also: auto-migrating on startup is a development convenience that should not reach production
(concurrent instances race; no rollback).

**Fix:** an `IHostedService` that migrates before the server starts accepting traffic, and gate
it on the environment.

### 15. `CreateOrderHandler` fudges the unique index

```csharp
// src/Modules/Ordering/Orders/Features/CreateOrder/CreateOrderHandler.cs:60-62
id: Guid.NewGuid(),                                        // ignores orderDto.Id
orderName: $"{orderDto.OrderName}_{new Random().Next()}",  // dodges the unique index
```

`OrderName` has a unique index, so a random suffix is appended to avoid collisions — and
`new Random()` per call is a known correctness trap (identical seeds under rapid calls on older
runtimes). **Fix:** decide whether `OrderName` is a business identifier; if so, generate it
properly and handle collisions explicitly.

### 16. No OpenAPI / Swagger

Every endpoint carries `.WithName`, `.Produces<T>`, `.WithSummary`, `.WithDescription` — and
`Program.cs` never calls `AddOpenApi()`/`MapOpenApi()`. The metadata is inert.

**Fix:** `builder.Services.AddOpenApi();` and `app.MapOpenApi();`. For a template, the generated
contract is a major payoff for metadata that is already being written.

### 17. Nullable warnings in the JSON converters

`ShoppingCartConverter.cs:19` and `ShoppingCartItemConverter.cs:22` produce `CS8604` — values
read from JSON are passed to non-nullable parameters unchecked. Malformed cached JSON throws
`ArgumentNullException` from deep inside deserialisation.

Also note `ShoppingCartConverter` uses **reflection** to set the private `_items` field. That is
the cost of caching a domain aggregate directly; caching a DTO instead would remove it.

**Fix:** validate with `GetProperty(...).GetString() ?? throw new JsonException(...)`. Consider
enabling `<TreatWarningsAsErrors>` for new code.

### 18. Known vulnerable transitive package

`dotnet build` reports `NU1903` — `System.Security.Cryptography.Xml` 9.0.0 has known high
severity advisories, pulled in transitively through `Shared`.

**Fix:** add an explicit `PackageReference` to a patched version, or update the package that
pulls it in. Add `dotnet list package --vulnerable --include-transitive` to CI.

### 19. Cosmetic inconsistencies

| Issue | Detail |
|---|---|
| `Basket.Basket.*` namespaces | The module folder and aggregate folder are both `Basket`. Use a plural aggregate name in new modules. |
| `GlobalUsings.cs` only in Catalog and Api | Basket and Ordering repeat the same usings in every file. |
| `Ordering.csproj` has an empty `<Folder Include="Data\Migrations\" />` | Left over from scaffolding; migrations exist now. |
| `.github/workflows/` is empty | No CI at all. |
| Only Catalog has a Contracts project | See issue 7. |
| Docker Compose has no app service | Infrastructure only; the API runs from the IDE. Fine for local dev, but the `Dockerfile` is never exercised. |

---

## Fix order

If you are preparing this repo to be the reference template:

**Before using it at all** — 1 (auth), 4 (PII logging), 3 (event ids), 9 (swallowed exceptions).
These are the ones that become a company-wide problem the moment they are replicated.

**Before the first AI-driven refactor** — 5 (architecture tests + integration tests). This is the
verification loop; without it you cannot tell a good refactor from a bad one.

**Before the second module is refactored** — 7 (publisher-owned contracts), 8 (split `Shared`),
2 + 6 (outbox everywhere). These are architectural and get more expensive the more code is
already in the shape.

**Whenever** — everything else.
