using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Hindsight.Tests;

/// <summary>
/// DESIGN.md D6: a history column whose source property was removed from the entity must stay in the
/// model — nullable, tagged <see cref="HindsightAnnotationNames.Orphaned"/> — so the migrations differ
/// never emits a <c>DropColumn</c> on a history table (golden rule 3). The convention learns which
/// columns used to exist by reading the previous <see cref="IMigrationsAssembly.ModelSnapshot"/>.
/// </summary>
public sealed class OrphanedHistoryColumnTests
{
    [Fact]
    public void Column_removed_from_the_entity_is_kept_on_history_as_a_nullable_orphan()
    {
        var previous = BuildModel(b =>
        {
            b.Entity<Policy>().IsTemporal();
            b.Entity<Policy>().Property<string>("LegacyNote");
        });

        var current = BuildModel(
            b => b.Entity<Policy>().IsTemporal(),
            previousSnapshot: previous);

        var legacy = current.FindEntityType("policies_history")!.FindProperty("LegacyNote");

        Assert.NotNull(legacy);
        Assert.True(legacy.IsNullable);
        Assert.True(legacy[HindsightAnnotationNames.Orphaned] is true);
    }

    [Fact]
    public void Orphaned_column_keeps_the_store_type_it_had_in_the_snapshot()
    {
        var previous = BuildModel(b =>
        {
            b.Entity<Policy>().IsTemporal();
            b.Entity<Policy>().Property<string>("Payload").HasColumnType("jsonb");
        });

        var current = BuildModel(
            b => b.Entity<Policy>().IsTemporal(),
            previousSnapshot: previous);

        var payload = current.FindEntityType("policies_history")!.FindProperty("Payload");

        Assert.NotNull(payload);
        Assert.Equal("jsonb", payload.GetColumnType());
        Assert.True(payload[HindsightAnnotationNames.Orphaned] is true);
    }

    [Fact]
    public void Orphans_accumulate_across_successive_removals()
    {
        var v1 = BuildModel(b =>
        {
            b.Entity<Policy>().IsTemporal();
            b.Entity<Policy>().Property<string>("First");
            b.Entity<Policy>().Property<string>("Second");
        });

        var v2 = BuildModel(
            b =>
            {
                b.Entity<Policy>().IsTemporal();
                b.Entity<Policy>().Property<string>("Second");
            },
            previousSnapshot: v1);

        var v3 = BuildModel(
            b => b.Entity<Policy>().IsTemporal(),
            previousSnapshot: v2);

        var history = v3.FindEntityType("policies_history")!;
        Assert.True(history.FindProperty("First")![HindsightAnnotationNames.Orphaned] is true);
        Assert.True(history.FindProperty("Second")![HindsightAnnotationNames.Orphaned] is true);
    }

    [Fact]
    public void A_first_migration_with_no_prior_snapshot_orphans_nothing()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal(), previousSnapshot: null);

        var orphans = model.FindEntityType("policies_history")!.GetProperties()
            .Where(p => p[HindsightAnnotationNames.Orphaned] is true);

        Assert.Empty(orphans);
    }

    [Fact]
    public void A_column_still_backed_by_a_live_property_is_not_marked_orphan()
    {
        var previous = BuildModel(b => b.Entity<Policy>().IsTemporal());
        var current = BuildModel(b => b.Entity<Policy>().IsTemporal(), previousSnapshot: previous);

        var orphans = current.FindEntityType("policies_history")!.GetProperties()
            .Where(p => p[HindsightAnnotationNames.Orphaned] is true);

        Assert.Empty(orphans);
    }

    [Fact]
    public void The_differ_emits_no_drop_on_the_history_table_when_a_property_is_removed()
    {
        var previous = BuildModel(b =>
        {
            b.Entity<Policy>().IsTemporal();
            b.Entity<Policy>().Property<string>("LegacyNote");
        });

        StubMigrationsAssembly.Current.Value = new StubModelSnapshot(previous);
        try
        {
            using var db = new TestContext(b => b.Entity<Policy>().IsTemporal());
            var current = db.GetService<IDesignTimeModel>().Model;
            var differ = db.GetService<IMigrationsModelDiffer>();

            var operations = differ.GetDifferences(
                previous.GetRelationalModel(), current.GetRelationalModel());

            Assert.Contains(
                operations.OfType<DropColumnOperation>(),
                op => op.Table == "policies" && op.Name == "LegacyNote");
            Assert.DoesNotContain(
                operations.OfType<DropColumnOperation>(), op => op.Table == "policies_history");
            Assert.DoesNotContain(
                operations.OfType<DropTableOperation>(), op => op.Name == "policies_history");
            Assert.DoesNotContain(
                operations.OfType<AlterColumnOperation>(), op => op.Table == "policies_history");
        }
        finally
        {
            StubMigrationsAssembly.Current.Value = null;
        }
    }

    [Fact]
    public void Removing_a_primary_key_property_from_a_temporal_entity_throws()
    {
        var previous = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(
            b =>
            {
                b.Entity<Policy>().Ignore(p => p.Id).HasKey(p => p.Number);
                b.Entity<Policy>().IsTemporal();
            },
            previousSnapshot: previous));

        Assert.Contains("primary key", ex.Message);
        Assert.Contains("Id", ex.Message);
    }

    private static IModel BuildModel(Action<ModelBuilder> configure, IModel? previousSnapshot = null)
    {
        StubMigrationsAssembly.Current.Value =
            previousSnapshot is null ? null : new StubModelSnapshot(previousSnapshot);

        try
        {
            using var db = new TestContext(configure);
            return db.GetService<IDesignTimeModel>().Model;
        }
        finally
        {
            StubMigrationsAssembly.Current.Value = null;
        }
    }

    [Table("policies")]
    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public PolicyStatus Status { get; set; }
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
                .ReplaceService<IMigrationsAssembly, StubMigrationsAssembly>()
                .ReplaceService<IModelCacheKeyFactory, UniquePerContextModelCacheKeyFactory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => _configure(modelBuilder);
    }

    // Feeds the convention a caller-supplied snapshot model instead of a real migrations assembly.
    private sealed class StubMigrationsAssembly : IMigrationsAssembly
    {
        public static readonly AsyncLocal<StubModelSnapshot?> Current = new();

        public IReadOnlyDictionary<string, TypeInfo> Migrations { get; } = new Dictionary<string, TypeInfo>();

        public ModelSnapshot? ModelSnapshot => Current.Value;

        public Assembly Assembly => typeof(StubMigrationsAssembly).Assembly;

        public string? FindMigrationId(string nameOrId) => null;

        public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
            => throw new NotSupportedException();
    }

    private sealed class StubModelSnapshot(IModel model) : ModelSnapshot
    {
        public override IModel Model { get; } = model;

        protected override void BuildModel(ModelBuilder modelBuilder)
        {
        }
    }

    private sealed class UniquePerContextModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => Guid.NewGuid();
    }
}
