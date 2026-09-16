using System.Text;
using Hindsight.Migrations;
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
    // Column types Hindsight fixes on history tables (DESIGN.md D5).
    private const string TimestamptzColumnType = "timestamp with time zone";
    private const string PeriodEndDefaultSql = "'infinity'::timestamp with time zone";
    private const string ExtraColumnType = "jsonb";
    private const string DbSessionUserDefaultSql = "session_user";

    // The history entity's identity in the model (DESIGN.md D15) for a source becoming temporal for
    // the first time: stable across a later table rename, unlike the table name itself. Styled after
    // EF's own generated names for entities that don't come from a CLR type directly (e.g.
    // "Blog.Owner#Owner" for an owned type) — '#' cannot appear in a CLR type name, so this can never
    // collide with a real entity.
    private const string HistoryIdentitySuffix = "#History";

    // PostgreSQL's NAMEDATALEN is 64, so any identifier (table, column, function, trigger, index name)
    // longer than this is silently truncated to it, in bytes — not characters; PostgreSQL counts UTF-8
    // bytes, and a quoted identifier can be non-ASCII. Two identifiers that share the same first 63
    // bytes become the same physical object with no error from PostgreSQL and none from EF Core either
    // (see ValidateIdentifierLengths).
    private const int MaxIdentifierBytes = 63;

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

        // Resolved once per pass (rather than once per entity type, as before) and shared by every
        // callee below; guarded exactly as each callee used to guard it individually, so a re-entrant
        // pass building the snapshot's own model never tries to resolve the snapshot a second time.
        var snapshotModel = _resolvingSnapshotModel ? null : ResolveSnapshotModel();

        var liveHistoryEntityTypeNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entityType in temporalEntityTypes)
        {
            ValidateTemporalEntityType(entityType);
            var historyBuilder = BuildHistoryEntityType(modelBuilder, entityType, snapshotModel);
            if (historyBuilder is not null)
            {
                liveHistoryEntityTypeNames.Add(historyBuilder.Metadata.Name);
                RestoreOrphanedColumns(historyBuilder, entityType, snapshotModel);
            }
        }

        RestoreOrphanedHistoryEntityTypes(modelBuilder, liveHistoryEntityTypeNames, snapshotModel);
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

        // A property still exists and is still the primary key here — it is only marked non-versioned.
        // If every key property is excluded, the history table mirrors none of them (MirrorEntityColumns
        // skips excluded properties too), so there is no column left to identify which history rows
        // belong to which version of the entity. The writer's "close the previous version" step has
        // nothing to match on, and it would silently write no history row at all, forever, with no
        // error. Reject at model build time instead.
        if (entityType.FindPrimaryKey()!.Properties.All(property => property[HindsightAnnotationNames.IsExcluded] is true))
        {
            throw new InvalidOperationException(
                $"Entity '{entityType.DisplayName()}' is temporal but every property of its primary key is "
                + "excluded from history with Exclude(...). Hindsight has no way to tell which history rows "
                + "belong to which version of the entity without at least one key column, so the writer could "
                + "never find the previous version to close. Un-exclude at least one primary-key property — "
                + "Exclude(...) is meant for noisy non-key columns, not the key itself.");
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
        // all. Reject at model build time instead — matches the read-side guard in
        // HistoryQueryRootRewriter (DESIGN.md D12).
        if (entityType.GetNavigations().Any(n => n.TargetEntityType.IsOwned())
            || entityType.GetComplexProperties().Any())
        {
            throw new NotSupportedException(
                $"Entity '{entityType.DisplayName()}' is temporal but has owned or complex members, which "
                + "Hindsight cannot mirror into a history table (their columns live on their own type, not "
                + "on this entity's). Remove IsTemporal(), or remove the owned/complex members.");
        }

        ValidateReservedColumnNames(entityType);

        // Unconditional rather than gated on HistoryWriter.Trigger (the trigger function/trigger name
        // only matter in that mode): a caller can switch writer modes later with UseHistoryWriter(...)
        // without touching the model, and by then a writer-gated check would already have skipped this
        // entity while it was still in Interceptor mode. Checking every identifier unconditionally
        // costs nothing at model-build time and never has to be re-run.
        ValidateIdentifierLengths(entityType, ResolveHistoryTableName(entityType));
    }

    // PostgreSQL truncates any identifier over MaxIdentifierBytes to that length, silently and with no
    // error (NAMEDATALEN - 1). Two entities whose generated table, function, trigger or index name
    // differ only after that many bytes become the SAME physical object once a migration is applied to
    // real PostgreSQL: CREATE TABLE / CREATE INDEX on the collided name then fails loudly ("relation ...
    // already exists"), but CREATE OR REPLACE FUNCTION does not — it silently replaces the losing
    // entity's trigger function body with the winning one's, and the losing entity's own trigger (itself
    // unaffected, since triggers are scoped per relation, not global) goes on calling the wrong function
    // on every future insert/update/delete, corrupting that entity's history with no error anywhere.
    // EF Core itself performs no such check at model-build or migration-generation
    // time, so `dotnet ef migrations add` succeeds silently for both entities; the collision only
    // surfaces once the generated SQL reaches PostgreSQL. Reject it here instead.
    private static void ValidateIdentifierLengths(IConventionEntityType entityType, string historyTableName)
    {
        CheckIdentifierLength(entityType, "history table name", historyTableName);
        CheckIdentifierLength(entityType, "trigger function name", HistoryTriggerSqlGenerator.FunctionName(historyTableName));
        CheckIdentifierLength(entityType, "trigger name", HistoryTriggerSqlGenerator.TriggerName(historyTableName));
        CheckIdentifierLength(entityType, "version index name", VersionIndexName(historyTableName));
        CheckIdentifierLength(entityType, "period-range index name", HistoryIndexSqlGenerator.IndexName(historyTableName));
    }

    private static void CheckIdentifierLength(IConventionEntityType entityType, string kind, string identifier)
    {
        var byteLength = Encoding.UTF8.GetByteCount(identifier);
        if (byteLength <= MaxIdentifierBytes)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Entity '{entityType.DisplayName()}' is temporal but its generated {kind} '{identifier}' is "
            + $"{byteLength} bytes long, which exceeds PostgreSQL's {MaxIdentifierBytes}-byte identifier "
            + "limit (NAMEDATALEN - 1). PostgreSQL truncates identifiers over that length silently "
            + "instead of erroring, so two entities whose names collide only after that point can end up "
            + "sharing the same physical table, index, function or trigger. Give the entity a shorter "
            + "history table name with UseHistoryTable(\"shorter_name\"), or rename its main table.");
    }

    // Every column Hindsight fixes on the history table (DESIGN.md D5): the surrogate key, the change
    // context, and the period columns, whichever names those were configured with. A source property
    // mapped to one of these silently loses the collision instead of failing: MirrorEntityColumns adds
    // the property first, and AddContextColumns/AddPeriodColumns/AddSurrogateKey's own
    // historyBuilder.Property(fixedType, sameName) calls quietly reconfigure that same property to the
    // fixed CLR type and facets rather than erroring, so the entity's original data type is lost with no
    // signal. Downstream this shows up differently per writer: HistoryWriter.Interceptor concatenates the
    // versioned and fixed context columns into one INSERT list with no de-duplication, so it either fails
    // an InvalidCastException converting the entity's value into the fixed column's shape, or the SQL
    // reaches PostgreSQL with the same column named twice ("column ... specified more than once"),
    // depending on whether the coerced type happens to match the entity's own. HistoryWriter.Trigger's
    // migrations generator excludes every fixed-name column from the trigger's versioned column set by
    // name, so the entity's real value for that column is never captured at all — no error anywhere,
    // dotnet ef migrations add and every SaveChanges succeed, and the column silently always holds the
    // change context's value (or NULL) instead. Reject the collision at model-build time instead of
    // hitting either failure mode at migration or SaveChanges time.
    private static void ValidateReservedColumnNames(IConventionEntityType entityType)
    {
        var periodStart = (string?)entityType[HindsightAnnotationNames.PeriodStartColumnName]
            ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName;
        var periodEnd = (string?)entityType[HindsightAnnotationNames.PeriodEndColumnName]
            ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName;

        if (string.Equals(periodStart, periodEnd, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Entity '{entityType.DisplayName()}' is temporal but its period start and end columns are "
                + $"both named '{periodStart}'. Give them distinct names with HasPeriodStart(...) and HasPeriodEnd(...).");
        }

        var reservedColumns = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HindsightHistoryColumns.HistoryId] = "the history table's surrogate key",
            [HindsightHistoryColumns.Operation] = "the change-kind column",
            [HindsightHistoryColumns.ChangedBy] = "the change context",
            [HindsightHistoryColumns.ChangedByName] = "the change context",
            [HindsightHistoryColumns.CorrelationId] = "the change context",
            [HindsightHistoryColumns.Reason] = "the change context",
            [HindsightHistoryColumns.Extra] = "the change context",
            [HindsightHistoryColumns.DbSessionUser] = "the db_session_user audit column (DESIGN.md D16), whether or not this entity opts into it",
        };

        var periodColumns = new HashSet<string>(StringComparer.Ordinal) { periodStart, periodEnd };

        foreach (var property in entityType.GetProperties())
        {
            if (property[HindsightAnnotationNames.IsExcluded] is true)
            {
                // Never mirrored onto the history table (MirrorEntityColumns skips it too), so there is
                // no actual column to collide with.
                continue;
            }

            var columnName = property.GetColumnName();
            if (columnName is null)
            {
                continue;
            }

            if (reservedColumns.TryGetValue(columnName, out var reservedFor))
            {
                throw new InvalidOperationException(
                    $"Entity '{entityType.DisplayName()}' is temporal but its property '{property.Name}' is "
                    + $"mapped to column '{columnName}', which Hindsight reserves on the history table for "
                    + $"{reservedFor}. Map the property to a different column with HasColumnName(...), or "
                    + "exclude it from history with Exclude(...) if it does not need to be versioned.");
            }

            if (periodColumns.Contains(columnName))
            {
                throw new InvalidOperationException(
                    $"Entity '{entityType.DisplayName()}' is temporal but its property '{property.Name}' is "
                    + $"mapped to column '{columnName}', which is configured as the history table's period "
                    + "column. Map the property to a different column with HasColumnName(...), pick a "
                    + "different period column name with HasPeriodStart(...)/HasPeriodEnd(...), or exclude "
                    + "the property from history with Exclude(...) if it does not need to be versioned.");
            }
        }
    }

    // Shared with ValidateIdentifierLengths, which must reject on the actual resolved history table
    // name — the caller-chosen UseHistoryTable(...) override when there is one, not the default
    // derivation from the main table name — rather than re-deriving it and risking the two falling out
    // of sync.
    private static string ResolveHistoryTableName(IConventionEntityType source)
        => (string?)source[HindsightAnnotationNames.HistoryTableName]
            ?? source.GetTableName() + TemporalEntityTypeBuilderExtensions.DefaultHistoryTableSuffix;

    private static IConventionEntityTypeBuilder? BuildHistoryEntityType(
        IConventionModelBuilder modelBuilder, IConventionEntityType source, IModel? snapshotModel)
    {
        var historyIdentityName = ResolveHistoryIdentityName(source, snapshotModel);
        var historyTableName = ResolveHistoryTableName(source);
        var historySchema = (string?)source[HindsightAnnotationNames.HistoryTableSchema] ?? source.GetSchema();

        ValidateNoStoreFacetChanges(source, snapshotModel?.FindEntityType(historyIdentityName));

        var historyBuilder = modelBuilder.SharedTypeEntity(historyIdentityName, typeof(Dictionary<string, object>));
        if (historyBuilder is null)
        {
            return null;
        }

        historyBuilder.ToTable(historyTableName, historySchema);
        historyBuilder.HasAnnotation(HindsightAnnotationNames.IsHistoryTable, true);
        source.SetAnnotation(HindsightAnnotationNames.HistoryEntityType, historyIdentityName);

        MirrorEntityColumns(historyBuilder, source);
        AddPeriodColumns(historyBuilder, source);
        AddContextColumns(historyBuilder);
        AddDbSessionUserColumn(historyBuilder, source);
        AddSurrogateKey(historyBuilder);
        AddVersionIndex(historyBuilder, source);
        return historyBuilder;
    }

    // DESIGN.md D15. The history entity's identity (its Name in the model, as opposed to its mapped
    // table name) must survive a later rename of either the main table (default-suffix naming derives
    // the history table name from it) or the history table itself (UseHistoryTable) — otherwise the
    // differ can never see the rename as the SAME entity with a new table name, only as one entity
    // disappearing and an unrelated one appearing (CreateTable, with the old one kept alive but
    // orphaned by RestoreOrphanedHistoryEntityTypes below). A source already temporal in the previous
    // snapshot keeps whatever identity it already had there, forever — the same "propagate forward"
    // trick D6 already uses for Orphaned — which grandfathers every entity that was already temporal
    // before this fix (its identity today equals its own table name) so upgrading Hindsight alone
    // produces no migration diff. Only a source becoming temporal for the first time after this fix
    // gets the new table-name-independent scheme.
    private static string ResolveHistoryIdentityName(IConventionEntityType source, IModel? snapshotModel)
    {
        if (snapshotModel?.FindEntityType(source.Name) is { } snapshotSource
            && (string?)snapshotSource[HindsightAnnotationNames.HistoryEntityType] is { } previousIdentity)
        {
            return previousIdentity;
        }

        return source.Name + HistoryIdentitySuffix;
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

    // DESIGN.md D6 (extended). MirrorEntityColumns re-declares a live column under its existing name on
    // every build, so a source property that changed its store shape since the previous snapshot — a
    // different HasColumnType, a different HasConversion, a widened/narrowed HasPrecision/HasScale/
    // HasMaxLength — looks identical to an untouched column from the differ's point of view: same
    // identity, same name, just different facets on the history side. The differ then emits a genuine
    // AlterColumnOperation against the history table, which golden rule 3 forbids (a generated migration
    // never destroys or reshapes history) and which can fail or silently corrupt values that were valid
    // under the old type (e.g. `text` history rows that are not valid JSON, altered to `jsonb`). Reject
    // it here, the same way a removed primary-key column is rejected, instead of letting it reach the
    // differ. Compared against the *previous snapshot's* history property (not the current one, which
    // this same convention is about to rebuild from the live source) — the one column set that actually
    // reflects what is physically still in the history table today.
    private static void ValidateNoStoreFacetChanges(IConventionEntityType source, IEntityType? snapshotHistory)
    {
        if (snapshotHistory is null)
        {
            return;
        }

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

            var previous = snapshotHistory.FindProperty(columnName);
            if (previous is null || previous[HindsightAnnotationNames.Orphaned] is true)
            {
                // Brand new column (the "add a property" case), or the name only exists today as an
                // orphan (e.g. reused after an earlier remove) — nothing live to compare against.
                continue;
            }

            if (!StoreFacetsMatch(property, previous))
            {
                throw new InvalidOperationException(
                    $"Entity '{source.DisplayName()}' is temporal and property '{property.Name}' (column "
                    + $"'{columnName}') changed its store type, precision/scale, max length or value "
                    + "converter since the last migration. Hindsight mirrors a live column's type onto the "
                    + "history table under the same name (DESIGN.md D2), so this would alter an existing "
                    + "history column in place — which DESIGN.md D6 and golden rule 3 forbid, since it can "
                    + "fail or silently reshape values that history already holds under the old type. Give "
                    + "the property a different column name with HasColumnName(...) instead: the old column "
                    + "is kept as a nullable orphan, exactly like a rename (DESIGN.md D6), and the new one "
                    + "starts clean under the new type. If the physical column truly must change type in "
                    + "place, write that migration by hand.");
            }
        }
    }

    // Facets compared the same way CopyStoreFacets copies them, so "no change" here really does mean the
    // physical column shape is unchanged. Value converters are compared by their resulting provider CLR
    // type rather than by instance, since a fresh model build always constructs a new converter instance
    // even when its configuration did not change; falling back to the property's own ClrType (with the
    // Nullable<T> wrapper MirrorEntityColumns adds on the history side stripped, so a value type compares
    // equal to itself across the two sides) catches a plain CLR type change with neither a converter nor
    // an explicit column type configured.
    //
    // GetColumnType() is compared only when BOTH sides return a value. `source` here is still an
    // IConventionProperty mid-model-finalizing (this convention runs as part of that pass), and an
    // unconfigured property's *default* store type (e.g. plain `int` -> "integer") is only resolved by
    // relational type mapping once the model is fully built — the same reason CopyStoreFacets itself only
    // calls HasColumnType when the source already has an explicit value. `previous` is read from an
    // already fully-built snapshot IModel, so an unconfigured column there legitimately shows its
    // resolved default. Comparing "unresolved null" against "resolved default" would be a false
    // mismatch on essentially every column nobody ever called HasColumnType on; skipping the comparison
    // when either side is null is safe because CopyStoreFacets would not have set an explicit type on the
    // history column either, so the differ resolves both the old and the new mirrored column through the
    // exact same default mapping from the (compared below) ClrType and reaches the same physical type.
    private static bool StoreFacetsMatch(IReadOnlyProperty current, IReadOnlyProperty previous)
    {
        var currentType = current.GetColumnType();
        var previousType = previous.GetColumnType();
        if (currentType is not null && previousType is not null
            && !string.Equals(currentType, previousType, StringComparison.Ordinal))
        {
            return false;
        }

        return ResolvedStoreClrType(current) == ResolvedStoreClrType(previous)
            && current.GetMaxLength() == previous.GetMaxLength()
            && current.IsUnicode() == previous.IsUnicode()
            && current.GetPrecision() == previous.GetPrecision()
            && current.GetScale() == previous.GetScale();
    }

    private static Type ResolvedStoreClrType(IReadOnlyProperty property)
    {
        var type = property.GetValueConverter()?.ProviderClrType
            ?? property.GetProviderClrType()
            ?? property.ClrType;
        return Nullable.GetUnderlyingType(type) ?? type;
    }

    // DESIGN.md D6. A column present on the history table in the previous model snapshot but no longer
    // backed by a live entity property is re-added here — nullable, with its store facets copied from
    // the snapshot property, tagged Orphaned — so the differ sees no change and emits no DropColumn.
    private static void RestoreOrphanedColumns(
        IConventionEntityTypeBuilder historyBuilder, IConventionEntityType source, IModel? snapshotModel)
    {
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
    // The re-entrancy guard lives in the caller now (ProcessModelFinalizing passes null while building
    // the snapshot's own model), so the snapshot's own history entity types are never re-cloned into
    // themselves.
    private static void RestoreOrphanedHistoryEntityTypes(
        IConventionModelBuilder modelBuilder, HashSet<string> liveHistoryEntityTypeNames, IModel? snapshotModel)
    {
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

    // DESIGN.md D16. Opt-in only (TemporalEntityTypeBuilder<TEntity>.WithDbSessionUser), unlike every
    // other column AddContextColumns adds. A sibling method rather than a branch inside
    // AddContextColumns, for the same reason the spike that proved this column is "free" called out:
    // the column is populated by PostgreSQL's own DEFAULT session_user, not by either writer's explicit
    // column list, so nothing here — or in HistoryRowWriter / HistoryTriggerSqlGenerator — needs to know
    // about it beyond declaring it on the history entity type with that default and NOT NULL.
    private static void AddDbSessionUserColumn(IConventionEntityTypeBuilder historyBuilder, IConventionEntityType source)
    {
        if (source[HindsightAnnotationNames.HasDbSessionUser] is not true)
        {
            return;
        }

        var dbSessionUser = AddScalarColumn(historyBuilder, HindsightHistoryColumns.DbSessionUser, typeof(string), nullable: false);
        dbSessionUser?.HasDefaultValueSql(DbSessionUserDefaultSql);
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

        var index = historyBuilder.HasIndex(columns, VersionIndexName(historyBuilder.Metadata.GetTableName()!));
        index?.IsDescending([.. Enumerable.Repeat(false, columns.Count - 1), true]);
    }

    /// <summary>The name of the version index for a history table: <c>ix_&lt;history_table&gt;_version</c>.</summary>
    private static string VersionIndexName(string historyTable) => "ix_" + historyTable + "_version";

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
