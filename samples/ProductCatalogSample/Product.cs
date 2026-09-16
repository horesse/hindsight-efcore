namespace ProductCatalogSample;

public sealed class Product
{
    public int Id { get; set; }
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }

    /// <summary>Excluded from versioning — changes to this property alone do not create a history row.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
