# Module communication

The hardest part of a modular monolith is not splitting the code — it is deciding how the
pieces talk. This codebase demonstrates four patterns. Pick with the decision tree, then follow
the matching recipe.

## Decision tree

```
Does module A need something from module B?
│
├─ Is it data A needs RIGHT NOW to complete the current request,
│  where a stale answer would be wrong?
│  │
│  └─ YES → Pattern A: synchronous query via B's Contracts assembly
│           (e.g. Basket needs the current product price before adding an item)
│
├─ Is it "something happened in B, and A should react eventually"?
│  │
│  ├─ Must the reaction survive a crash between B's commit and A's reaction?
│  │  │
│  │  ├─ YES → Pattern C2: integration event + transactional outbox
│  │  │        (e.g. basket checkout must always produce an order)
│  │  │
│  │  └─ NO  → Pattern C1: integration event, published directly
│  │            (e.g. price change propagating to baskets)
│  │
│  └─ Is the reaction entirely INSIDE the same module?
│     │
│     └─ YES → Pattern B: domain event (MediatR notification)
│
└─ Are you about to reference B's DbContext, entity or handler directly?
   │
   └─ STOP. This is a boundary violation. Go back to the top.
```

Cost ordering, cheapest to most expensive: **B < A < C1 < C2**. Coupling ordering, loosest to
tightest: **C2 ≈ C1 < A < direct reference (forbidden)**.

---

## Pattern A — Synchronous, in-process, via a Contracts assembly

**When:** module A cannot complete its work without a fresh answer from module B.

**Live example:** `Basket` needs the current price and name of a product before it can add a
line to a cart.

### How it works

1. `Catalog` publishes the query type in a **separate, dependency-light assembly**,
   `Catalog.Contracts`:

```csharp
// Modules/Catalog.Contracts/Products/Features/GetProductById/GetProductByIdQuery.cs
namespace Catalog.Contracts.Products.Features.GetProductById;

public record GetProductByIdQuery(Guid Id) : IQuery<GetProductByIdResult>;
public record GetProductByIdResult(ProductDto Product);
```

```csharp
// Modules/Catalog.Contracts/Products/Dtos/ProductDto.cs
public record ProductDto(Guid Id, string Name, List<string> Categories,
                         string Description, string ImageFile, decimal Price);
```

2. `Catalog` keeps the **handler** in its own implementation assembly. Note the comment marking
   where the records went:

```csharp
// Modules/Catalog/Products/Features/GetProductById/GetProductByIdHandler.cs
namespace Catalog.Products.Features.GetProductById;
// --- Records: In Catalog.Contracts

public class GetProductByIdHandler(CatalogDbContext dbContext)
    : IQueryHandler<GetProductByIdQuery, GetProductByIdResult>
{
    public async Task<GetProductByIdResult> Handle(GetProductByIdQuery query, CancellationToken ct)
    {
        var product = await dbContext.Products.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == query.Id, ct)
            ?? throw new ProductNotFoundException(query.Id);

        return new GetProductByIdResult(product.Adapt<ProductDto>());
    }
}
```

3. `Basket.csproj` references **only the contracts**:

```xml
<ProjectReference Include="..\Catalog.Contracts\Catalog.Contracts.csproj" />
```

4. `Basket` sends the query through the shared MediatR instance:

```csharp
public class AddItemIntoBasketHandler(IBasketRepository repository, ISender sender)
    : ICommandHandler<AddItemIntoBasketCommand, AddItemIntoBasketResult>
{
    public async Task<AddItemIntoBasketResult> Handle(AddItemIntoBasketCommand command, CancellationToken ct)
    {
        var shoppingCart = await repository.GetBasket(command.UserName, false, ct);

        // ── crosses into Catalog, in-process, same transaction scope ──
        var result = await sender.Send(new GetProductByIdQuery(command.ShoppingCartItem.ProductId), ct);

        shoppingCart.AddItem(
            command.ShoppingCartItem.ProductId,
            command.ShoppingCartItem.Quantity,
            command.ShoppingCartItem.Color,
            result.Product.Price,      // ← copied into Basket's own storage
            result.Product.Name);

        await repository.SaveChangesAsync(command.UserName, ct);
        return new AddItemIntoBasketResult(shoppingCart.Id);
    }
}
```

### Why this is safe

The dispatch works because `Program.cs` registers **one** MediatR instance scanning all module
assemblies, so Catalog's handler is findable from Basket's handler. The *boundary* comes from
the project graph: Basket can name `GetProductByIdQuery` because it references
`Catalog.Contracts`, and cannot name `CatalogDbContext` or `Product` because it does not
reference `Catalog`.

### Rules

- A Contracts assembly contains **only** records: queries, results, DTOs. No handlers, no
  entities, no EF, no infrastructure package references.
- It may reference `Shared.Contracts` (for `IQuery<T>`) and nothing else.
- Expose **queries**, not commands. Letting module A command module B couples their business
  rules; a command that must cross a boundary should almost always be an integration event.
- Treat what comes back as a **copy**. `ShoppingCartItem` stores `Price` and `ProductName` as
  its own columns — Basket owns that data from then on.

### Cost

This creates a compile-time and a runtime dependency. If Catalog is later extracted into its
own service, every Pattern-A call becomes a network call and must be redesigned. Use it
sparingly and only for genuine read-now needs.

---

## Pattern B — Domain events (inside one module)

**When:** something changed in the domain and other parts of *the same module* should react.

**Live example:** `Product.Update` raises `ProductPriceChangedEvent`.

### How it works

The aggregate raises the event as part of the state change:

```csharp
public void Update(string name, /* ... */ decimal price)
{
    Name = name;
    if (Price != price)
    {
        Price = price;
        AddDomainEvent(new ProductPriceChangedEvent(this));
    }
}
```

```csharp
// Products/Events/ProductPriceChangedEvent.cs
public record ProductPriceChangedEvent(Product Product) : IDomainEvent;
```

`IDomainEvent : INotification`, so MediatR delivers it. Nobody calls `Publish` explicitly —
`DispatchDomainEventsInterceptor` picks the events off every tracked aggregate during
`SaveChanges`, clears them, and publishes each:

```csharp
var aggregates = context.ChangeTracker.Entries<IAggregate>()
    .Where(a => a.Entity.DomainEvents.Any()).Select(a => a.Entity);

var domainEvents = aggregates.SelectMany(a => a.DomainEvents).ToList();
aggregates.ToList().ForEach(a => a.ClearDomainEvents());

foreach (var domainEvent in domainEvents)
    await mediator.Publish(domainEvent);
```

Handlers are ordinary notification handlers:

```csharp
public class ProductCreatedEventHandler(ILogger<ProductCreatedEventHandler> logger)
    : INotificationHandler<ProductCreatedEvent>
{
    public Task Handle(ProductCreatedEvent notification, CancellationToken ct)
    {
        logger.LogInformation("Domain event handled: {DomainEvent}", notification.GetType().Name);
        return Task.CompletedTask;
    }
}
```

### Rules

- Domain events carry **domain objects** and stay inside the module. They are not a public
  contract; rename them freely.
- Raise them in the aggregate, never in the handler.
- A domain event handler may do module-local work, or translate the event into an **integration
  event** — that is the bridge from Pattern B to Pattern C.

### Caveat

Dispatch happens on `SavingChanges`, **before** the commit. Handlers therefore run inside the
pending transaction and a rollback does not un-send them. See
[06-known-issues.md](06-known-issues.md#2-domain-events-are-dispatched-before-commit).

---

## Pattern C1 — Integration event, published directly

**When:** another module should react eventually, and losing the message occasionally is
tolerable.

**Live example:** a price change propagating into existing baskets.

### Publisher side (Catalog)

A domain-event handler translates the domain event into an integration event and publishes it
on the bus:

```csharp
public class ProductPriceChangedEventHandler(IBus bus, ILogger<...> logger)
    : INotificationHandler<ProductPriceChangedEvent>
{
    public async Task Handle(ProductPriceChangedEvent notification, CancellationToken ct)
    {
        var integrationEvent = new ProductPriceChangedIntegrationEvent
        {
            ProductId = notification.Product.Id,
            Name      = notification.Product.Name,
            Price     = notification.Product.Price,
            // ...
        };

        await bus.Publish(integrationEvent, ct);
    }
}
```

### Contract

```csharp
// Shared.Messaging/Events/ProductPriceChangedIntegrationEvent.cs
public record ProductPriceChangedIntegrationEvent : IntegrationEvent
{
    public Guid ProductId { get; set; }
    public string Name { get; set; } = null!;
    public decimal Price { get; set; }
    // ...
}
```

### Consumer side (Basket)

```csharp
public class ProductPriceChangedIntegrationEventHandler(ISender sender, ILogger<...> logger)
    : IConsumer<ProductPriceChangedIntegrationEvent>
{
    public async Task Consume(ConsumeContext<ProductPriceChangedIntegrationEvent> context)
    {
        var command = new UpdateItemPriceInBasketCommand(context.Message.ProductId, context.Message.Price);
        var result = await sender.Send(command);
        // ...
    }
}
```

Note the shape: **the consumer does not contain business logic.** It translates the message into
a local command and sends it through MediatR, so the work goes through the same validation and
logging pipeline as an HTTP request, and `UpdateItemPriceInBasket` remains a normal, testable
use case.

MassTransit binds the consumer automatically — `config.AddConsumers(assemblies)` in
`MassTransitExtensions`, with `SetKebabCaseEndpointNameFormatter()` deriving the queue name.

### Reliability caveat

This publishes from inside `SavingChanges`, so the message can go out for a transaction that
later rolls back. If that matters, use C2.

---

## Pattern C2 — Integration event with a transactional outbox

**When:** the event must not be lost, and must not be sent for work that did not commit.

**Live example:** basket checkout must always result in an order.

### 1. Write the message in the same transaction as the business change

```csharp
public class CheckoutBasketHandler(BasketDbContext dbContext)
    : ICommandHandler<CheckoutBasketCommand, CheckoutBasketResult>
{
    public async Task<CheckoutBasketResult> Handle(CheckoutBasketCommand command, CancellationToken ct)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);

        var basket = await dbContext.ShoppingCarts.Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.UserName == command.BasketCheckout.UserName, ct)
            ?? throw new BasketNotFoundException(command.BasketCheckout.UserName);

        var eventMessage = command.BasketCheckout.Adapt<BasketCheckoutIntegrationEvent>();
        eventMessage.TotalPrice = basket.TotalPrice;

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id         = Guid.NewGuid(),
            Type       = typeof(BasketCheckoutIntegrationEvent).AssemblyQualifiedName!,
            Content    = JsonSerializer.Serialize(eventMessage),
            OccuredOn  = DateTime.UtcNow
        });

        dbContext.ShoppingCarts.Remove(basket);     // business change
                                                    //   ... and message ...
        await dbContext.SaveChangesAsync(ct);       //   ... commit together
        await transaction.CommitAsync(ct);

        return new CheckoutBasketResult(true);
    }
}
```

The basket removal and the outbox row are one atomic write. Either both happen or neither does.

### 2. The outbox table

```csharp
public class OutboxMessage : Entity<Guid>
{
    public string Type { get; set; } = null!;       // AssemblyQualifiedName of the event
    public string Content { get; set; } = null!;    // JSON payload
    public DateTime OccuredOn { get; set; }
    public DateTime? ProcessedOn { get; set; }      // null = not yet published
}
```

### 3. A background service drains it

```csharp
public class OutboxProcessor(IServiceProvider serviceProvider, IBus bus, ILogger<...> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<BasketDbContext>();

            var outboxMessages = await dbContext.OutboxMessages
                .Where(m => m.ProcessedOn == null).ToListAsync(stoppingToken);

            foreach (var message in outboxMessages)
            {
                var eventType = Type.GetType(message.Type);
                var eventMessage = JsonSerializer.Deserialize(message.Content, eventType!);

                await bus.Publish(eventMessage!, stoppingToken);
                message.ProcessedOn = DateTime.UtcNow;
            }

            await dbContext.SaveChangesAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
```

Registered in `BasketModule`: `services.AddHostedService<OutboxProcessor>();`

### 4. The consumer (Ordering)

```csharp
public class BasketCheckoutIntegrationEventHandler(ISender sender, ILogger<...> logger)
    : IConsumer<BasketCheckoutIntegrationEvent>
{
    public async Task Consume(ConsumeContext<BasketCheckoutIntegrationEvent> context)
    {
        var createOrderCommand = MapToCreateOrderCommand(context.Message);
        await sender.Send(createOrderCommand);
    }
}
```

### Guarantees and obligations

Outbox gives **at-least-once** delivery: a crash after `bus.Publish` but before
`SaveChangesAsync` republishes the message on the next tick.

**Consumers must therefore be idempotent.** Nothing in this codebase implements that — see
[06-known-issues.md](06-known-issues.md#3-event-ids-are-regenerated-on-every-read). The
intended mechanism is a stable `EventId` plus a processed-messages table on the consumer side.

The `Type.GetType(AssemblyQualifiedName)` round-trip also couples the persisted message to the
CLR assembly identity — renaming the assembly breaks unprocessed messages, and the outbox cannot
be read by a different service. Prefer a stable logical name mapped to a type.

---

## Summary table

| | A: Contracts query | B: Domain event | C1: Integration event | C2: Outbox event |
|---|---|---|---|---|
| Scope | cross-module | intra-module | cross-module | cross-module |
| Transport | MediatR (in-process) | MediatR (in-process) | RabbitMQ | RabbitMQ |
| Consistency | immediate | immediate | eventual | eventual |
| Compile-time coupling | yes (Contracts) | none | contract record | contract record |
| Delivery guarantee | n/a (call) | n/a (call) | best effort | at-least-once |
| Survives module extraction | no — becomes a network call | n/a | yes | yes |
| Consumer must be idempotent | no | no | yes | yes |
| In this repo | `GetProductByIdQuery` | `ProductCreatedEvent`, `ProductPriceChangedEvent`, `OrderCreatedEvent` | `ProductPriceChangedIntegrationEvent` | `BasketCheckoutIntegrationEvent` |

## Forbidden

- One module referencing another module's implementation assembly.
- Injecting another module's `DbContext`.
- Querying another module's schema or joining across schemas.
- Foreign keys across schemas.
- Exposing entities (as opposed to DTOs) in a Contracts assembly.
- A consumer containing business logic instead of delegating to a local command.
