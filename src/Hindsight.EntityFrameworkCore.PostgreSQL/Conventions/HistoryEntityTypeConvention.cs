using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Hindsight.Conventions;

/// <summary>
/// Model-finalizing convention that turns every entity type marked <see cref="HindsightAnnotationNames.IsTemporal"/>
/// into a system-versioned entity by adding a property-bag history entity type (DESIGN.md D2, D5).
/// The history entity type is a real entity type, so <c>IMigrationsModelDiffer</c> generates its table.
/// </summary>
/// <remarks>
/// After mirroring the live entity columns, the convention reads the previous
/// <see cref="IMigrationsAssembly.ModelSnapshot"/> and re-materializes any history column whose source
/// property has since been removed, as a nullable shadow property tagged
/// <see cref="HindsightAnnotationNames.Orphaned"/>. The column therefore never disappears from the
/// model, so the differ never emits a <c>DropColumn</c> on a history table (DESIGN.md D6, golden rule 3).
/// The same snapshot pass also catches an entity that stopped being temporal altogether (<c>IsTemporal()</c>
/// removed, or the entity type removed from the model entirely): its whole history entity type is cloned
/// back from the snapshot verbatim — columns, key, indexes — and tagged <see cref="HindsightAnnotationNames.Orphaned"/>
/// on the entity type itself, so the differ never emits a <c>DropTable</c> either.
/// </remarks>
internal sealed class HistoryEntityTypeConvention(IMigrationsAssembly migrationsAssembly) : IModelFinalizingConvention
{
    // Column types Hindsight fixes on history tables (DESIGN.md D5). 'internal' rather than 'private'
    // only to satisfy the repo's private-field naming rule, which expects a leading underscore.
    internal const string TimestamptzColumnType = "timestamp with time zone";
    internal const string PeriodEndDefaultSql = "'infinity'::timestamp with time zone";
    internal const string ExtraColumnType = "jsonb";

    // Building the snapshot model runs the finalizing conventions again (this one included); the flag
    // stops the re-entrant pass from resolving the snapshot a second time.
    [ThreadStatic]
    private static bool _resolvingSnapshotModel;

    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var temporalEntityTypes = modelBuilder.Metadata.GetEntityTypes()
            .Where(entityType => entityType[HindsightAnnotationNames.IsTemporal] is true)
            .ToList();

        var liveHistoryEntityTypeNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entityType in temporalEntityTypes)
        {
            ValidateTemporalEntityType(entityType);
            var historyBuilder = BuildHistoryEntityType(modelBuilder, entityType);
            if (historyBuilder is not null)
            {
                liveHistoryEntityTypeNames.Add(historyBuilder.Metadata.Name);
                RestoreOrphanedColumns(historyBuilder, entityType);
            }
        }

        RestoreOrphanedHistoryEntityTypes(modelBuilder, liveHistoryEntityTypeNames);
    }

    private static void ValidateTemporalEntityType(IConventionEntityType entityType)
    {
        if (entityType.BaseType is not null
            || entityType.GetDirectlyDerivedTypes().Any()
            || entityType.FindDiscriminatorProperty() is not null)
        {
            throw new NotSupportedException(
                $"Entity '{entityType.DisplayName()}' takes part in an inheritance hierarchy, which Hindsight "
                + "does not support in v1 (DESIGN.md D9). Make the entity standalone, or remove IsTemporal().");
        }

        if (entityType.FindPrimaryKey() is null)
        {
            throw new InvalidOperationException(
                $"Entity '{entityType.DisplayName()}' is temporal but has no primary key. Temporal entities "
                + "require a key; use HasKey() or mark the entity as keyless and remove IsTemporal().");
        }

        if (entityType.GetTableName() is null)
        {
            throw new InvalidOperationException(
                $"Entity '{entityType.DisplayName()}' is temporal but is not mapped to a table. Hindsight "
                + "history requires a table mapping; call ToTable(...) or remove IsTemporal().");
        }

        // Owned references and complex properties live on their own IConventionEntityType / complex type,
        // so GetProperties() below never sees their columns: mirroring would silently drop them from
        // history, and a SaveChanges that only touches one of them would silently write no history row at
        // all (CLAUDE.md rule 2). Reject at model build time instead — matches the read-side guard in
        // HistoryQueryRootRewriter (DESIGN.md D12).
        if (entityType.GetNavigations().Any(n => n.TargetEntityType.IsOwned())
            || entityType.GetComplexProperties().Any())
        {
            throw new NotSupportedException(
                $"Entity '{entityType.DisplayName()}' is temporal but has owned or complex members, which "
                + "Hindsight cannot mirror into a history table (their columns live on their own type, not "
                + "on this entity's). Remove IsTemporal(), or remove the owned/complex members.");
        }
    }

    private static IConventionEntityTypeBuilder? BuildHistoryEntityType(
        IConventionModelBuilder modelBuilder, IConventionEntityType source)
    {
        var historyTableName = (string?)source[HindsightAnnotationNames.HistoryTableName]
            ?? source.GetTableName() + TemporalEntityTypeBuilderExtensions.DefaultHistoryTableSuffix;
        var historySchema = (string?)source[HindsightAnnotationNames.HistoryTableSchema] ?? source.GetSchema();

        var historyBuilder = modelBuilder.SharedTypeEntity(historyTableName, typeof(Dictionary<string, object>));
        if (historyBuilder is null)
        {
            return null;
        }

        historyBuilder.ToTable(historyTableName, historySchema);
        historyBuilder.HasAnnotation(HindsightAnnotationNames.IsHistoryTable, true);
        source.SetAnnotation(HindsightAnnotationNames.HistoryEntityType, historyTableName);

        MirrorEntityColumns(historyBuilder, source);
        AddPeriodColumns(historyBuilder, source);
        AddContextColumns(historyBuilder);
        AddSurrogateKey(historyBuilder);
        AddVersionIndex(historyBuilder, source);
        return historyBuilder;
    }

    private static void MirrorEntityColumns(IConventionEntityTypeBuilder historyBuilder, IConventionEntityType source)
    {
        foreach (var property in source.GetProperties())
        {
            if (property[HindsightAnnotationNames.IsExcluded] is true)
            {
                continue;
            }

            var columnName = property.GetColumnName();
            if (columnName is null)
            {
                continue;
            }

            // History drops every NOT NULL constraint of the original (DESIGN.md D5): old versions of a
            // removed column must still be storable, and a writer may not populate every column. Map
            // value types as Nullable<T> so the column can actually be null.
            var historyProperty = historyBuilder.Property(AsNullable(property.ClrType), columnName);
            if (historyProperty is null)
            {
                continue;
            }

            historyProperty.HasColumnName(columnName);
            historyProperty.IsRequired(false);

            // Facet mirroring (DESIGN.md D2): a property-bag column declared by CLR type alone loses
            // the source's store type; copy the explicit facets so enum-as-string, jsonb, numeric(p,s)
            // and custom converters land on the same column type.
            CopyStoreFacets(historyProperty, property);
        }
    }

    // Shared by MirrorEntityColumns, RestoreOrphanedColumns and CloneOrphanedHistoryEntityType: a
    // property-bag column declared by CLR type alone loses every store facet of its source, so each
    // caller copies them across explicitly.
    private static void CopyStoreFacets(IConventionPropertyBuilder target, IReadOnlyProperty source)
    {
        if (source.GetColumnType() is { } storeType)
        {
            target.HasColumnType(storeType);
        }

        if (source.GetValueConverter() is { } converter)
        {
            target.HasConversion(converter);
        }
        else if (source.GetProviderClrType() is { } providerClrType)
        {
            target.HasConversion(providerClrType);
        }

        if (source.GetMaxLength() is { } maxLength)
        {
            target.HasMaxLength(maxLength);
        }

        if (source.IsUnicode() is { } unicode)
        {
            target.IsUnicode(unicode);
        }

        if (source.GetPrecision() is { } precision)
        {
            target.HasPrecision(precision);
        }

        if (source.GetScale() is { } scale)
        {
            target.HasScale(scale);
        }
    }

    // DESIGN.md D6. A column present on the history table in the previous model snapshot but no longer
    // backed by a live entity property is re-added here — nullable, with its store facets copied from
    // the snapshot property, tagged Orphaned — so the differ sees no change and emits no DropColumn.
    private void RestoreOrphanedColumns(IConventionEntityTypeBuilder historyBuilder, IConventionEntityType source)
    {
        if (_resolvingSnapshotModel)
        {
            return;
        }

        var snapshotModel = ResolveSnapshotModel();
        var snapshotHistory = snapshotModel?.FindEntityType(historyBuilder.Metadata.Name);
        if (snapshotHistory is null)
        {
            return;
        }

        var currentColumns = historyBuilder.Metadata.GetProperties()
            .Select(property => property.GetColumnName())
            .Where(column => column is not null)
            .ToHashSet(StringComparer.Ordinal);

        var removedKeyColumns = RemovedPrimaryKeyColumns(snapshotModel!, source);

        foreach (var snapshotProperty in snapshotHistory.GetProperties())
        {
            var columnName = snapshotProperty.GetColumnName();
            if (columnName is null || currentColumns.Contains(columnName))
            {
                // Either an infrastructure column the convention just re-added, or a column still
                // mapped from a live property.
                continue;
            }

            if (removedKeyColumns.Contains(columnName))
            {
                throw new InvalidOperationException(
                    $"Entity '{source.DisplayName()}' is temporal and the property mapped to history column "
                    + $"'{columnName}' was part of its primary key, which changed. The history version index "
                    + "and the writer's close-previous-version step depend on the key columns; restore the "
                    + "property, or remove IsTemporal() from the entity.");
            }

            // Match MirrorEntityColumns: the property-bag property is named after its column.
            var restored = historyBuilder.Property(snapshotProperty.ClrType, columnName);
            if (restored is null)
            {
                continue;
            }

            restored.HasColumnName(columnName);
            restored.IsRequired(false);
            CopyStoreFacets(restored, snapshotProperty);
            restored.HasAnnotation(HindsightAnnotationNames.Orphaned, true);
        }
    }

    // DESIGN.md D6. A history entity type present in the previous model snapshot whose source stopped
    // being temporal — IsTemporal() removed, or the source entity type removed from the model entirely
    // — is not in liveHistoryEntityTypeNames, because it was never rebuilt above. Cloning it back from
    // the snapshot (columns, key, indexes, tagged Orphaned on the entity type itself) keeps it in the
    // model unchanged, so the differ sees no difference and never emits a DropTable (golden rule 3).
    // Same re-entrancy guard as RestoreOrphanedColumns: building the snapshot model re-runs this
    // convention, and the snapshot's own history entity types must not be re-cloned into themselves.
    private void RestoreOrphanedHistoryEntityTypes(
        IConventionModelBuilder modelBuilder, HashSet<string> liveHistoryEntityTypeNames)
    {
        if (_resolvingSnapshotModel)
        {
            return;
        }

        var snapshotModel = ResolveSnapshotModel();
        if (snapshotModel is null)
        {
            return;
        }

        foreach (var snapshotHistory in snapshotModel.GetEntityTypes())
        {
            if (snapshotHistory[HindsightAnnotationNames.IsHistoryTable] is true
                && !liveHistoryEntityTypeNames.Contains(snapshotHistory.Name))
            {
                CloneOrphanedHistoryEntityType(modelBuilder, snapshotHistory);
            }
        }
    }

    // Recreates a history entity type verbatim from its previous-snapshot shape: every column with its
    // store facets, nullability, default and value-generation strategy; the primary key; every index.
    // There is no live source entity left to mirror from (that is the whole point), so the snapshot's
    // own history entity type — already fully built by this convention when it was current — is the
    // only source of truth from here on.
    private static void CloneOrphanedHistoryEntityType(IConventionModelBuilder modelBuilder, IEntityType snapshotHistory)
    {
        var tableName = snapshotHistory.GetTableName();
        if (tableName is null)
        {
            return;
        }

        var historyBuilder = modelBuilder.SharedTypeEntity(snapshotHistory.Name, typeof(Dictionary<string, object>));
        if (historyBuilder is null)
        {
            // Already present in the model — can't happen alongside "not in liveHistoryEntityTypeNames"
            // unless something else claimed the name; leave it alone rather than fight over it.
            return;
        }

        historyBuilder.ToTable(tableName, snapshotHistory.GetSchema());
        historyBuilder.HasAnnotation(HindsightAnnotationNames.IsHistoryTable, true);
        historyBuilder.HasAnnotation(HindsightAnnotationNames.Orphaned, true);

        // The snapshot entity type already being Orphaned means some earlier migration already made
        // this transition (and, in Trigger mode, already dropped the stale trigger then); only the
        // build where the transition happens for the first time needs to signal it.
        if (snapshotHistory[HindsightAnnotationNames.Orphaned] is not true)
        {
            historyBuilder.HasAnnotation(HindsightAnnotationNames.OrphanedTriggerPending, true);
        }

        foreach (var snapshotProperty in snapshotHistory.GetProperties())
        {
            var columnName = snapshotProperty.GetColumnName();
            if (columnName is null)
            {
                continue;
            }

            var restored = historyBuilder.Property(snapshotProperty.ClrType, columnName);
            if (restored is null)
            {
                continue;
            }

            restored.HasColumnName(columnName);
            restored.IsRequired(!snapshotProperty.IsNullable);
            restored.ValueGenerated(snapshotProperty.ValueGenerated);
            CopyStoreFacets(restored, snapshotProperty);

            if (snapshotProperty.GetDefaultValueSql() is { } defaultSql)
            {
                restored.HasDefaultValueSql(defaultSql);
            }

            // Npgsql's stable public annotation key (AddSurrogateKey sets it the same way); only
            // history_id carries it, but copy whatever is actually there rather than assume.
            if (snapshotProperty.FindAnnotation("Npgsql:ValueGenerationStrategy") is { Value: not null } strategy)
            {
                restored.HasAnnotation("Npgsql:ValueGenerationStrategy", strategy.Value);
            }
        }

        var snapshotKey = snapshotHistory.FindPrimaryKey();
        if (snapshotKey is not null)
        {
            var keyProperties = snapshotKey.Properties
                .Select(p => p.GetColumnName() is { } column ? historyBuilder.Metadata.FindProperty(column) : null)
                .ToList();
            if (keyProperties.Count > 0 && keyProperties.All(p => p is not null))
            {
                historyBuilder.PrimaryKey(keyProperties!);
            }
        }

        foreach (var snapshotIndex in snapshotHistory.GetIndexes())
        {
            var indexColumns = snapshotIndex.Properties
                .Select(p => p.GetColumnName())
                .Where(column => column is not null)
                .Select(column => column!)
                .ToList();
            if (indexColumns.Count != snapshotIndex.Properties.Count || snapshotIndex.Name is not { } indexName)
            {
                continue;
            }

            var restoredIndex = historyBuilder.HasIndex(indexColumns, indexName);
            if (restoredIndex is not null && snapshotIndex.IsDescending is { Count: > 0 } descending)
            {
                restoredIndex.IsDescending(descending);
            }
        }
    }

    // Columns that were part of the source entity's primary key in the snapshot and are not part of
    // it now. Removing a key property from a temporal entity is a usage mistake, not a supported
    // evolution (DESIGN.md D6).
    private static HashSet<string> RemovedPrimaryKeyColumns(IModel snapshotModel, IConventionEntityType source)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);

        var snapshotKey = snapshotModel.FindEntityType(source.Name)?.FindPrimaryKey();
        if (snapshotKey is null)
        {
            return removed;
        }

        var currentKeyColumns = source.FindPrimaryKey()!.Properties
            .Select(property => property.GetColumnName())
            .Where(column => column is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var keyProperty in snapshotKey.Properties)
        {
            if (keyProperty.GetColumnName() is { } column && !currentKeyColumns.Contains(column))
            {
                removed.Add(column);
            }
        }

        return removed;
    }

    private IModel? ResolveSnapshotModel()
    {
        var snapshot = migrationsAssembly.ModelSnapshot;
        if (snapshot is null)
        {
            return null;
        }

        _resolvingSnapshotModel = true;
        try
        {
            return snapshot.Model;
        }
        finally
        {
            _resolvingSnapshotModel = false;
        }
    }

    private static void AddPeriodColumns(IConventionEntityTypeBuilder historyBuilder, IConventionEntityType source)
    {
        var periodStart = (string?)source[HindsightAnnotationNames.PeriodStartColumnName]
            ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName;
        var periodEnd = (string?)source[HindsightAnnotationNames.PeriodEndColumnName]
            ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName;

        var start = AddScalarColumn(historyBuilder, periodStart, typeof(DateTime), nullable: false);
        start?.HasColumnType(TimestamptzColumnType);

        var end = AddScalarColumn(historyBuilder, periodEnd, typeof(DateTime), nullable: false);
        end?.HasColumnType(TimestamptzColumnType);
        end?.HasDefaultValueSql(PeriodEndDefaultSql);
    }

    private static void AddContextColumns(IConventionEntityTypeBuilder historyBuilder)
    {
        AddScalarColumn(historyBuilder, HindsightHistoryColumns.Operation, typeof(short), nullable: false);
        AddScalarColumn(historyBuilder, HindsightHistoryColumns.ChangedBy, typeof(string), nullable: true);
        AddScalarColumn(historyBuilder, HindsightHistoryColumns.ChangedByName, typeof(string), nullable: true);
        AddScalarColumn(historyBuilder, HindsightHistoryColumns.CorrelationId, typeof(string), nullable: true);
        AddScalarColumn(historyBuilder, HindsightHistoryColumns.Reason, typeof(string), nullable: true);

        var extra = AddScalarColumn(historyBuilder, HindsightHistoryColumns.Extra, typeof(string), nullable: true);
        extra?.HasColumnType(ExtraColumnType);
    }

    private static void AddSurrogateKey(IConventionEntityTypeBuilder historyBuilder)
    {
        var historyId = AddScalarColumn(historyBuilder, HindsightHistoryColumns.HistoryId, typeof(long), nullable: false);
        if (historyId is null)
        {
            return;
        }

        historyId.ValueGenerated(ValueGenerated.OnAdd);

        // Npgsql's stable public annotation key; the value must be the strongly-typed enum, not a string.
        historyId.HasAnnotation(
            "Npgsql:ValueGenerationStrategy",
            NpgsqlValueGenerationStrategy.IdentityAlwaysColumn);

        var keyProperty = historyBuilder.Metadata.FindProperty(HindsightHistoryColumns.HistoryId);
        if (keyProperty is not null)
        {
            historyBuilder.PrimaryKey([keyProperty]);
        }
    }

    private static void AddVersionIndex(IConventionEntityTypeBuilder historyBuilder, IConventionEntityType source)
    {
        var periodStart = (string?)source[HindsightAnnotationNames.PeriodStartColumnName]
            ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName;

        var columns = new List<string>();
        foreach (var keyProperty in source.FindPrimaryKey()!.Properties)
        {
            if (keyProperty.GetColumnName() is { } column && historyBuilder.Metadata.FindProperty(column) is not null)
            {
                columns.Add(column);
            }
        }

        columns.Add(periodStart);

        var index = historyBuilder.HasIndex(columns, "ix_" + historyBuilder.Metadata.GetTableName() + "_version");
        index?.IsDescending([.. Enumerable.Repeat(false, columns.Count - 1), true]);
    }

    private static Type AsNullable(Type clrType)
        => clrType.IsValueType && Nullable.GetUnderlyingType(clrType) is null
            ? typeof(Nullable<>).MakeGenericType(clrType)
            : clrType;

    private static IConventionPropertyBuilder? AddScalarColumn(
        IConventionEntityTypeBuilder historyBuilder,
        string columnName,
        Type clrType,
        bool nullable)
    {
        var builder = historyBuilder.Property(clrType, columnName);
        if (builder is null)
        {
            return null;
        }

        builder.HasColumnName(columnName);
        builder.IsRequired(!nullable);
        return builder;
    }
}
