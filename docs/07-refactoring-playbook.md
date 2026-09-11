# Refactoring playbook: porting an existing API into this shape

This is the document to hand an AI agent (together with
[04-adding-a-feature.md](04-adding-a-feature.md) and [05-conventions.md](05-conventions.md))
when the task is *"restructure this existing ASP.NET Core API as a modular monolith like the
reference project"*.

It is written as a procedure, not as advice.

---

## Before you start: is this the right target?

Refactor to this architecture when **all** of these hold:

- The codebase has (or should have) more than one distinct business capability.
- Different capabilities are changing at different rates, or by different people.
- The team is losing time to unintended coupling — a change in A breaks B.
- There is a plausible future where one capability becomes its own service.

Do **not** refactor to this when:

- The API is a single capability with fewer than ~15 endpoints. You will add ceremony and gain
  nothing. A clean layered project is the right answer.
- The domain is genuinely CRUD over a handful of tables. CQRS-with-MediatR buys nothing here.
- Boundaries are not yet understood. Splitting into the wrong modules is more expensive than not
  splitting: module boundaries are hard to move once migrations exist.
- The team cannot take on MediatR, Carter, MassTransit, FluentValidation and Mapster at once.

**If the boundaries are unclear, stop and do step 1 alone.** Report the proposed boundaries and
wait for a human to confirm before moving any code.

---

## Preconditions

Do not start moving code until these exist:

1. **A green build** on the current code.
2. **Characterisation tests on the existing HTTP surface** — at minimum one request/response
   test per endpoint, asserting status code and body shape. These are the contract that must
   survive. If they do not exist, write them first; they are the only thing that makes the
   refactor verifiable.
3. **A recorded inventory** of the current endpoints, their handlers, and the tables each
   touches (step 1 produces this).
4. **A branch.** This refactor is not incremental in small commits at the start; the skeleton
   lands in one piece.

---

## Step 1 — Find the module boundaries

**Split by business capability, never by technical layer.** `Products`, `Orders`, `Payments` are
modules. `Api`, `Services`, `Repositories`, `Domain` are not.

Procedure:

1. List every table and every aggregate root.
2. Cluster them by **who writes them**: a table is owned by exactly one cluster. If two
   candidate modules both write a table, either they are one module, or the table is actually two
   tables.
3. Check the clusters against the endpoint list. A cluster whose endpoints are all reads of
   another cluster's data is not a module — it is a query on that module.
4. Name each cluster after the capability, not the entity: `Ordering`, not `Orders` —
   though `Catalog`/`Products` shows both conventions are survivable.
5. For each pair of clusters, write down every place the current code crosses between them
   (a join, a shared service, a foreign key, a direct method call). **This list is the real
   work of the refactor.** Each crossing must become a Pattern A / B / C call from
   [03-module-communication.md](03-module-communication.md), or disappear.

Output a table before writing any code:

| Module | Aggregates | Tables owned | Endpoints | Crossings out |
|---|---|---|---|---|
| Catalog | Product | products | 6 | — |
| Basket | ShoppingCart, ShoppingCartItem | shopping_carts, shopping_cart_items, outbox | 6 | reads Product price (→ Pattern A); publishes checkout (→ C2) |
| Ordering | Order, OrderItem | orders, order_items | 4 | consumes checkout (→ C2) |

**Stop here and get confirmation if the clusters are not obvious.**

---

## Step 2 — Build the skeleton

Create the structure empty, and get it compiling, before moving a single line of business logic.

```
src/
├── <Solution>.sln
├── Bootstrapper/Api/          new empty web project
├── Modules/                   empty
├── Shared/
├── Shared.Contracts/
└── Shared.Messaging/          only if you need integration events
```

Copy the shared projects from the reference repo essentially verbatim — they are generic:

| From reference | Contains |
|---|---|
| `Shared.Contracts/CQRS/` | `ICommand`, `ICommandHandler`, `IQuery`, `IQueryHandler` |
| `Shared/DDD/` | `Entity<T>`, `Aggregate<T>`, `IDomainEvent` |
| `Shared/Behaviors/` | `ValidationBehavior`, `LoggingBehavior` *(apply fix 4 from [06](06-known-issues.md))* |
| `Shared/Data/Interceptors/` | `AuditableEntityInterceptor`, `DispatchDomainEventsInterceptor` |
| `Shared/Exceptions/` | `NotFoundException`, `BadRequestException`, `InternalServerException`, `CustomExceptionHandler` |
| `Shared/Extensions/` | `AddCarterWithAssemblies`, `AddMediatRWithAssemblies` |
| `Shared/Pagination/` | `PaginationRequest`, `PaginatedResult<T>` |
| `Shared.Messaging/` | `IntegrationEvent` *(apply fix 3)*, `AddMassTransitWithAssemblies` |

Then write `Program.cs` in the shape shown in
[01-architecture.md](01-architecture.md#the-composition-root), with zero modules registered.
**Build. It must be green before step 3.**

---

## Step 3 — Move one module at a time

**Order matters.** Do the module with the fewest outbound crossings first — usually the one
others read from (in the reference, `Catalog`). Finish it end to end, including its migration,
before starting the next. Never have two half-moved modules at once.

For each module:

### 3a. Create the project

```bash
dotnet new classlib -o src/Modules/<Module> -f net10.0
dotnet sln src/<Solution>.sln add src/Modules/<Module>/<Module>.csproj
```

Add references to `Shared`, `Shared.Messaging`, and a `ProjectReference` from `Api.csproj`.
Create `<Module>Module.cs` with the standard `Add`/`Use` pair.

### 3b. Move the domain

Move entities into `<Module>/<Aggregate>/Models/`. While moving, convert them from anaemic data
holders into aggregates:

```csharp
// BEFORE — public setters, no invariants
public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public decimal Price { get; set; }
}

// AFTER
public class Product : Aggregate<Guid>
{
    public string Name { get; private set; } = null!;
    public decimal Price { get; private set; }

    public static Product Create(Guid id, string name, decimal price)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);

        var product = new Product { Id = id, Name = name, Price = price };
        product.AddDomainEvent(new ProductCreatedEvent(product));
        return product;
    }

    public void Update(string name, decimal price)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        if (Price != price) { Price = price; AddDomainEvent(new ProductPriceChangedEvent(this)); }
    }
}
```

Every `entity.Property = value` assignment in the old service code is now a domain method. That
mapping — assignment cluster → named method — is the core of the domain conversion.

### 3c. Create the DbContext and take the schema

```csharp
public class <Module>DbContext(DbContextOptions<<Module>DbContext> options) : DbContext(options)
{
    public DbSet<Thing> Things => Set<Thing>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("<module>");
        builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(builder);
    }
}
```

Move the module's `IEntityTypeConfiguration` classes into `Data/Configurations/`. Delete the
module's entities from the old shared `DbContext`.

**On existing data:** the move from `public` to schema `<module>` is a real migration. Either
generate `ALTER TABLE ... SET SCHEMA` statements in the first migration, or keep the existing
schema name for the first cut and move it in a separate, dedicated migration. Do not silently
generate a drop-and-create.

### 3d. Carve slices out of the controller

One controller action becomes one `Features/<UseCase>/` folder. Work action by action.

```csharp
// BEFORE
[HttpPost]
public async Task<IActionResult> Create([FromBody] CreateProductDto dto)
{
    if (string.IsNullOrEmpty(dto.Name))
        return BadRequest("Name is required");

    var product = await _productService.CreateAsync(dto);
    return CreatedAtAction(nameof(GetById), new { id = product.Id }, product);
}
```

decomposes into exactly four pieces:

| Old | New | Goes to |
|---|---|---|
| `[HttpPost]` + route | `app.MapPost("/products", ...)` | `CreateProductEndpoint.cs` |
| `CreateProductDto` | `CreateProductRequest` + `CreateProductCommand` | Endpoint + Handler file |
| the `if (...) return BadRequest` | `CreateProductCommandValidator` rule | Handler file |
| `_productService.CreateAsync` body | `CreateProductHandler.Handle` | Handler file |

Follow the templates in [04-adding-a-feature.md](04-adding-a-feature.md) exactly. Delete the
service method once the last action using it is ported — do not leave both.

### 3e. Rewrite the crossings

Take the "crossings out" column from step 1 and convert each, using the decision tree in
[03-module-communication.md](03-module-communication.md#decision-tree):

| Old code | New |
|---|---|
| `_otherService.GetThing(id)` where the result is needed *now* | `sender.Send(new GetThingByIdQuery(id))`, query type in `<Other>.Contracts` |
| `_otherService.DoThing(...)` fire-and-forget | publish an integration event; the other module consumes it |
| a LINQ join across two modules' tables | denormalise: copy the needed fields into this module's table, keep fresh with an integration event |
| a shared entity written by both modules | the boundary is wrong — revisit step 1 |
| a shared `DbContext` transaction across modules | outbox (Pattern C2) |

**Denormalisation is the answer more often than people expect.** In the reference,
`ShoppingCartItem` stores `Price` and `ProductName` as its own columns instead of joining to
`products`.

### 3f. Register and verify

Add the three lines in `Program.cs`, generate the migration into the module, run the
characterisation tests. **All of them must still pass.** The HTTP contract does not change
during this refactor — if a response shape changed, that is a bug, not an improvement.

---

## Step 4 — Verify

Run, in order:

```bash
dotnet build src/<Solution>.sln                      # boundaries: must be 0 errors
dotnet test tests/ArchitectureTests                  # boundaries: rules from 05-conventions.md
dotnet test tests/<Module>.IntegrationTests          # behaviour: HTTP contract unchanged
```

Then check by hand:

- [ ] No module `.csproj` references another module's implementation project.
- [ ] Each module has exactly one `DbContext` with its own `HasDefaultSchema`.
- [ ] No `DbSet` of one module's entity appears in another module's context.
- [ ] Every `Features/<UseCase>/` folder has exactly two files.
- [ ] `Program.cs` contains no endpoint or handler registration.
- [ ] Every response shape is byte-identical to before the refactor.
- [ ] The old controllers, services and shared `DbContext` are **deleted**, not left orphaned.

---

## Translation reference

Applied when reading old code. This is the table to keep in front of you.

### Structure

| Existing pattern | Becomes |
|---|---|
| `Controllers/ProductsController.cs` | one `Features/<UseCase>/<UseCase>Endpoint.cs` per action |
| `Services/ProductService.cs` | one `<UseCase>Handler` per public method |
| `Repositories/ProductRepository.cs` | usually deleted — inject `DbContext` into the handler |
| `Repositories/` with caching/retry/audit logic | keep the interface, register with `services.Decorate<...>` (Scrutor) |
| `IUnitOfWork` | deleted — `DbContext.SaveChangesAsync` is the unit of work |
| `Models/`, `DTOs/`, `ViewModels/` | `Request`/`Response` in the endpoint file; `Command`/`Query`/`Result` in the handler file; cross-module DTOs in `<Module>.Contracts` |
| a single `AppDbContext` | one `<Module>DbContext` per module, each with `HasDefaultSchema` |
| `Startup.cs` / a large `Program.cs` | slim `Program.cs` + one `<Module>Module.cs` per module |
| `Extensions/ServiceCollectionExtensions.cs` | split: shared → `Shared`; module-specific → `<Module>Module.cs` |

### Code

| Existing pattern | Becomes |
|---|---|
| `[HttpGet("{id}")]` | `app.MapGet("/things/{id:Guid}", ...)` inside an `ICarterModule` |
| `[Required]`, `[Range]` data annotations | `AbstractValidator<TCommand>` rules |
| `if (x == null) return NotFound()` | `throw new ThingNotFoundException(id)` — mapped by `CustomExceptionHandler` |
| `try/catch` returning `BadRequest`/`500` | delete; throw typed exceptions instead |
| `_mapper.Map<TDto>(entity)` (AutoMapper) | `entity.Adapt<TDto>()` (Mapster) — no profile needed for matching names |
| AutoMapper `Profile` classes | delete; add a Mapster `TypeAdapterConfig` only where names do not match |
| `_logger.LogInformation($"...{id}")` | `_logger.LogInformation("...{Id}", id)` |
| middleware doing auth/validation/logging per request | MediatR `IPipelineBehavior` in `Shared/Behaviors/` |
| `entity.Status = "Shipped"` in a service | `order.MarkShipped()` on the aggregate |
| `_context.SaveChanges()` then `_bus.Publish(...)` | outbox row + `SaveChangesAsync` in one transaction (Pattern C2) |
| an `IHostedService` polling a table | keep it, move into `<Module>/Data/Processors/` |
| `services.AddScoped<IFooService, FooService>()` in `Startup` | move into `Add<Module>Module`, under "Application use case services" |

### Anti-patterns to actively remove during the move

| Found | Do |
|---|---|
| A handler injecting two modules' `DbContext`s | split the use case, or make one side a Pattern A query |
| An entity with all-public setters | convert to a factory + domain methods |
| A "Manager"/"Helper"/"Util" class holding business rules | move the rules onto the aggregate |
| A DTO used as both the HTTP body and the EF entity | split into `Request` + entity |
| Business logic in a controller | move to the handler; endpoint keeps only map→send→map→return |
| A `catch (Exception)` that returns a status code | delete; use typed exceptions |

---

## Guardrails for an AI doing this work

State these explicitly in the task prompt:

1. **Do not change the HTTP contract.** Routes, status codes, and response shapes stay
   identical. If the old API returned a bare array, the refactor still returns a bare array.
2. **One module per pull request.** Never move two modules in one change.
3. **Build after every slice.** A red build is the signal to stop and fix, not to continue.
4. **Never add a project reference between two module implementation assemblies.** If a slice
   seems to need one, stop and report — the boundary is wrong, or the crossing needs a pattern
   from [03-module-communication.md](03-module-communication.md).
5. **Do not invent business rules.** If behaviour is unclear, port it verbatim and flag it. A
   refactor that "improves" undocumented behaviour is a bug with extra steps.
6. **Do not delete tests to make them pass.**
7. **Report, do not resolve, ambiguous boundaries.** Wrong module boundaries are the one mistake
   that is expensive to undo.
8. **Copy the reference structure, not the reference bugs.** Read
   [06-known-issues.md](06-known-issues.md) first; the items there are marked "do not copy".

---

## A prompt skeleton

```
Refactor the API in <path> into a modular monolith following the reference architecture
documented in <this repo>/docs/.

Read first, in this order:
  docs/01-architecture.md        the target structure
  docs/02-module-anatomy.md      what one module contains
  docs/03-module-communication.md  how modules may talk
  docs/04-adding-a-feature.md    the exact file templates
  docs/05-conventions.md         the rules
  docs/06-known-issues.md        what NOT to copy from the reference
  docs/07-refactoring-playbook.md  the procedure to follow

Follow docs/07-refactoring-playbook.md step by step.

Step 1 only for now: produce the module boundary table and the list of crossings.
Do not move any code. Stop and wait for my confirmation of the boundaries.

Constraints:
  - The HTTP contract must not change.
  - `dotnet build` must be green after every slice.
  - No project reference between two module implementation assemblies.
  - Port unclear behaviour verbatim and flag it; do not redesign it.
```

Then, once boundaries are agreed, one message per module:

```
Boundaries confirmed. Execute step 3 for the <Module> module only.
Finish it end to end — project, domain, DbContext, migration, all slices, crossings,
registration — then run step 4 and report the checklist.
Do not start any other module.
```
