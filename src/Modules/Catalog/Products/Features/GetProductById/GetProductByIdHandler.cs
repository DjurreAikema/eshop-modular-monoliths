using Catalog.Products.Dtos;
using Catalog.Products.Exceptions;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Shared.CQRS;

namespace Catalog.Products.Features.GetProductById;

// --- Records
public record GetProductByIdQuery(Guid Id) : IQuery<GetProductByIdResult>;

public record GetProductByIdResult(ProductDto Product);

// --- Handler
public class GetProductByIdHandler(CatalogDbContext dbContext) : IQueryHandler<GetProductByIdQuery, GetProductByIdResult>
{
    public async Task<GetProductByIdResult> Handle(GetProductByIdQuery query, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == query.Id, cancellationToken);

        if (product is null)
        {
            throw new ProductNotFoundException(query.Id);
        }

        var productDto = product.Adapt<ProductDto>();

        return new GetProductByIdResult(productDto);
    }
}