using System.Text.Json.Serialization;
using Shared.DDD;

namespace Basket.Basket.Models;

public class ShoppingCartItem : Entity<Guid>
{
    // --- Properties
    public Guid ShoppingCartId { get; private set; } = Guid.Empty;
    public Guid ProductId { get; private set; } = Guid.Empty;
    public int Quantity { get; internal set; } = 0;
    public string Color { get; private set; } = null!;

    // Comes from Catalog module
    public decimal Price { get; private set; } = 0;
    public string ProductName { get; private set; } = null!;

    // --- Constructors
    internal ShoppingCartItem(Guid shoppingCartId, Guid productId, int quantity, string color, decimal price, string productName)
    {
        ShoppingCartId = shoppingCartId;
        ProductId = productId;
        Quantity = quantity;
        Color = color;
        Price = price;
        ProductName = productName;
    }

    [JsonConstructor]
    internal ShoppingCartItem(Guid id, Guid shoppingCartId, Guid productId, int quantity, string color, decimal price, string productName)
    {
        Id = id;
        ShoppingCartId = shoppingCartId;
        ProductId = productId;
        Quantity = quantity;
        Color = color;
        Price = price;
        ProductName = productName;
    }

    // --- Methods
    public void UpdatePrice(decimal newPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newPrice);

        Price = newPrice;
    }
}