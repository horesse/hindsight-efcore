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

        var history = model.HistoryEntityType(typeof(Policy));

        Assert.Equal("policies_history", history.GetTableName());
        Assert.True(history.IsPropertyBag);
        Assert.True(history[HindsightAnnotationNames.IsHistoryTable] is true);
    }

    [Fact]
    public void History_entity_type_mirrors_entity_columns_and_skips_excluded_ones()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.Exclude(p => p.UpdatedAt)));

        var columns = model.HistoryEntityType(typeof(Policy)).GetProperties()
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

        var columns = model.HistoryEntityType(typeof(Policy)).GetProperties()
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

        var history = model.HistoryEntityType(typeof(Policy));
        var key = history.FindPrimaryKey();

        Assert.NotNull(key);
        Assert.Equal("history_id", Assert.Single(key.Properties).GetColumnName());
        Assert.Equal(ValueGenerated.OnAdd, history.GetProperty("history_id").ValueGenerated);
    }

    [Fact]
    public void History_entity_type_has_a_version_index_on_key_columns_and_period_start()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var index = Assert.Single(model.HistoryEntityType(typeof(Policy)).GetIndexes());

        Assert.Equal(["Id", "valid_from"], index.Properties.Select(p => p.GetColumnName()));
        Assert.Equal([false, true], index.IsDescending!);
    }

    [Fact]
    public void Custom_history_table_name_and_schema_are_honored()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t
            .UseHistoryTable("policy_versions", schema: "audit")));

        var history = model.HistoryEntityType(typeof(Policy));

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

    [Fact]
    public void Temporal_entity_with_an_owned_reference_is_rejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => BuildModel(b => b.Entity<PolicyWithOwnedAddress>(e =>
        {
            e.OwnsOne(p => p.BillingAddress);
            e.IsTemporal();
        })));

        Assert.Contains("owned or complex members", ex.Message);
    }

    [Fact]
    public void Temporal_entity_with_a_complex_property_is_rejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => BuildModel(b => b.Entity<PolicyWithComplexMoney>(e =>
        {
            e.ComplexProperty(p => p.Premium);
            e.IsTemporal();
        })));

        Assert.Contains("owned or complex members", ex.Message);
    }

    [Theory]
    [InlineData("history_id")]
    [InlineData("operation")]
    [InlineData("changed_by")]
    [InlineData("changed_by_name")]
    [InlineData("correlation_id")]
    [InlineData("reason")]
    [InlineData("extra")]
    public void Property_column_colliding_with_a_fixed_history_column_throws_with_a_clear_message(string column)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b =>
        {
            var entity = b.Entity<ReservedColumnEntity>();
            entity.Property(p => p.Value).HasColumnName(column);
            entity.IsTemporal();
        }));

        Assert.Contains($"'{nameof(ReservedColumnEntity)}'", ex.Message);
        Assert.Contains(nameof(ReservedColumnEntity.Value), ex.Message);
        Assert.Contains($"'{column}'", ex.Message);
    }

    [Theory]
    [InlineData("valid_from")]
    [InlineData("valid_to")]
    public void Property_column_colliding_with_the_default_period_column_throws_with_a_clear_message(string column)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b =>
        {
            var entity = b.Entity<ReservedColumnEntity>();
            entity.Property(p => p.Value).HasColumnName(column);
            entity.IsTemporal();
        }));

        Assert.Contains("period", ex.Message);
    }

    [Fact]
    public void Property_column_colliding_with_a_custom_period_column_throws_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b =>
        {
            var entity = b.Entity<ReservedColumnEntity>();
            entity.Property(p => p.Value).HasColumnName("effective_at");
            entity.IsTemporal(t => t.HasPeriodStart("effective_at"));
        }));

        Assert.Contains("period", ex.Message);
    }

    [Fact]
    public void Temporal_entity_with_its_entire_primary_key_excluded_throws_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.Exclude(p => p.Id))));

        Assert.Contains($"'{nameof(Policy)}'", ex.Message);
        Assert.Contains("primary key", ex.Message);
        Assert.Contains("Exclude(...)", ex.Message);
    }

    [Fact]
    public void Temporal_entity_with_a_composite_key_partially_excluded_does_not_throw()
    {
        var model = BuildModel(b => b.Entity<CompositeKeyPolicy>(e =>
        {
            e.HasKey(p => new { p.TenantId, p.Id });
            e.IsTemporal(t => t.Exclude(p => p.TenantId));
        }));

        var columns = model.HistoryEntityType(typeof(CompositeKeyPolicy)).GetProperties()
            .Select(p => p.GetColumnName())
            .ToList();

        // TenantId is excluded but Id still identifies the previous version, so this is allowed
        // (HistoryRowPlan's keyColumns already handles "some key columns present" correctly).
        Assert.Contains("Id", columns);
        Assert.DoesNotContain("TenantId", columns);
    }

    [Fact]
    public void Equal_period_start_and_end_column_names_throw_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b => b.Entity<Policy>().IsTemporal(t => t
            .HasPeriodStart("same")
            .HasPeriodEnd("same"))));

        Assert.Contains("period start and end columns", ex.Message);
    }

    [Fact]
    public void Excluded_property_colliding_with_a_fixed_history_column_does_not_throw()
    {
        var model = BuildModel(b =>
        {
            var entity = b.Entity<ReservedColumnEntity>();
            entity.Property(p => p.Value).HasColumnName("reason");
            entity.IsTemporal(t => t.Exclude(p => p.Value));
        });

        var columns = model.HistoryEntityType(typeof(ReservedColumnEntity)).GetProperties()
            .Select(p => p.GetColumnName())
            .ToList();

        // Only the fixed 'reason' context column exists; the excluded property never reached the
        // history table (MirrorEntityColumns skips excluded properties), so there was no real collision.
        Assert.Single(columns, c => c == "reason");
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

    [Table("composite_key_policies")]
    private sealed class CompositeKeyPolicy
    {
        public int TenantId { get; set; }
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private sealed class MotorPolicy : Policy
    {
        public string PlateNumber { get; set; } = "";
    }

    private sealed class Keyless
    {
        public string Name { get; set; } = "";
    }

    [Table("reserved_column_entities")]
    private sealed class ReservedColumnEntity
    {
        public int Id { get; set; }
        public string Value { get; set; } = "";
    }

    private enum PolicyStatus
    {
        Draft,
        Active,
    }

    private sealed class PolicyWithOwnedAddress
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public Address BillingAddress { get; set; } = new();
    }

    private sealed class Address
    {
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
    }

    private sealed class PolicyWithComplexMoney
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public Money Premium { get; set; }
    }

    private readonly record struct Money(decimal Amount, string Currency);

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
