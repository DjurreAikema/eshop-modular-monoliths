# Assessment: is this structure any good?

**Short answer: the skeleton is genuinely good; the flesh is course-grade.**

The *structural* decisions are the ones you would actually make in a production modular
monolith. The *implementation* inside those structures contains shortcuts and a handful of
real bugs that you do not want replicated across dozens of refactored APIs.

Verdict per use:

| Use | Verdict |
|---|---|
| As an architecture to learn from | **Good** |
| As a shape to standardise your APIs on | **Good, after the fixes in [06-known-issues.md](06-known-issues.md)** |
| As a template an AI copies verbatim | **Not yet** — it will copy the bugs too |
| As a *target shape* for AI-driven refactoring | **Unusually well suited** — see below |

---

## What it gets right

### 1. Boundaries are compile-time, not conventional

Each module is its own assembly. The project reference graph *is* the boundary:

```
Api  ──►  Basket   ──►  Catalog.Contracts
     ──►  Catalog  ──►  Catalog.Contracts
     ──►  Ordering

      all modules ──►  Shared, Shared.Messaging  ──►  Shared.Contracts
```

`Basket` cannot see `Catalog`'s `CatalogDbContext`, its `Product` entity, or its handlers — the
types are simply not in scope. This is the single most important property, and most
"modular monolith" repos get it wrong by putting modules in folders of one project, where the
boundary is a naming convention the compiler ignores.

### 2. Data isolation with an exit path

Each module owns a `DbContext`, a Postgres schema (`catalog`, `basket`, `ordering`) and its own
EF migration history. No module queries another module's tables. Extracting a module into its
own service becomes a deployment change rather than a rewrite — you point its connection string
elsewhere and its schema travels with it.

The pragmatic compromise (one physical database, three schemas) is the right call for a
monolith, and it is reversible.

### 3. Vertical slices, not layers

A use case is a folder with two files:

```
Products/Features/CreateProduct/
  CreateProductEndpoint.cs   → HTTP shape: request/response records + route
  CreateProductHandler.cs    → Command + Validator + Handler
```

Everything a feature needs is co-located. Adding a feature touches only new files; deleting a
feature is one folder deletion. Contrast with layered architectures, where one use case is
smeared across `Controllers/`, `Services/`, `Repositories/`, `DTOs/` and `Validators/`.

### 4. No central registration file

Carter discovers `ICarterModule` endpoints by assembly scan; MediatR discovers handlers and
validators by assembly scan; MassTransit discovers consumers by assembly scan. Adding a feature
requires **zero edits** to `Program.cs`. Only adding a whole *module* touches the bootstrapper,
and that is three lines.

### 5. Three communication mechanisms, correctly distinguished

The project demonstrates all three and uses each where it belongs — see
[03-module-communication.md](03-module-communication.md):

- **Sync, in-process** — `Basket` sends `GetProductByIdQuery` (a type owned by
  `Catalog.Contracts`) through MediatR; `Catalog` handles it. Needed because you cannot add a
  basket item without the current price.
- **Async, in-process** — domain events such as `ProductPriceChangedEvent` via MediatR
  notifications, dispatched by an EF `SaveChanges` interceptor.
- **Async, cross-module** — integration events over RabbitMQ/MassTransit
  (`ProductPriceChangedIntegrationEvent`, `BasketCheckoutIntegrationEvent`), with a
  transactional **outbox** on the Basket side.

### 6. Cross-cutting concerns live in the pipeline

Validation and logging are MediatR pipeline behaviors, not repeated in handlers. Exception to
HTTP mapping is one `IExceptionHandler`. Auditing is an EF interceptor. Caching is a Scrutor
decorator over `IBasketRepository`. Handlers stay about the use case.

---

## What it gets wrong

Detail and fixes in [06-known-issues.md](06-known-issues.md). Summary:

**Architectural**

1. **Boundaries are never verified.** Nothing prevents someone adding a project reference from
   `Basket.csproj` to `Catalog.csproj`. There are no architecture tests. For a reference
   architecture this is the biggest gap — and for AI-driven refactoring it is critical, because
   it is the only automated feedback signal the AI has.
2. **Zero tests.** No unit, integration or architecture tests exist. "Refactor to this shape"
   is unverifiable without them.
3. **Integration-event contracts live in a shared assembly.** `Shared.Messaging.Events` holds
   *every* module's integration events, so every module compiles against every other module's
   contracts. This contradicts the pattern the project already applies correctly for sync calls
   (`Catalog.Contracts`). Contracts should be publisher-owned.
4. **`Shared` is a fat shared kernel.** It transitively forces Carter, EF Core, Npgsql, Redis,
   Mapster, FluentValidation and Scrutor onto every module. A module cannot choose a different
   store. It should be split into `Shared.Abstractions` / `Shared.Web` / `Shared.Persistence`.
5. **Only Catalog has a Contracts assembly.** The "every module publishes contracts" rule is
   aspirational rather than applied.

**Correctness bugs a template would propagate**

6. Catalog publishes its integration event from a domain-event handler that runs **before
   commit** — a failed commit still tells Basket about a price that was never saved. Catalog has
   no outbox; only Basket does.
7. `IntegrationEvent.EventId` and `IDomainEvent.EventId` are expression-bodied
   `=> Guid.NewGuid()`, producing a *new* id on every read. Event identity is therefore useless,
   which kills any consumer-side idempotency built on it.
8. `LoggingBehavior` logs the entire request object, so `CheckoutBasketCommand` writes
   **card number and CVV in plaintext to Seq**.
9. `CheckoutBasketHandler` swallows all exceptions and returns `IsSuccess: false`, turning what
   should be a 404 into a 200.
10. `BasketRepository.GetBasket` calls `query.AsNoTracking()` and discards the result, so the
    `asNoTracking` parameter does nothing.
11. Authentication is registered but `.RequireAuthorization()` is commented out on every
    endpoint, and the audit interceptor hardcodes `CreatedBy = "me"`.
12. `BasketCheckoutIntegrationEvent` carries no line items, so `Ordering` builds orders from
    **hardcoded product GUIDs**. The checkout flow is not actually complete.

---

## Why it is a strong target for AI refactoring

The properties that make an architecture easy for an LLM to refactor *into* are not quite the
same as the ones that make it pleasant for humans. This structure scores well on both.

| Property | Why it helps an AI |
|---|---|
| **One use case = one folder = two files** | Small, bounded unit of work. One controller action ports at a time, with a blast radius of two new files. |
| **Highly repetitive shape** | Every slice is the same Endpoint/Command/Validator/Handler quartet. Models reproduce regular structure far more reliably than they invent bespoke structure. |
| **No central registry to edit** | Assembly scanning means N parallel refactors never contend on `Program.cs` — removing the most common failure mode of bulk AI refactoring. |
| **Compile-time boundaries** | `dotnet build` is a hard, cheap, unambiguous verification signal. A boundary violation is a build error, not a code-review opinion. |
| **Dependency graph is explicit in `.csproj`** | The rules are machine-readable and machine-checkable, not buried in prose. |
| **Naming is fully derivable** | Given the use case name `CreateProduct`, every type name and file path follows mechanically. No judgement calls. |
| **Handlers take `DbContext` directly** | No repository/unit-of-work indirection to invent. "Old service method" maps to "new handler" close to 1:1. |

### What to add before using it as the reference

In priority order:

1. **Architecture tests** (NetArchTest or ArchUnitNET) encoding the boundary rules: module
   assemblies may not reference each other; handlers may not touch another module's `DbContext`;
   contracts assemblies may not reference implementation assemblies. This is the AI's feedback
   loop. Without it, the AI cannot know it broke the architecture.
2. **One integration test per module** (Testcontainers + `WebApplicationFactory`) so that
   "refactored correctly" becomes a checkable claim rather than a vibe.
3. **Fix the bugs in [06-known-issues.md](06-known-issues.md)** — otherwise every refactored API
   inherits a PII log leak and a broken event identity.
4. **Write the rules as rules.** Prose about architecture is weak guidance for an LLM; numbered
   constraints with a compilable example beside each are strong. That is what
   [04-adding-a-feature.md](04-adding-a-feature.md), [05-conventions.md](05-conventions.md) and
   [07-refactoring-playbook.md](07-refactoring-playbook.md) are for.

### Where it will fight you

Be honest about the limits before standardising on it:

- **It is opinionated about five libraries** (Carter, MediatR, Mapster, FluentValidation,
  MassTransit). Refactoring an existing API into this shape means adopting all five. If your
  APIs already use, say, AutoMapper and plain controllers, the diff is much larger than
  "reorganise the folders".
- **CQRS-with-MediatR is overhead for CRUD.** A module that is genuinely a thin table editor
  gains ceremony and loses nothing. Say so explicitly in the refactoring brief, or the AI will
  apply the full pattern uniformly.
- **MediatR v12+ is commercially licensed.** Check this before rolling it out across a company.
- **A single MediatR instance spans all modules**, so any module can technically send any other
  module's command if it can name the type. Only the Contracts-assembly discipline prevents
  that, and only architecture tests enforce the discipline.
