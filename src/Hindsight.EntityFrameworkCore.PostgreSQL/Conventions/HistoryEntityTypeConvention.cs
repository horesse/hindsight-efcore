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

        foreach (var entityType in temporalEntityTypes)
        {
            ValidateTemporalEntityType(entityType);
            var historyBuilder = BuildHistoryEntityType(modelBuilder, entityType);
            if (historyBuilder is not null)
            {
                RestoreOrphanedColumns(historyBuilder, entityType);
            }
        }
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
            if (property.GetColumnType() is { } storeType)
            {
                historyProperty.HasColumnType(storeType);
            }

            if (property.GetValueConverter() is { } converter)
            {
                historyProperty.HasConversion(converter);
            }
            else if (property.GetProviderClrType() is { } providerClrType)
            {
                historyProperty.HasConversion(providerClrType);
            }

            if (property.GetMaxLength() is { } maxLength)
            {
                historyProperty.HasMaxLength(maxLength);
            }

            if (property.IsUnicode() is { } unicode)
            {
                historyProperty.IsUnicode(unicode);
            }

            if (property.GetPrecision() is { } precision)
            {
                historyProperty.HasPrecision(precision);
            }

            if (property.GetScale() is { } scale)
            {
                historyProperty.HasScale(scale);
            }
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

            if (snapshotProperty.GetColumnType() is { } storeType)
            {
                restored.HasColumnType(storeType);
            }

            if (snapshotProperty.GetValueConverter() is { } converter)
            {
                restored.HasConversion(converter);
            }
            else if (snapshotProperty.GetProviderClrType() is { } providerClrType)
            {
                restored.HasConversion(providerClrType);
            }

            if (snapshotProperty.GetMaxLength() is { } maxLength)
            {
                restored.HasMaxLength(maxLength);
            }

            if (snapshotProperty.IsUnicode() is { } unicode)
            {
                restored.IsUnicode(unicode);
            }

            if (snapshotProperty.GetPrecision() is { } precision)
            {
                restored.HasPrecision(precision);
            }

            if (snapshotProperty.GetScale() is { } scale)
            {
                restored.HasScale(scale);
            }

            restored.HasAnnotation(HindsightAnnotationNames.Orphaned, true);
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
