using Basket.Basket.Dtos;
using Basket.Basket.Exceptions;
using Basket.Data;
using FluentValidation;
using Mapster;
using Microsoft.EntityFrameworkCore;
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
public class GetBasketHandler(BasketDbContext dbContext) : IQueryHandler<GetBasketQuery, GetBasketResult>
{
    public async Task<GetBasketResult> Handle(GetBasketQuery query, CancellationToken cancellationToken)
    {
        var basket = await dbContext.ShoppingCarts
            .AsNoTracking()
            .Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.UserName == query.UserName, cancellationToken);

        if (basket is null)
        {
            throw new BasketNotFoundException(query.UserName);
        }

        var basketDto = basket.Adapt<ShoppingCartDto>();

        return new GetBasketResult(basketDto);
    }
}