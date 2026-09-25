using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Hindsight.Tests;

/// <summary>
/// DESIGN.md D6: what a migration does, entity-by-entity, as a temporal entity's shape evolves across
/// successive <see cref="IMigrationsAssembly.ModelSnapshot"/> builds. <see cref="OrphanedHistoryColumnTests"/>
/// and <see cref="OrphanedHistoryTriggerTests"/> already cover "remove a property" and "remove IsTemporal()
/// entirely"; this file rounds out the remaining scenarios in that table: add, rename, the rejected
/// type/precision/converter change, becoming temporal for the first time, and composite keys.
/// </summary>
public sealed class SchemaEvolutionModelTests
{
    [Fact]
    public void Adding_a_property_adds_the_column_to_both_tables_and_orphans_nothing()
    {
        var previous = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var current = BuildModel(
            b =>
            {
                b.Entity<Policy>().IsTemporal();
                b.Entity<Policy>().Property<string>("Note");
            },
            previousSnapshot: previous);

        var history = current.HistoryEntityType(typeof(Policy));
        var note = history.FindProperty("Note");

        Assert.NotNull(note);
        Assert.True(note.IsNullable);
        Assert.True(note[HindsightAnnotationNames.Orphaned] is not true);
    }

    [Fact]
    public void The_differ_emits_a_plain_add_column_on_both_tables_for_a_new_property()
    {
        var previous = BuildModel(b => b.Entity<Policy>().IsTemporal());

        StubMigrationsAssembly.Current.Value = new StubModelSnapshot(previous);
        try
        {
            using var db = new TestContext(b =>
            {
                b.Entity<Policy>().IsTemporal();
                b.Entity<Policy>().Property<string>("Note");
            });
            var current = db.GetService<IDesignTimeModel>().Model;
            var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(
                previous.GetRelationalModel(), current.GetRelationalModel());

            Assert.Contains(
                operations.OfType<AddColumnOperation>(), op => op.Table == "policies" && op.Name == "Note");
            Assert.Contains(
                operations.OfType<AddColumnOperation>(), op => op.Table == "policies_history" && op.Name == "Note");
            Assert.DoesNotContain(operations, op => op is DropColumnOperation or AlterColumnOperation);
        }
        finally
        {
            StubMigrationsAssembly.Current.Value = null;
        }
    }

    [Fact]
    public void Renaming_a_property_orphans_the_old_column_and_adds_a_clean_new_one()
    {
        var previous = BuildModel(b =>
        {
            var e = b.Entity<Policy>();
            e.Property<string>("OldName").HasColumnName("old_name");
            e.IsTemporal();
        });

        var current = BuildModel(
            b =>
            {
                var e = b.Entity<Policy>();
                e.Property<string>("NewName").HasColumnName("new_name");
                e.IsTemporal();
            },
            previousSnapshot: previous);

        var history = current.HistoryEntityType(typeof(Policy));
        var oldColumn = history.FindProperty("old_name");
        var newColumn = history.FindProperty("new_name");

        Assert.NotNull(oldColumn);
        Assert.True(oldColumn.IsNullable);
        Assert.True(oldColumn[HindsightAnnotationNames.Orphaned] is true);

        Assert.NotNull(newColumn);
        Assert.True(newColumn[HindsightAnnotationNames.Orphaned] is not true);
    }

    [Fact]
    public void The_differ_emits_no_rename_column_on_history_for_a_renamed_property()
    {
        var previous = BuildModel(b =>
        {
            var e = b.Entity<Policy>();
            e.Property<string>("OldName").HasColumnName("old_name");
            e.IsTemporal();
        });

        StubMigrationsAssembly.Current.Value = new StubModelSnapshot(previous);
        try
        {
            using var db = new TestContext(b =>
            {
                var e = b.Entity<Policy>();
                e.Property<string>("NewName").HasColumnName("new_name");
                e.IsTemporal();
            });
            var current = db.GetService<IDesignTimeModel>().Model;
            var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(
                previous.GetRelationalModel(), current.GetRelationalModel());

            // The main table gets EF Core's own rename detection; the history table must not — D6 says
            // "no rename magic" there, so it should see an unrelated add (new_name) plus nothing dropped
            // or altered for old_name, never a RenameColumnOperation for the history table.
            Assert.DoesNotContain(
                operations.OfType<RenameColumnOperation>(), op => op.Table == "policies_history");
            Assert.Contains(
                operations.OfType<AddColumnOperation>(), op => op.Table == "policies_history" && op.Name == "new_name");
            Assert.DoesNotContain(
                operations.OfType<DropColumnOperation>(), op => op.Table == "policies_history");
            Assert.DoesNotContain(
                operations.OfType<AlterColumnOperation>(), op => op.Table == "policies_history");
        }
        finally
        {
            StubMigrationsAssembly.Current.Value = null;
        }
    }

    [Fact]
    public void Changing_a_columns_store_type_under_the_same_name_throws_with_a_clear_message()
    {
        var previous = BuildModel(b =>
        {
            var e = b.Entity<Policy>();
            e.Property<string>("Payload").HasColumnName("payload").HasColumnType("text");
            e.IsTemporal();
        });

        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(
            b =>
            {
                var e = b.Entity<Policy>();
                e.Property<string>("Payload").HasColumnName("payload").HasColumnType("jsonb");
                e.IsTemporal();
            },
            previousSnapshot: previous));

        Assert.Contains($"'{nameof(Policy)}'", ex.Message);
        Assert.Contains("Payload", ex.Message);
        Assert.Contains("payload", ex.Message);
        Assert.Contains("HasColumnName(", ex.Message);
    }

    [Fact]
    public void Changing_a_columns_precision_under_the_same_name_throws()
    {
        var previous = BuildModel(b =>
        {
            var e = b.Entity<Policy>();
            e.Property<decimal>("Premium").HasColumnName("premium").HasPrecision(18, 4);
            e.IsTemporal();
        });

        Assert.Throws<InvalidOperationException>(() => BuildModel(
            b =>
            {
                var e = b.Entity<Policy>();
                e.Property<decimal>("Premium").HasColumnName("premium").HasPrecision(10, 2);
                e.IsTemporal();
            },
            previousSnapshot: previous));
    }

    [Fact]
    public void Changing_a_columns_value_converter_under_the_same_name_throws()
    {
        var previous = BuildModel(b =>
        {
            var e = b.Entity<Policy>();
            e.Property(p => p.Status).HasConversion<string>();
            e.IsTemporal();
        });

        Assert.Throws<InvalidOperationException>(() => BuildModel(
            b =>
            {
                var e = b.Entity<Policy>();
                e.Property(p => p.Status).HasConversion<int>();
                e.IsTemporal();
            },
            previousSnapshot: previous));
    }

    [Fact]
    public void Widening_a_columns_precision_under_the_same_name_still_throws()
    {
        // Golden rule 3 forbids any AlterColumn on a history table, not only a narrowing one — even a
        // widening precision change is rejected, since the guard cannot tell widening from narrowing
        // apart from EF facets alone and either one is an in-place ALTER on physical history rows.
        var previous = BuildModel(b =>
        {
            var e = b.Entity<Policy>();
            e.Property<decimal>("Premium").HasColumnName("premium").HasPrecision(10, 2);
            e.IsTemporal();
        });

        Assert.Throws<InvalidOperationException>(() => BuildModel(
            b =>
            {
                var e = b.Entity<Policy>();
                e.Property<decimal>("Premium").HasColumnName("premium").HasPrecision(18, 4);
                e.IsTemporal();
            },
            previousSnapshot: previous));
    }

    [Fact]
    public void An_unconfigured_columns_default_type_across_two_builds_does_not_throw()
    {
        // Regression: a plain, never-explicitly-typed column (the common case) must not be flagged just
        // because GetColumnType() only resolves to its default once the model is fully built — see the
        // long comment on HistoryEntityTypeConvention.StoreFacetsMatch.
        var previous = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var current = BuildModel(b => b.Entity<Policy>().IsTemporal(), previousSnapshot: previous);

        Assert.NotNull(current.HistoryEntityType(typeof(Policy)));
    }

    [Fact]
    public void Making_a_previously_non_temporal_entity_temporal_creates_its_history_table()
    {
        var previous = BuildModel(b => b.Entity<Policy>().ToTable("policies"));

        var current = BuildModel(b => b.Entity<Policy>().IsTemporal(), previousSnapshot: previous);

        var history = current.HistoryEntityType(typeof(Policy));
        Assert.Equal("policies_history", history.GetTableName());
    }

    [Fact]
    public void The_differ_emits_a_plain_create_table_for_a_newly_temporal_entity()
    {
        var previous = BuildModel(b => b.Entity<Policy>().ToTable("policies"));

        StubMigrationsAssembly.Current.Value = new StubModelSnapshot(previous);
        try
        {
            using var db = new TestContext(b => b.Entity<Policy>().IsTemporal());
            var current = db.GetService<IDesignTimeModel>().Model;
            var operations = db.GetService<IMigrationsModelDiffer>().GetDifferences(
                previous.GetRelationalModel(), current.GetRelationalModel());

            Assert.Contains(operations.OfType<CreateTableOperation>(), op => op.Name == "policies_history");
            Assert.DoesNotContain(operations, op => op is DropTableOperation);
        }
        finally
        {
            StubMigrationsAssembly.Current.Value = null;
        }
    }

    [Fact]
    public void Composite_key_entity_removing_a_non_key_property_orphans_it_and_keeps_both_key_columns_indexed()
    {
        var previous = BuildModel(b =>
        {
            var e = b.Entity<CompositeKeyPolicy>();
            e.HasKey(p => new { p.TenantId, p.Id });
            e.Property<string>("Note");
            e.IsTemporal();
        });

        var current = BuildModel(
            b =>
            {
                var e = b.Entity<CompositeKeyPolicy>();
                e.HasKey(p => new { p.TenantId, p.Id });
                e.IsTemporal();
            },
            previousSnapshot: previous);

        var history = current.HistoryEntityType(typeof(CompositeKeyPolicy));
        var note = history.FindProperty("Note");
        Assert.NotNull(note);
        Assert.True(note[HindsightAnnotationNames.Orphaned] is true);

        var index = Assert.Single(history.GetIndexes());
        Assert.Equal(
            ["TenantId", "Id", "valid_from"],
            index.Properties.Select(p => p.GetColumnName()));
    }

    [Fact]
    public void Composite_key_entity_removing_part_of_the_key_throws()
    {
        var previous = BuildModel(b =>
        {
            var e = b.Entity<CompositeKeyPolicy>();
            e.HasKey(p => new { p.TenantId, p.Id });
            e.IsTemporal();
        });

        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(
            b =>
            {
                var e = b.Entity<CompositeKeyPolicy>();
                e.Ignore(p => p.TenantId);
                e.HasKey(p => p.Id);
                e.IsTemporal();
            },
            previousSnapshot: previous));

        Assert.Contains("primary key", ex.Message);
    }

    // A real ModelSnapshot is generated C#, and it declares every property by its *provider* CLR type:
    // an enum with no converter is written as `Property<int>`, an Npgsql LTree as `Property<string>`
    // with HasColumnType("ltree") — the model-level CLR type and the converter that bridges it are gone.
    // The previous-snapshot check must see such a column as unchanged, or the second `migrations add`
    // after IsTemporal() fails on a column nobody touched.
    [Theory]
    [InlineData(typeof(PolicyStatus), typeof(int), "integer")]
    [InlineData(typeof(LTree), typeof(string), "ltree")]
    public void A_column_mapped_through_a_provider_type_converter_is_unchanged_against_a_generated_snapshot(
        Type modelType, Type snapshotType, string storeType)
    {
        var current = BuildModel(
            b =>
            {
                var e = b.Entity<Policy>();
                e.Property(modelType, "Tag").HasColumnName("tag");
                e.IsTemporal();
            },
            previousSnapshot: new GeneratedStyleSnapshot(("tag", snapshotType, storeType)));

        var history = current.HistoryEntityType(typeof(Policy));
        Assert.NotNull(history.FindProperty("tag"));
        Assert.True(history.FindProperty("tag")![HindsightAnnotationNames.Orphaned] is not true);
    }

    [Fact]
    public void A_real_store_type_change_is_still_rejected_against_a_generated_snapshot()
    {
        Assert.Throws<InvalidOperationException>(() => BuildModel(
            b =>
            {
                var e = b.Entity<Policy>();
                e.Property<LTree>("Tag").HasColumnName("tag");
                e.IsTemporal();
            },
            previousSnapshot: new GeneratedStyleSnapshot(("tag", typeof(int), "integer"))));
    }

    private static IModel BuildModel(Action<ModelBuilder> configure, IModel? previousSnapshot = null)
        => BuildModel(configure, previousSnapshot is null ? null : new StubModelSnapshot(previousSnapshot));

    private static IModel BuildModel(Action<ModelBuilder> configure, ModelSnapshot? previousSnapshot)
    {
        StubMigrationsAssembly.Current.Value = previousSnapshot;

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

    [Table("composite_key_policies")]
    private sealed class CompositeKeyPolicy
    {
        public int TenantId { get; set; }
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
        public static readonly AsyncLocal<ModelSnapshot?> Current = new();

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

    // What `dotnet ef migrations add` writes for Policy made temporal, plus one extra column: every
    // property declared by its provider CLR type and store type, the way CSharpSnapshotGenerator does.
    private sealed class GeneratedStyleSnapshot((string Column, Type ClrType, string StoreType) extra) : ModelSnapshot
    {
        private static readonly string _sourceName = typeof(Policy).FullName!;
        private static readonly string _historyName = _sourceName + "#History";

        protected override void BuildModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity(_sourceName, b =>
            {
                b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("integer");
                b.Property<string>("Number").IsRequired().HasColumnType("text");
                b.Property<int>("Status").HasColumnType("integer");
                b.Property(extra.ClrType, extra.Column).HasColumnType(extra.StoreType).HasColumnName(extra.Column);
                b.HasKey("Id");
                b.ToTable("policies");
                b.HasAnnotation(HindsightAnnotationNames.HistoryEntityType, _historyName)
                    .HasAnnotation(HindsightAnnotationNames.IsTemporal, true);
            });

            modelBuilder.Entity(_historyName, b =>
            {
                b.Property<long>("history_id").ValueGeneratedOnAdd().HasColumnType("bigint").HasColumnName("history_id");
                b.Property<int?>("Id").HasColumnType("integer").HasColumnName("Id");
                b.Property<string>("Number").HasColumnType("text").HasColumnName("Number");
                b.Property<int?>("Status").HasColumnType("integer").HasColumnName("Status");
                b.Property(extra.ClrType, extra.Column).HasColumnType(extra.StoreType).HasColumnName(extra.Column);
                b.Property<string>("changed_by").HasColumnType("text").HasColumnName("changed_by");
                b.Property<string>("changed_by_name").HasColumnType("text").HasColumnName("changed_by_name");
                b.Property<string>("correlation_id").HasColumnType("text").HasColumnName("correlation_id");
                b.Property<string>("extra").HasColumnType("jsonb").HasColumnName("extra");
                b.Property<short>("operation").HasColumnType("smallint").HasColumnName("operation");
                b.Property<string>("reason").HasColumnType("text").HasColumnName("reason");
                b.Property<DateTime>("valid_from").HasColumnType("timestamp with time zone").HasColumnName("valid_from");
                b.Property<DateTime>("valid_to").ValueGeneratedOnAdd().HasColumnType("timestamp with time zone")
                    .HasColumnName("valid_to").HasDefaultValueSql("'infinity'::timestamp with time zone");
                b.HasKey("history_id");
                b.HasIndex(["Id", "valid_from"], "ix_policies_history_version").IsDescending(false, true);
                b.ToTable("policies_history");
                b.HasAnnotation(HindsightAnnotationNames.IsHistoryTable, true);
            });
        }
    }

    private sealed class UniquePerContextModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => Guid.NewGuid();
    }
}
