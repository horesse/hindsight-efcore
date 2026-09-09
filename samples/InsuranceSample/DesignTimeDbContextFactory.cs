using Hindsight;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InsuranceSample;

/// <summary>
/// Used by <c>dotnet ef</c> at design time. The connection string is never opened for
/// <c>migrations add</c>; it only needs to be syntactically valid for Npgsql.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<InsuranceDbContext>
{
    public InsuranceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("HINDSIGHT_SAMPLE_CS")
                               ?? "Host=localhost;Database=hindsight_sample;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<InsuranceDbContext>()
            .UseNpgsql(connectionString)
            .UseHindsight()
            .Options;

        return new InsuranceDbContext(options);
    }
}
