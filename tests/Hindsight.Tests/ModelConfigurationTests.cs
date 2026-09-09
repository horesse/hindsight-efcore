using Microsoft.EntityFrameworkCore;

namespace Hindsight.Tests;

public sealed class ModelConfigurationTests
{
    [Fact]
    public void IsTemporal_WritesAnnotations()
    {
        using var db = new TestContext();

        var entity = db.Model.FindEntityType(typeof(Policy))!;

        Assert.True(entity[HindsightAnnotationNames.IsTemporal] is true);
        Assert.Equal("policies_history", entity[HindsightAnnotationNames.HistoryTableName]);
        Assert.Equal("audit", entity[HindsightAnnotationNames.HistoryTableSchema]);
        Assert.True(entity.FindProperty(nameof(Policy.UpdatedAt))![HindsightAnnotationNames.IsExcluded] is true);
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseNpgsql("Host=localhost;Database=unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Policy>().IsTemporal(t => t
                .UseHistoryTable("policies_history", schema: "audit")
                .Exclude(p => p.UpdatedAt));
    }
}
