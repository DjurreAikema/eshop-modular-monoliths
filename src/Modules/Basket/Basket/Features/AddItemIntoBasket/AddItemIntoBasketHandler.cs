using Basket.Basket.Dtos;
using Basket.Data.Repository;
using Catalog.Contracts.Products.Features.GetProductById;
using FluentValidation;
using MediatR;
using Shared.Contracts.CQRS;

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
public class AddItemIntoBasketHandler(
    IBasketRepository repository,
    ISender sender
) : ICommandHandler<AddItemIntoBasketCommand, AddItemIntoBasketResult>
{
    public async Task<AddItemIntoBasketResult> Handle(AddItemIntoBasketCommand command, CancellationToken cancellationToken)
    {
        var shoppingCart = await repository.GetBasket(command.UserName, false, cancellationToken);

        var result = await sender.Send(new GetProductByIdQuery(command.ShoppingCartItem.ProductId), cancellationToken);

        shoppingCart.AddItem(
            command.ShoppingCartItem.ProductId,
            command.ShoppingCartItem.Quantity,
            command.ShoppingCartItem.Color,
            result.Product.Price,
            result.Product.Name
        );

        await repository.SaveChangesAsync(command.UserName, cancellationToken);

        return new AddItemIntoBasketResult(shoppingCart.Id);
    }
}