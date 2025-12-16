using MediatR;
using Microsoft.Extensions.Logging;

namespace Catalog.Products.EventHandlers;

public class ProductPriceChangedEventHandler(ILogger<ProductPriceChangedEventHandler> logger) : INotificationHandler<ProductPriceChangedEvent>
{
    public Task Handle(ProductPriceChangedEvent notification, CancellationToken cancellationToken)
    {
        logger.LogInformation("Doman event handeled: {DomainEvent}", notification.GetType().Name);
        return Task.CompletedTask;
    }
}