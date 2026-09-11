using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hindsight.Tests;

/// <summary>
/// DESIGN.md D6/D13: in <see cref="HistoryWriter.Trigger"/> mode the physical trigger lives on the main
/// table, not the history table, so orphaning the history entity type unchanged (golden rule 3) leaves
/// nothing in the model diff to hang a <c>DROP FUNCTION</c> on. <c>HistoryEntityTypeConvention</c> instead
/// signals the transition itself, once, via <see cref="HindsightAnnotationNames.OrphanedTriggerPending"/>,
/// and <c>HindsightMigrationsSqlGenerator</c> reads it back to emit the drop. See the real end-to-end
/// Testcontainers version in <c>Hindsight.IntegrationTests.OrphanedHistoryTriggerTests</c>.
/// </summary>
public sealed class OrphanedHistoryTriggerTests
{
    [Fact]
    public void Detemporalizing_an_entity_marks_its_history_table_trigger_pending_for_drop()
    {
        var v1 = BuildModel(b => b.Entity<Policy>().IsTemporal());
        var v2 = BuildModel(b => { }, previousSnapshot: v1);

        var history = v2.FindEntityType("policies_history")!;

        Assert.True(history[HindsightAnnotationNames.Orphaned] is true);
        Assert.True(history[HindsightAnnotationNames.OrphanedTriggerPending] is true);
    }

    [Fact]
    public void The_pending_flag_is_not_set_again_on_a_later_migration()
    {
        var v1 = BuildModel(b => b.Entity<Policy>().IsTemporal());
        var v2 = BuildModel(b => { }, previousSnapshot: v1);
        var v3 = BuildModel(b => { }, previousSnapshot: v2);

        var history = v3.FindEntityType("policies_history")!;

        Assert.True(history[HindsightAnnotationNames.Orphaned] is true);
        Assert.True(history[HindsightAnnotationNames.OrphanedTriggerPending] is not true);
    }

    [Fact]
    public void A_still_temporal_entity_never_gets_the_pending_flag()
    {
        var v1 = BuildModel(b => b.Entity<Policy>().IsTemporal());
        var v2 = BuildModel(b => b.Entity<Policy>().IsTemporal(), previousSnapshot: v1);

        var history = v2.FindEntityType("policies_history")!;

        Assert.True(history[HindsightAnnotationNames.Orphaned] is not true);
        Assert.True(history[HindsightAnnotationNames.OrphanedTriggerPending] is not true);
    }

    [Fact]
    public void The_generator_drops_the_function_exactly_on_the_transition_migration()
    {
        StubMigrationsAssembly.Current.Value = null;
        try
        {
            using var v1 = new TestContext(b => b.Entity<Policy>().IsTemporal());
            var v1Model = v1.GetService<IDesignTimeModel>().Model;

            StubMigrationsAssembly.Current.Value = new StubModelSnapshot(v1Model);
            using var v2 = new TestContext(b => { });
            var v2Model = v2.GetService<IDesignTimeModel>().Model;
            var v2Commands = v2.GetService<IMigrationsSqlGenerator>()
                .Generate(
                    v2.GetService<IMigrationsModelDiffer>().GetDifferences(
                        v1Model.GetRelationalModel(), v2Model.GetRelationalModel()),
                    v2Model)
                .Select(c => c.CommandText)
                .ToList();

            Assert.Contains(v2Commands, text =>
                text.Contains("DROP FUNCTION", StringComparison.Ordinal)
                && text.Contains("policies_history_write", StringComparison.Ordinal));

            StubMigrationsAssembly.Current.Value = new StubModelSnapshot(v2Model);
            using var v3 = new TestContext(b => { });
            var v3Model = v3.GetService<IDesignTimeModel>().Model;
            var v3Commands = v3.GetService<IMigrationsSqlGenerator>()
                .Generate(
                    v3.GetService<IMigrationsModelDiffer>().GetDifferences(
                        v2Model.GetRelationalModel(), v3Model.GetRelationalModel()),
                    v3Model)
                .Select(c => c.CommandText)
                .ToList();

            Assert.DoesNotContain(v3Commands, text =>
                text.Contains("DROP FUNCTION", StringComparison.Ordinal)
                && text.Contains("policies_history_write", StringComparison.Ordinal));
        }
        finally
        {
            StubMigrationsAssembly.Current.Value = null;
        }
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

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private sealed class TestContext(Action<ModelBuilder> configure) : DbContext
    {
        private readonly Action<ModelBuilder> _configure = configure;

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options
                .UseNpgsql("Host=localhost;Database=unused")
                .UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger))
                .ReplaceService<IMigrationsAssembly, StubMigrationsAssembly>()
                .ReplaceService<IModelCacheKeyFactory, UniquePerContextModelCacheKeyFactory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");

            _configure(modelBuilder);
        }
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
