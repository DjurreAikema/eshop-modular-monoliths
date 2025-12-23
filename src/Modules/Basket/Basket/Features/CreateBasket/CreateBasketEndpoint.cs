using System.Security.Claims;
using Basket.Basket.Dtos;
using Carter;
using Mapster;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Basket.Basket.Features.CreateBasket;

// --- Records
public record CreateBasketRequest(ShoppingCartDto ShoppingCart);

public record CreateBasketResponse(Guid Id);

// --- Endpoints
public class CreateBasketEndpoint : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/basket", async (CreateBasketRequest request, ISender sender, ClaimsPrincipal user) =>
            {
                var userName = user.Identity!.Name;
                var updatedShoppingCart = request.ShoppingCart with {UserName = userName!};

                var command = new CreateBasketCommand(updatedShoppingCart);

                var result = await sender.Send(command);

                var response = result.Adapt<CreateBasketResponse>();

                return Results.Created($"/basket/{response.Id}", response);
            })
            .Produces<CreateBasketResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("CreateBasket")
            .WithSummary("Create Basket")
            .WithDescription("Create Basket")
            .RequireAuthorization();
    }
}