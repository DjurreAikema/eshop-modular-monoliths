using Mapster;
using Microsoft.EntityFrameworkCore;
using Ordering.Data;
using Ordering.Orders.Dtos;
using Ordering.Orders.Exceptions;
using Shared.Contracts.CQRS;

namespace Ordering.Orders.Features.GetOrderById;

// --- Records
public record GetOrderByIdQuery(Guid Id) : IQuery<GetOrderByIdResult>;

public record GetOrderByIdResult(OrderDto Order);

// --- Handler
public class GetOrderByIdHandler(OrderingDbContext dbContext) : IQueryHandler<GetOrderByIdQuery, GetOrderByIdResult>
{
    public async Task<GetOrderByIdResult> Handle(GetOrderByIdQuery query, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Include(x => x.Items)
            .SingleOrDefaultAsync(p => p.Id == query.Id, cancellationToken: cancellationToken);

        if (order is null)
        {
            throw new OrderNotFoundException(query.Id);
        }

        var orderDto = order.Adapt<OrderDto>();

        return new GetOrderByIdResult(orderDto);
    }
}