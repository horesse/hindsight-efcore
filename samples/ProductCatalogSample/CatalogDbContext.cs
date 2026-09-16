using Hindsight;
using Microsoft.EntityFrameworkCore;

namespace ProductCatalogSample;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.IsTemporal(t => t.Exclude(p => p.UpdatedAt));
        });
    }
}
