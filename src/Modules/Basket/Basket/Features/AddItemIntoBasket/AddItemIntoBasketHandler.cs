using Basket.Basket.Dtos;
using Basket.Basket.Exceptions;
using Basket.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Shared.CQRS;

namespace Basket.Basket.Features.AddItemIntoBasket;

// --- Records
public record AddItemIntoBasketCommand(string UserName, ShoppingCartItemDto ShoppingCartItem) : ICommand<AddItemIntoBasketResult>;

public record AddItemIntoBasketResult(Guid Id);

// --- Validation
public class AddItemIntoBasketCommandValidator : AbstractValidator<AddItemIntoBasketCommand>
{
    public AddItemIntoBasketCommandValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().WithMessage("UserName is required");
        RuleFor(x => x.ShoppingCartItem.ProductId).NotEmpty().WithMessage("ProductId is required");
        RuleFor(x => x.ShoppingCartItem.Quantity).GreaterThan(0).WithMessage("Quantity must be greater than 0");
    }
}

// --- Handler
public class AddItemIntoBasketHandler(BasketDbContext dbContext) : ICommandHandler<AddItemIntoBasketCommand, AddItemIntoBasketResult>
{
    public async Task<AddItemIntoBasketResult> Handle(AddItemIntoBasketCommand command, CancellationToken cancellationToken)
    {
        var shoppingCart = await dbContext.ShoppingCarts
            .Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.UserName == command.UserName, cancellationToken);

        if (shoppingCart is null)
        {
            throw new BasketNotFoundException(command.UserName);
        }

        shoppingCart.AddItem(
            command.ShoppingCartItem.ProductId,
            command.ShoppingCartItem.Quantity,
            command.ShoppingCartItem.Color,
            command.ShoppingCartItem.Price,
            command.ShoppingCartItem.ProductName
        );

        await dbContext.SaveChangesAsync(cancellationToken);

        return new AddItemIntoBasketResult(shoppingCart.Id);
    }
}