# Adding a feature

A feature (use case) is **one folder with two files**. Nothing else in the solution changes —
no registration, no `Program.cs` edit, no route table.

```
Modules/<Module>/<Aggregate>/Features/<UseCase>/
├── <UseCase>Endpoint.cs      HTTP: Request record, Response record, ICarterModule
└── <UseCase>Handler.cs       App:  Command/Query record, Result record, Validator, Handler
```

Name the use case as a **verb phrase in PascalCase**: `CreateProduct`, `GetOrdersByCustomer`,
`RemoveItemFromBasket`. Every type name derives mechanically from it.

| Type | Name | Lives in |
|---|---|---|
| HTTP request body | `<UseCase>Request` | Endpoint file |
| HTTP response body | `<UseCase>Response` | Endpoint file |
| Endpoint class | `<UseCase>Endpoint` | Endpoint file |
| MediatR message | `<UseCase>Command` or `<UseCase>Query` | Handler file |
| MediatR result | `<UseCase>Result` | Handler file |
| Validator | `<UseCase>CommandValidator` | Handler file |
| Handler | `<UseCase>Handler` | Handler file |

The Request/Command and Result/Response pairs look redundant. They are deliberate: the HTTP
records may change with API versioning without touching the application layer, and the
application records can be reused by a consumer or another module.

---

## Command template (writes)

### `<UseCase>Endpoint.cs`

```csharp
using Carter;
using Mapster;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace <Module>.<Aggregate>.Features.<UseCase>;

// --- Records
public record <UseCase>Request(/* HTTP body shape */);

public record <UseCase>Response(/* HTTP response shape */);

// --- Endpoint
public class <UseCase>Endpoint : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/<resource>", async (<UseCase>Request request, ISender sender) =>
            {
                var command = request.Adapt<<UseCase>Command>();

                var result = await sender.Send(command);

                var response = result.Adapt<<UseCase>Response>();

                return Results.Created($"/<resource>/{response.Id}", response);
            })
            .WithName("<UseCase>")
            .Produces<<UseCase>Response>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("<Human readable>")
            .WithDescription("<Human readable>")
            .RequireAuthorization();
    }
}
```

The endpoint body is always the same four steps: **map in → send → map out → return**. It
contains no business logic, no validation, no `try/catch`, and no data access.

### `<UseCase>Handler.cs`

```csharp
using FluentValidation;
using Shared.Contracts.CQRS;

namespace <Module>.<Aggregate>.Features.<UseCase>;

// --- Records
public record <UseCase>Command(/* ... */) : ICommand<<UseCase>Result>;

public record <UseCase>Result(/* ... */);

// --- Validation
public class <UseCase>CommandValidator : AbstractValidator<<UseCase>Command>
{
    public <UseCase>CommandValidator()
    {
        RuleFor(x => x.Something).NotEmpty().WithMessage("Something is required");
    }
}

// --- Handler
public class <UseCase>Handler(<Module>DbContext dbContext)
    : ICommandHandler<<UseCase>Command, <UseCase>Result>
{
    public async Task<<UseCase>Result> Handle(<UseCase>Command command, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Things.FindAsync([command.Id], cancellationToken)
            ?? throw new ThingNotFoundException(command.Id);

        entity.DoTheDomainThing(command.Value);      // ← business rules live in the aggregate

        await dbContext.SaveChangesAsync(cancellationToken);

        return new <UseCase>Result(true);
    }
}
```

Handler rules:

- Use primary-constructor injection.
- Inject **your own module's** `DbContext` (or repository, if the module has one).
- Throw typed exceptions; never return error codes and never catch to build an HTTP response.
  `CustomExceptionHandler` turns exceptions into `ProblemDetails`.
- Put invariants and state transitions in the aggregate, not here.
- Always pass the `CancellationToken` through.

---

## Query template (reads)

Identical shape, with `IQuery`/`IQueryHandler`, `MapGet`, and usually no validator —
`ValidationBehavior` only runs for `ICommand<T>`.

```csharp
// --- Records
public record Get<Things>Query(PaginationRequest PaginationRequest) : IQuery<Get<Things>Result>;

public record Get<Things>Result(PaginatedResult<<Thing>Dto> Things);

// --- Handler
public class Get<Things>Handler(<Module>DbContext dbContext)
    : IQueryHandler<Get<Things>Query, Get<Things>Result>
{
    public async Task<Get<Things>Result> Handle(Get<Things>Query query, CancellationToken cancellationToken)
    {
        var pageIndex = query.PaginationRequest.PageIndex;
        var pageSize  = query.PaginationRequest.PageSize;

        var totalCount = await dbContext.Things.LongCountAsync(cancellationToken);

        var things = await dbContext.Things
            .AsNoTracking()                       // ← always, on reads
            .OrderBy(t => t.Name)                 // ← always; paging without ordering is undefined
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new Get<Things>Result(
            new PaginatedResult<<Thing>Dto>(pageIndex, pageSize, totalCount, things.Adapt<List<<Thing>Dto>>()));
    }
}
```

Paginated endpoints bind the request with `[AsParameters]`:

```csharp
app.MapGet("/products", async ([AsParameters] PaginationRequest request, ISender sender) =>
{
    var result = await sender.Send(new GetProductsQuery(request));
    return Results.Ok(result.Adapt<GetProductsResponse>());
})
```

`PaginationRequest` is `record PaginationRequest(int PageIndex = 0, int PageSize = 10)`, so
`GET /products?pageIndex=2&pageSize=25` works without any extra binding code.

---

## Worked example: `CreateProduct`

**`Modules/Catalog/Products/Features/CreateProduct/CreateProductEndpoint.cs`**

```csharp
namespace Catalog.Products.Features.CreateProduct;

public record CreateProductRequest(ProductDto Product);
public record CreateProductResponse(Guid Id);

public class CreateProductEndpoint : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/products", async (CreateProductRequest request, ISender sender) =>
            {
                var command  = request.Adapt<CreateProductCommand>();
                var result   = await sender.Send(command);
                var response = result.Adapt<CreateProductResponse>();

                return Results.Created($"/products/{response.Id}", response);
            })
            .WithName("CreateProduct")
            .Produces<CreateProductResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Create Product")
            .WithDescription("Create Product");
    }
}
```

**`Modules/Catalog/Products/Features/CreateProduct/CreateProductHandler.cs`**

```csharp
namespace Catalog.Products.Features.CreateProduct;

public record CreateProductCommand(ProductDto Product) : ICommand<CreateProductResult>;
public record CreateProductResult(Guid Id);

public class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.Product.Name).NotEmpty().WithMessage("Name is required");
        RuleFor(x => x.Product.Categories).NotEmpty().WithMessage("Categories are required");
        RuleFor(x => x.Product.Price).GreaterThan(0).WithMessage("Price must be greater than 0");
    }
}

public class CreateProductHandler(CatalogDbContext dbContext)
    : ICommandHandler<CreateProductCommand, CreateProductResult>
{
    public async Task<CreateProductResult> Handle(CreateProductCommand command, CancellationToken cancellationToken)
    {
        var product = Product.Create(
            Guid.NewGuid(),
            command.Product.Name,
            command.Product.Categories,
            command.Product.Description,
            command.Product.ImageFile,
            command.Product.Price);       // ← raises ProductCreatedEvent internally

        dbContext.Products.Add(product);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreateProductResult(product.Id);
    }
}
```

That is the whole feature. Carter finds the endpoint, MediatR finds the handler,
`AddValidatorsFromAssemblies` finds the validator.

---

## Adding a whole module

Four mechanical edits plus the module skeleton.

1. **Create the project** and add it to the solution:

```bash
dotnet new classlib -o src/Modules/Payments -f net10.0
dotnet sln src/eshop-modular-monolith.sln add src/Modules/Payments/Payments.csproj
```

2. **Reference the shared projects** in `Payments.csproj` (copy from `Ordering.csproj`):

```xml
<ItemGroup>
  <ProjectReference Include="..\..\Shared\Shared.csproj" />
  <ProjectReference Include="..\..\Shared.Messaging\Shared.Messaging.csproj" />
</ItemGroup>
```

3. **Create `PaymentsModule.cs`** — copy `OrderingModule.cs` and rename. Keep the three comment
   sections.

4. **Create `Data/PaymentsDbContext.cs`** with `builder.HasDefaultSchema("payments")`.

5. **Wire it into the bootstrapper** — `Api.csproj` gets a `ProjectReference`, and `Program.cs`
   gets three lines:

```csharp
var paymentsAssembly = typeof(PaymentsModule).Assembly;        // + add to the three Add*WithAssemblies calls

builder.Services.AddPaymentsModule(builder.Configuration);

app.UsePaymentsModule();
```

6. **Create the initial migration:**

```bash
dotnet ef migrations add InitialCreate \
  --project src/Modules/Payments/Payments.csproj \
  --startup-project src/Bootstrapper/Api/Api.csproj \
  --output-dir Data/Migrations \
  --context PaymentsDbContext
```

The `--project` / `--startup-project` split matters: the migration must land in the **module's**
assembly, while configuration and DI come from the **Api**.

7. **Add a Contracts project** if other modules will query this one — mirror
   `Catalog.Contracts`.

---

## Checklist

Before calling a feature done:

- [ ] Folder is `Features/<UseCase>/` with exactly the two files.
- [ ] All six type names derive from the use case name.
- [ ] Endpoint contains no logic beyond map → send → map → return.
- [ ] Handler injects only its own module's `DbContext`/repository.
- [ ] Business rules are in the aggregate, not the handler.
- [ ] Commands have a validator; error paths throw typed exceptions.
- [ ] Reads use `AsNoTracking()` and an explicit `OrderBy` when paged.
- [ ] `CancellationToken` is threaded all the way through.
- [ ] No `using` of another module's implementation namespace.
- [ ] `dotnet build src/eshop-modular-monolith.sln` succeeds.
