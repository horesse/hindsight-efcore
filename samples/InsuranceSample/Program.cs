using Hindsight;
using InsuranceSample;
using Microsoft.EntityFrameworkCore;

// `dotnet ef migrations add Initial` uses this connection string at design time; nothing is
// connected to at runtime until you point it at a real database.
var connectionString = Environment.GetEnvironmentVariable("HINDSIGHT_SAMPLE_CS")
    ?? "Host=localhost;Database=hindsight_sample;Username=postgres;Password=postgres";

var options = new DbContextOptionsBuilder<InsuranceDbContext>()
    .UseNpgsql(connectionString)
    .UseHindsight()
    .Options;

await using var db = new InsuranceDbContext(options);

Console.WriteLine("Model:");
foreach (var entityType in db.Model.GetEntityTypes())
{
    Console.WriteLine($"  {entityType.DisplayName()} -> {entityType.GetSchema()}.{entityType.GetTableName()}");
}
