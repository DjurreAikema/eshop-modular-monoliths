using Catalog.Contracts.Products.Dtos;
using Shared.Contracts.CQRS;

namespace Catalog.Contracts.Products.Features.GetProductById;

// --- Records
public record GetProductByIdQuery(Guid Id) : IQuery<GetProductByIdResult>;

public record GetProductByIdResult(ProductDto Product);