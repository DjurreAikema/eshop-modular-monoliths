# CLAUDE.md

Guidance for AI agents working in this repository.

## What this is

A .NET 10 **modular monolith** reference: one ASP.NET Core process hosting three independently
compiled business modules (Catalog, Basket, Ordering). Originally a course project; now
maintained as a **reference architecture and a target shape for refactoring other APIs**.

That second purpose matters: code here is copied. Follow the documented conventions exactly, and
never introduce a shortcut you would not want replicated across every API in the company.

## Commands

```bash
# Build (this is also the architecture check — boundaries are compile-time)
dotnet build src/eshop-modular-monolith.sln

# Infrastructure: Postgres, Redis, RabbitMQ, Seq, Keycloak
docker compose -f src/docker-compose.yml -f src/docker-compose.override.yml up -d

# Run
dotnet run --project src/Bootstrapper/Api/Api.csproj

# Add a migration (note the --project / --startup-project split)
dotnet ef migrations add <Name> \
  --project src/Modules/<Module>/<Module>.csproj \
  --startup-project src/Bootstrapper/Api/Api.csproj \
  --output-dir Data/Migrations \
  --context <Module>DbContext
```

There are no tests yet. See [docs/06-known-issues.md](docs/06-known-issues.md#5-no-tests-and-no-architecture-tests).

## Documentation map

Read the one that matches the task; do not read all of them by default.

| Task | Read |
|---|---|
| Understand the overall shape | [docs/01-architecture.md](docs/01-architecture.md) |
| Work inside one module | [docs/02-module-anatomy.md](docs/02-module-anatomy.md) |
| Make two modules interact | [docs/03-module-communication.md](docs/03-module-communication.md) |
| Add an endpoint / use case / module | [docs/04-adding-a-feature.md](docs/04-adding-a-feature.md) |
| Check a naming or layout rule | [docs/05-conventions.md](docs/05-conventions.md) |
| Before copying any pattern from here | [docs/06-known-issues.md](docs/06-known-issues.md) |
| Port another API into this shape | [docs/07-refactoring-playbook.md](docs/07-refactoring-playbook.md) |
| Judge whether the architecture fits | [docs/00-assessment.md](docs/00-assessment.md) |

## The rules that must never be broken

1. **A module assembly never references another module's implementation assembly.** Only
   `<Other>.Contracts`. If a change seems to need one, the design is wrong — stop and say so.
2. **A handler injects only its own module's `DbContext`.** No cross-schema queries, no
   cross-schema foreign keys.
3. **Entities never leave a module.** Contracts assemblies expose DTO records only.
4. **A use case is one folder with two files**: `<UseCase>Endpoint.cs` and
   `<UseCase>Handler.cs`, under `<Module>/<Aggregate>/Features/<UseCase>/`.
5. **Endpoints contain no logic**: map to the message, `sender.Send`, map to the response,
   return. No validation, no data access, no `try/catch`.
6. **Business rules live in the aggregate**, not in the handler. All setters private;
   construction through a static `Create`/`Of` factory.
7. **Error paths throw typed exceptions.** `CustomExceptionHandler` maps them to
   `ProblemDetails`. Never catch broadly to build a response.
8. **Never register endpoints, handlers or validators by hand.** Carter, MediatR and MassTransit
   discover them by assembly scan. `Program.cs` changes only when a whole module is added.

## Cross-module interaction — pick with this tree

```
Needs a fresh answer to finish the current request?  → query via <Other>.Contracts (Pattern A)
Something happened, same module should react?        → domain event (Pattern B)
Something happened, another module reacts eventually?→ integration event (C1)
   ...and it must not be lost?                       → integration event + outbox (C2)
About to reference another module directly?          → STOP. Boundary violation.
```

Full detail and code for each: [docs/03-module-communication.md](docs/03-module-communication.md).

## Naming — derive, do not invent

From the use case name (PascalCase verb phrase, e.g. `CreateProduct`):

```
CreateProductRequest   CreateProductCommand   CreateProductCommandValidator
CreateProductResponse  CreateProductResult    CreateProductHandler   CreateProductEndpoint
```

Handlers implement `ICommandHandler<,>` / `IQueryHandler<,>` from `Shared.Contracts.CQRS`, never
MediatR's `IRequestHandler` directly.

## Style

- .NET 10, file-scoped namespaces, nullable enabled, primary constructors for DI.
- `record` for messages, DTOs and events; `class` for entities and aggregates.
- Collection expressions (`= []`), `ArgumentException.ThrowIfNullOrEmpty` guards.
- Async throughout; `CancellationToken` last and always passed on.
- Reads use `AsNoTracking()`, and an explicit `OrderBy` whenever paged.
- Structured logging with named placeholders — never string interpolation, and
  **never log a whole command object** (they carry PII).

## Known traps in this codebase

Do not treat existing code as automatically exemplary. These are known-bad and documented in
[docs/06-known-issues.md](docs/06-known-issues.md):

- `.RequireAuthorization()` is **commented out on every endpoint**. The API is open.
- `LoggingBehavior` logs full request objects — card numbers and CVVs reach Seq in plaintext.
- `IntegrationEvent.EventId` / `IDomainEvent.EventId` are `=> Guid.NewGuid()`, so a new id is
  produced on every read. Do not build idempotency on them until fixed.
- Domain events dispatch on `SavingChanges`, i.e. **before** commit.
- `CheckoutBasketHandler` swallows all exceptions and returns 200 with `isSuccess: false`.
- `BasketRepository.GetBasket` discards the result of `AsNoTracking()` — the parameter is a no-op.
- `Ordering` builds orders from **hardcoded product GUIDs** because the checkout event carries no
  line items.
- Integration events live in shared `Shared.Messaging` rather than each publisher's contracts
  assembly. Follow `Catalog.Contracts` as the model for new contracts, not `Shared.Messaging`.

If a task touches one of these, fix it properly rather than extending it — and say that you did.

## Before finishing any change

- [ ] `dotnet build src/eshop-modular-monolith.sln` is green.
- [ ] No new project reference between two module implementation assemblies.
- [ ] New files follow the two-file slice layout and the derived naming.
- [ ] `Program.cs` untouched unless a whole module was added.
- [ ] Nothing from the "known traps" list was copied into new code.
