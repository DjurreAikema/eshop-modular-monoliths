using Catalog.Contracts.Products.Dtos;
using Catalog.Contracts.Products.Features.GetProductById;
using Catalog.Products.Exceptions;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Shared.Contracts.CQRS;

namespace Catalog.Products.Features.GetProductById;
// --- Records: In Catalog.Contracts

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