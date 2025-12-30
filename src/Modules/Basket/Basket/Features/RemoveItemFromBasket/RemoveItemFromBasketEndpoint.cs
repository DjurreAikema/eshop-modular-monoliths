using Carter;
using Mapster;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Basket.Basket.Features.RemoveItemFromBasket;

// --- Records
public record RemoveItemFromBasketResponse(Guid Id);

// --- Endpoints
public class RemoveItemFromBasketEndpoint : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapDelete("/basket/{userName}/items/{productId}", async (
                [FromRoute] string userName,
                [FromRoute] Guid productId,
                ISender sender
            ) =>
            {
                var command = new RemoveItemFromBasketCommand(userName, productId);

                var result = await sender.Send(command);

                var response = result.Adapt<RemoveItemFromBasketResponse>();

                return Results.Ok(response);
            })
            .Produces<RemoveItemFromBasketResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("RemoveItemFromBasket")
            .WithSummary("Remove Item From Basket")
            .WithDescription("Remove Item From Basket");
        // .RequireAuthorization();
    }
}