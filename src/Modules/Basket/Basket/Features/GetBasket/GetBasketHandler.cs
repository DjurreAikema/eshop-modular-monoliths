using Basket.Basket.Dtos;
using Basket.Data.Repository;
using FluentValidation;
using Mapster;
using Shared.CQRS;

namespace Basket.Basket.Features.GetBasket;

// --- Records
public record GetBasketQuery(string UserName) : IQuery<GetBasketResult>;

public record GetBasketResult(ShoppingCartDto ShoppingCart);

// --- Validation
public class GetBasketQueryValidator : AbstractValidator<GetBasketQuery>
{
    public GetBasketQueryValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().WithMessage("UserName is required");
    }
}

// --- Handler
public class GetBasketHandler(IBasketRepository repository) : IQueryHandler<GetBasketQuery, GetBasketResult>
{
    public async Task<GetBasketResult> Handle(GetBasketQuery query, CancellationToken cancellationToken)
    {
        var basket = await repository.GetBasket(query.UserName, true, cancellationToken);

        var basketDto = basket.Adapt<ShoppingCartDto>();

        return new GetBasketResult(basketDto);
    }
}