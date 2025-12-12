namespace Catalog.Products.Models;

public class Product : Aggregate<Guid>
{
    public string Name { get; private set; } = null!;
    public List<string> Categories { get; private set; } = [];
    public string Description { get; private set; } = null!;
    public string ImageFile { get; private set; } = null!;
    public decimal Price { get; private set; }

    // --- Domain Methods
    public static Product Create(Guid id, string name, List<string> categories, string description, string imageFile, decimal price)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);

        var product = new Product
        {
            Id = id,
            Name = name,
            Categories = categories,
            Description = description,
            ImageFile = imageFile,
            Price = price
        };

        product.AddDomainEvent(new ProductCreatedEvent(product));

        return product;
    }

    public void Update(string name, List<string> categories, string description, string imageFile, decimal price)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);

        // Update Product entity fields
        Name = name;
        Categories = categories;
        Description = description;
        ImageFile = imageFile;

        if (Price != price)
        {
            Price = price;
            AddDomainEvent(new ProductPriceChangedEvent(this));
        }
    }
}