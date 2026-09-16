using Hindsight;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ProductCatalogSample;

/// <summary>
/// Used by <c>dotnet ef</c> at design time. The connection string is never opened for
/// <c>migrations add</c>; it only needs to be syntactically valid for Npgsql.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=hindsight_sample;Username=postgres;Password=postgres")
            .UseHindsight()
            .Options;

        return new CatalogDbContext(options);
    }
}
