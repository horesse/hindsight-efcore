using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace Hindsight.Conventions;

/// <summary>
/// Model-finalizing convention that turns every entity type marked <see cref="HindsightAnnotationNames.IsTemporal"/>
/// into a system-versioned entity by adding a property-bag history entity type (DESIGN.md D2, D5).
/// The history entity type is a real entity type, so <c>IMigrationsModelDiffer</c> generates its table.
/// </summary>
internal sealed class HistoryEntityTypeConvention : IModelFinalizingConvention
{
    // Column types Hindsight fixes on history tables (DESIGN.md D5). 'internal' rather than 'private'
    // only to satisfy the repo's private-field naming rule, which expects a leading underscore.
    internal const string TimestamptzColumnType = "timestamp with time zone";
    internal const string PeriodEndDefaultSql = "'infinity'::timestamp with time zone";
    internal const string ExtraColumnType = "jsonb";

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
            BuildHistoryEntityType(modelBuilder, entityType);
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

    private static void BuildHistoryEntityType(IConventionModelBuilder modelBuilder, IConventionEntityType source)
    {
        var historyTableName = (string?)source[HindsightAnnotationNames.HistoryTableName]
            ?? source.GetTableName() + TemporalEntityTypeBuilderExtensions.DefaultHistoryTableSuffix;
        var historySchema = (string?)source[HindsightAnnotationNames.HistoryTableSchema] ?? source.GetSchema();

        var historyBuilder = modelBuilder.SharedTypeEntity(historyTableName, typeof(Dictionary<string, object>));
        if (historyBuilder is null)
        {
            return;
        }

        historyBuilder.ToTable(historyTableName, historySchema);
        historyBuilder.HasAnnotation(HindsightAnnotationNames.IsHistoryTable, true);
        source.SetAnnotation(HindsightAnnotationNames.HistoryEntityType, historyTableName);

        MirrorEntityColumns(historyBuilder, source);
        AddPeriodColumns(historyBuilder, source);
        AddContextColumns(historyBuilder);
        AddSurrogateKey(historyBuilder);
        AddVersionIndex(historyBuilder, source);
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
