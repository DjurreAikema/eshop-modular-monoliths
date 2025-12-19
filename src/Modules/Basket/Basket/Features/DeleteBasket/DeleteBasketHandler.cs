using Basket.Basket.Exceptions;
using Basket.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Shared.CQRS;

namespace Basket.Basket.Features.DeleteBasket;

// --- Records
public record DeleteBasketCommand(string UserName) : ICommand<DeleteBasketResult>;

public record DeleteBasketResult(bool IsSuccess);

// --- Validation
public class DeleteBasketCommandValidator : AbstractValidator<DeleteBasketCommand>
{
    public DeleteBasketCommandValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().WithMessage("UserName is required");
    }
}

// --- Handler
public class DeleteBasketHandler(BasketDbContext dbContext) : ICommandHandler<DeleteBasketCommand, DeleteBasketResult>
{
    public async Task<DeleteBasketResult> Handle(DeleteBasketCommand command, CancellationToken cancellationToken)
    {
        var basket = await dbContext.ShoppingCarts
            .SingleOrDefaultAsync(x => x.UserName == command.UserName, cancellationToken);

        if (basket is null)
        {
            throw new BasketNotFoundException(command.UserName);
        }

        dbContext.ShoppingCarts.Remove(basket);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeleteBasketResult(true);
    }
}