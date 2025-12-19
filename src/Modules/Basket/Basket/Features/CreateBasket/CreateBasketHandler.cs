using Basket.Basket.Dtos;
using Basket.Basket.Models;
using Basket.Data;
using FluentValidation;
using Shared.CQRS;

namespace Basket.Basket.Features.CreateBasket;

// --- Records
public record CreateBasketCommand(ShoppingCartDto ShoppingCart) : ICommand<CreateBasketResult>;

public record CreateBasketResult(Guid Id);

// -- Validation
public class CreateBasketCommandValidator : AbstractValidator<CreateBasketCommand>
{
    public CreateBasketCommandValidator()
    {
        RuleFor(x => x.ShoppingCart.UserName).NotEmpty().WithMessage("Username is required");
    }
}

// --- Handler
public class CreateBasketHandler(BasketDbContext dbContext) : ICommandHandler<CreateBasketCommand, CreateBasketResult>
{
    public async Task<CreateBasketResult> Handle(CreateBasketCommand command, CancellationToken cancellationToken)
    {
        var shoppingCart = CreateNewBasket(command.ShoppingCart);

        dbContext.ShoppingCarts.Add(shoppingCart);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CreateBasketResult(shoppingCart.Id);
    }

    private static ShoppingCart CreateNewBasket(ShoppingCartDto shoppingCartDto)
    {
        var shoppingCart = ShoppingCart.Create(
            Guid.NewGuid(),
            shoppingCartDto.UserName
        );

        shoppingCartDto.Items.ForEach(item =>
        {
            shoppingCart.AddItem(
                item.ProductId,
                item.Quantity,
                item.Color,
                item.Price,
                item.ProductName
            );
        });

        return shoppingCart;
    }
}