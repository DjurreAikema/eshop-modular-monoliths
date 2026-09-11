# eShop Modular Monolith

A .NET 10 reference implementation of a **modular monolith**: one deployable ASP.NET Core
process composed of independently-compiled business modules (Catalog, Basket, Ordering)
that share nothing but explicit contracts.

Originally built following a course; it has since been analysed and documented as a
**reference architecture and refactoring target** — see [`docs/`](docs/).

---

## Quick orientation

| I want to... | Read |
|---|---|
| Know whether this architecture is any good | [docs/00-assessment.md](docs/00-assessment.md) |
| Understand the overall shape | [docs/01-architecture.md](docs/01-architecture.md) |
| Understand what one module contains | [docs/02-module-anatomy.md](docs/02-module-anatomy.md) |
| Know how modules talk to each other | [docs/03-module-communication.md](docs/03-module-communication.md) |
| Add a new endpoint/feature | [docs/04-adding-a-feature.md](docs/04-adding-a-feature.md) |
| Know the naming & file rules | [docs/05-conventions.md](docs/05-conventions.md) |
| Know what NOT to copy from here | [docs/06-known-issues.md](docs/06-known-issues.md) |
| Port an existing API into this shape | [docs/07-refactoring-playbook.md](docs/07-refactoring-playbook.md) |

AI agents: start at [CLAUDE.md](CLAUDE.md).

---

## Running it

Infrastructure first (Postgres, Redis, RabbitMQ, Seq, Keycloak):

```bash
docker compose -f src/docker-compose.yml -f src/docker-compose.override.yml up -d
```

Then the API:

```bash
dotnet run --project src/Bootstrapper/Api/Api.csproj
```

Migrations are applied and seed data inserted automatically on startup
(`UseMigration<TContext>()` in each module's `UseXModule`).

| Service | Endpoint |
|---|---|
| Postgres | `localhost:5432` (`postgres`/`postgres`, db `EShopDb`) |
| Redis | `localhost:6379` |
| RabbitMQ management | http://localhost:15672 (`guest`/`guest`) |
| Seq (logs) | http://localhost:9091 |
| Keycloak | http://localhost:9090 (`admin`/`admin`) |

> Authentication is currently **disabled** on every endpoint — see
> [docs/06-known-issues.md](docs/06-known-issues.md#1-authentication-is-wired-but-not-enforced).

## Building

```bash
dotnet build src/eshop-modular-monolith.sln
```

## Tech stack

.NET 10 · ASP.NET Core Minimal APIs via **Carter** · **MediatR** (CQRS + in-process events) ·
**MassTransit**/RabbitMQ (integration events) · **EF Core** + Npgsql (schema-per-module) ·
**FluentValidation** · **Mapster** · **Scrutor** (decorators) · Redis · Serilog + Seq · Keycloak
