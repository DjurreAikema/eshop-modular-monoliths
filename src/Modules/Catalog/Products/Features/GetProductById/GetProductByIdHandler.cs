using Catalog.Products.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Shared.CQRS;

namespace Catalog.Products.Features.GetProductById;

public record GetProductByIdCommand(Guid Id) : IQuery<GetProductByIdResult>;

public record GetProductByIdResult(ProductDto Product);

public class GetProductByIdHandler(CatalogDbContext dbContext) : IQueryHandler<GetProductByIdCommand, GetProductByIdResult>
{
    public async Task<GetProductByIdResult> Handle(GetProductByIdCommand query, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == query.Id, cancellationToken);

        if (product is null)
        {
            throw new Exception($"Product not found: {query.Id}");
        }

        var productDto = product.Adapt<ProductDto>();

        return new GetProductByIdResult(productDto);
    }
}