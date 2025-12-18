using Catalog.Products.Exceptions;
using FluentValidation;
using Shared.CQRS;

namespace Catalog.Products.Features.DeleteProduct;

// --- Records
public record DeleteProductCommand(Guid ProductId) : ICommand<DeleteProductResult>;

public record DeleteProductResult(bool IsSuccess);

// --- Validation
public class DeleteProductCommandValidator : AbstractValidator<DeleteProductCommand>
{
    public DeleteProductCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty().WithMessage("Product Id is required");
    }
}

// --- Handler
public class DeleteProductHandler(CatalogDbContext dbContext) : ICommandHandler<DeleteProductCommand, DeleteProductResult>
{
    public async Task<DeleteProductResult> Handle(DeleteProductCommand command, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products
            .FindAsync([command.ProductId], cancellationToken: cancellationToken);

        if (product is null)
        {
            throw new ProductNotFoundException(command.ProductId);
        }

        dbContext.Products.Remove(product);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeleteProductResult(true);
    }
}