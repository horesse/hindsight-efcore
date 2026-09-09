using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight.Tests;

public sealed class HistoryEntityTypeConventionTests
{
    [Fact]
    public void UseHindsight_adds_a_history_entity_type_mapped_to_the_history_table()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var history = model.FindEntityType("policies_history");

        Assert.NotNull(history);
        Assert.Equal("policies_history", history.GetTableName());
        Assert.True(history.IsPropertyBag);
        Assert.True(history[HindsightAnnotationNames.IsHistoryTable] is true);
        Assert.Equal("policies_history", model.FindEntityType(typeof(Policy))![HindsightAnnotationNames.HistoryEntityType]);
    }

    [Fact]
    public void History_entity_type_mirrors_entity_columns_and_skips_excluded_ones()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.Exclude(p => p.UpdatedAt)));

        var columns = model.FindEntityType("policies_history")!.GetProperties()
            .Select(p => p.GetColumnName())
            .ToList();

        Assert.Contains("Number", columns);
        Assert.Contains("Status", columns);
        Assert.DoesNotContain("UpdatedAt", columns);
    }

    [Fact]
    public void History_entity_type_has_the_period_and_context_columns()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var columns = model.FindEntityType("policies_history")!.GetProperties()
            .ToDictionary(p => p.GetColumnName()!, p => p);

        Assert.False(columns["valid_from"].IsNullable);
        Assert.False(columns["valid_to"].IsNullable);
        Assert.False(columns["operation"].IsNullable);
        Assert.True(columns["changed_by"].IsNullable);
        Assert.True(columns["changed_by_name"].IsNullable);
        Assert.True(columns["correlation_id"].IsNullable);
        Assert.True(columns["reason"].IsNullable);
        Assert.Equal("jsonb", columns["extra"].GetColumnType());
    }

    [Fact]
    public void History_entity_type_has_a_surrogate_key_on_history_id()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var history = model.FindEntityType("policies_history")!;
        var key = history.FindPrimaryKey();

        Assert.NotNull(key);
        Assert.Equal("history_id", Assert.Single(key.Properties).GetColumnName());
        Assert.Equal(ValueGenerated.OnAdd, history.GetProperty("history_id").ValueGenerated);
    }

    [Fact]
    public void History_entity_type_has_a_version_index_on_key_columns_and_period_start()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var index = Assert.Single(model.FindEntityType("policies_history")!.GetIndexes());

        Assert.Equal(["Id", "valid_from"], index.Properties.Select(p => p.GetColumnName()));
        Assert.Equal([false, true], index.IsDescending!);
    }

    [Fact]
    public void Custom_history_table_name_and_schema_are_honored()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t
            .UseHistoryTable("policy_versions", schema: "audit")));

        var history = model.FindEntityType("policy_versions")!;

        Assert.Equal("policy_versions", history.GetTableName());
        Assert.Equal("audit", history.GetSchema());
    }

    [Fact]
    public void Temporal_entity_without_a_primary_key_throws_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildModel(b => b.Entity<Keyless>().HasNoKey().ToTable("keyless").IsTemporal()));

        Assert.Contains("has no primary key", ex.Message);
    }

    [Fact]
    public void Temporal_tph_hierarchy_is_rejected()
    {
        Assert.Throws<NotSupportedException>(() => BuildModel(b =>
        {
            b.Entity<Policy>().IsTemporal();
            b.Entity<MotorPolicy>();
        }));
    }

    private static IModel BuildModel(Action<ModelBuilder> configure)
    {
        using var db = new TestContext(configure);
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Table("policies")]
    private class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public PolicyStatus Status { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class MotorPolicy : Policy
    {
        public string PlateNumber { get; set; } = "";
    }

    private sealed class Keyless
    {
        public string Name { get; set; } = "";
    }

    private enum PolicyStatus
    {
        Draft,
        Active,
    }

    private sealed class TestContext(Action<ModelBuilder> configure) : DbContext
    {
        private readonly Action<ModelBuilder> _configure = configure;

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options
                .UseNpgsql("Host=localhost;Database=unused")
                .UseHindsight()
                .ReplaceService<IModelCacheKeyFactory, UniquePerContextModelCacheKeyFactory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => _configure(modelBuilder);
    }

    // Each context instance carries a different model configuration; never share a cached model.
    private sealed class UniquePerContextModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => Guid.NewGuid();
    }
}
