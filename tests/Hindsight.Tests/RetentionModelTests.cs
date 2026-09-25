using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Hindsight.Migrations;
using Hindsight.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Hindsight.Tests;

/// <summary>
/// DESIGN.md D19, the model side of retention: <c>WithRetention()</c> adds the retention-horizon table to
/// the model (and only then), the table is never dropped or moved once a migration recorded it, and an
/// entity that was configured with it cannot silently lose it.
/// </summary>
public sealed class RetentionModelTests
{
    [Fact]
    public void Model_without_WithRetention_has_no_horizon_table()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        Assert.Null(model.FindEntityType(RetentionSqlGenerator.EntityName));
    }

    [Fact]
    public void WithRetention_adds_the_horizon_table_keyed_by_history_entity()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.WithRetention()));

        var horizon = model.FindEntityType(RetentionSqlGenerator.EntityName);
        Assert.NotNull(horizon);
        Assert.Equal(RetentionSqlGenerator.TableName, horizon.GetTableName());
        Assert.Equal([RetentionSqlGenerator.HistoryEntityColumn], horizon.FindPrimaryKey()!.Properties.Select(p => p.GetColumnName()));
        Assert.Equal("timestamp with time zone", horizon.FindProperty(RetentionSqlGenerator.HorizonColumn)!.GetColumnType());
        Assert.False(horizon.FindProperty(RetentionSqlGenerator.HorizonColumn)!.IsNullable);
        Assert.True(model.FindEntityType(typeof(Policy))![HindsightAnnotationNames.HasRetention] is true);
    }

    [Fact]
    public void Horizon_table_and_guard_function_follow_the_default_schema()
    {
        var model = BuildModel(b =>
        {
            b.HasDefaultSchema("audit");
            b.Entity<Policy>().IsTemporal(t => t.WithRetention());
        });

        Assert.Equal("audit", model.FindEntityType(RetentionSqlGenerator.EntityName)!.GetSchema());
        Assert.Equal("audit", model.FindDbFunction(RetentionGuardFunction.Method)!.Schema);
    }

    [Fact]
    public void Adding_WithRetention_to_a_temporal_entity_creates_only_the_horizon_table()
    {
        var previous = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var operations = Diff(previous, b => b.Entity<Policy>().IsTemporal(t => t.WithRetention()));

        var create = Assert.Single(operations.OfType<CreateTableOperation>());
        Assert.Equal(RetentionSqlGenerator.TableName, create.Name);
        Assert.DoesNotContain(operations, op => op is DropTableOperation or DropColumnOperation or AlterColumnOperation);
    }

    [Fact]
    public void Removing_WithRetention_after_a_migration_recorded_it_throws()
    {
        var previous = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.WithRetention()));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BuildModel(b => b.Entity<Policy>().IsTemporal(), previousSnapshot: previous));

        Assert.Contains("Policy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithRetention()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Horizon_table_is_kept_when_no_entity_uses_retention_any_more()
    {
        // The only entity with retention stops being temporal altogether: its history table is kept
        // (DESIGN.md D6), and so is the record of what was pruned from it.
        var previous = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.WithRetention()));

        var operations = Diff(previous, b => b.Entity<Policy>());

        Assert.DoesNotContain(operations, op => op is DropTableOperation);
        Assert.DoesNotContain(operations, op => op is AlterTableOperation or RenameTableOperation);
    }

    [Fact]
    public void Changing_the_default_schema_moves_the_horizon_table_and_recreates_the_guard_function_there()
    {
        var previous = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.WithRetention()));

        var sql = Generate(previous, b =>
        {
            b.HasDefaultSchema("audit");
            b.Entity<Policy>().IsTemporal(t => t.WithRetention());
        });

        var move = sql.FindIndex(text => text.Contains("ALTER TABLE hindsight_retention_horizon SET SCHEMA audit", StringComparison.Ordinal));
        var drop = sql.FindIndex(text => text.StartsWith("DROP FUNCTION IF EXISTS hindsight_history_retained(", StringComparison.Ordinal));
        var create = sql.FindIndex(text => text.Contains("CREATE OR REPLACE FUNCTION audit.hindsight_history_retained(", StringComparison.Ordinal));
        Assert.True(move >= 0 && drop > move && create > drop, string.Join("\n---\n", sql));
    }

    private static IReadOnlyList<MigrationOperation> Diff(IModel previous, Action<ModelBuilder> configure)
        => WithSnapshot(previous, configure, (db, current) => db.GetService<IMigrationsModelDiffer>().GetDifferences(
            previous.GetRelationalModel(), current.GetRelationalModel()));

    private static List<string> Generate(IModel previous, Action<ModelBuilder> configure)
        => WithSnapshot(previous, configure, (db, current) =>
        {
            var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(
                previous.GetRelationalModel(), current.GetRelationalModel());
            return db.GetService<IMigrationsSqlGenerator>().Generate(operations, current)
                .Select(command => command.CommandText.Trim())
                .ToList();
        });

    private static T WithSnapshot<T>(IModel previous, Action<ModelBuilder> configure, Func<DbContext, IModel, T> use)
    {
        StubMigrationsAssembly.Current.Value = new StubModelSnapshot(previous);
        try
        {
            using var db = new TestContext(configure);
            return use(db, db.GetService<IDesignTimeModel>().Model);
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

    [Table("policies")]
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
