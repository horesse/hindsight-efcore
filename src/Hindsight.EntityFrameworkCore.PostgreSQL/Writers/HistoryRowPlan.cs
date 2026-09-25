using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight.Writers;

/// <summary>The kind of change a history row records; stored in the <c>operation</c> column (DESIGN.md D5).</summary>
internal enum HistoryOperation : short
{
    Insert = 1,
    Update = 2,
    Delete = 3,
}

/// <summary>
/// A single tracked temporal change, captured before <c>SaveChanges</c> hits the database and
/// completed afterwards. One instance becomes one SQL statement: an <c>INSERT</c> for an added
/// entity; for updates and deletes, a "close previous version" <c>UPDATE</c> in a CTE feeding the
/// <c>INSERT</c> of the new version.
/// </summary>
internal sealed class PendingHistoryRow
{
    /// <summary>The generated property-bag history entity type this row is written into.</summary>
    public required IEntityType HistoryEntityType { get; init; }

    /// <summary>
    /// The history operation's state: <c>Added</c>, <c>Modified</c> or <c>Deleted</c>. <c>Modified</c> also
    /// for an owner the change tracker reports <c>Unchanged</c> whose owned reference changed (DESIGN.md D9).
    /// </summary>
    public required EntityState State { get; init; }

    /// <summary>
    /// The tracked entry, kept for <c>Added</c>/<c>Modified</c> so store-generated values can be
    /// re-read once the save has completed. <see langword="null"/> for <c>Deleted</c> — the entry is
    /// detached by then, so its values are captured up front.
    /// </summary>
    public required EntityEntry? Entry { get; init; }

    /// <summary>
    /// The main (non-history) entity type of the tracked entry. Used only for <c>Deleted</c> rows, by
    /// <see cref="DeletedRowSnapshotReader"/>, to re-read the row's real column values from the source
    /// table before EF Core deletes it — <see cref="EntityEntry.OriginalValues"/> cannot be trusted for
    /// that (see the reader's remarks).
    /// </summary>
    public required IEntityType SourceEntityType { get; init; }

    /// <summary>
    /// Every versioned column mirrored onto the history table, including the primary-key columns and the
    /// columns of table-split complex properties and owned references (DESIGN.md D9).
    /// </summary>
    public required IReadOnlyList<VersionedColumn> VersionedColumns { get; init; }

    /// <summary>History column names of the source primary key, for the "close previous version" predicate.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>Name of the period-start column on the history table.</summary>
    public required string PeriodStartColumn { get; init; }

    /// <summary>Name of the period-end column on the history table.</summary>
    public required string PeriodEndColumn { get; init; }

    /// <summary>
    /// Versioned column values. Populated up front for <c>Deleted</c> from <c>OriginalValues</c> — a
    /// placeholder good only for its (always-trustworthy) key columns, see the remarks on that branch
    /// in <see cref="HistoryRowPlan.BuildPending"/> — then overwritten by
    /// <see cref="DeletedRowSnapshotReader"/> with the row's real values before the delete. Filled
    /// after the save for <c>Added</c>/<c>Modified</c> (from current values, so generated keys land).
    /// </summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// What the Interceptor writer needs for one temporal entity type, resolved once per model and cached as
/// a runtime annotation (<see cref="HindsightAnnotationNames.WritePlan"/>).
/// </summary>
internal sealed class TemporalWritePlan
{
    public required IEntityType HistoryEntityType { get; init; }

    /// <summary>The <see cref="VersionedColumns.Collect"/> columns that the history table actually has.</summary>
    public required IReadOnlyList<VersionedColumn> Columns { get; init; }

    public required IReadOnlyList<string> KeyColumns { get; init; }

    public required string PeriodStartColumn { get; init; }

    public required string PeriodEndColumn { get; init; }

    /// <summary>
    /// Whether the entity has owned references. Their changes are tracked on entries of their own, so the
    /// owner's entry must be found for them (DESIGN.md D9).
    /// </summary>
    public required bool HasOwnedReferences { get; init; }

    /// <summary>The cached plan, or <see langword="null"/> when the model has no history entity type for it.</summary>
    public static TemporalWritePlan? For(IEntityType entityType)
    {
        if (entityType[HindsightAnnotationNames.HistoryEntityType] is not string historyTypeName
            || entityType.Model.FindEntityType(historyTypeName) is not { } historyType)
        {
            return null;
        }

        return entityType.GetOrAddRuntimeAnnotationValue(
            HindsightAnnotationNames.WritePlan,
            static state => Build(state.EntityType, state.HistoryType),
            (EntityType: entityType, HistoryType: historyType));
    }

    private static TemporalWritePlan Build(IEntityType entityType, IEntityType historyType)
    {
        var keyColumns = new List<string>();
        foreach (var keyProperty in entityType.FindPrimaryKey()!.Properties)
        {
            if (keyProperty.GetColumnName() is { } column && historyType.FindProperty(column) is not null)
            {
                keyColumns.Add(column);
            }
        }

        return new TemporalWritePlan
        {
            HistoryEntityType = historyType,
            Columns = VersionedColumns.Collect(entityType)
                .Where(column => historyType.FindProperty(column.Column) is not null)
                .ToList(),
            KeyColumns = keyColumns,
            PeriodStartColumn = (string?)entityType[HindsightAnnotationNames.PeriodStartColumnName]
                ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName,
            PeriodEndColumn = (string?)entityType[HindsightAnnotationNames.PeriodEndColumnName]
                ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName,
            HasOwnedReferences = VersionedColumns.HasOwnedReferences(entityType),
        };
    }
}

/// <summary>
/// Builds the list of <see cref="PendingHistoryRow"/> from a context's change tracker. Pure: reads
/// metadata and tracked values, performs no I/O. A <c>Deleted</c> row's values are only a placeholder
/// at this point — see <see cref="DeletedRowSnapshotReader"/>, which must run before any row this
/// method returns is written to history.
/// </summary>
internal static class HistoryRowPlan
{
    public static IReadOnlyList<PendingHistoryRow> BuildPending(DbContext context)
    {
        // HistorySnapshotGuardInterceptor.Guard runs first on SaveChanges (registered before this
        // writer in HindsightOptionsExtension) and already walked ChangeTracker.Entries() once. With
        // AutoDetectChangesEnabled on — the default — that call already ran the one DetectChanges()
        // pass this SaveChanges needs; nothing mutates a tracked entity's properties between the two
        // calls, so redoing it here would just re-scan the same graph (every tracked entity, not only
        // temporal ones) for the same answer. A benchmark (ChangeTrackerOverheadBenchmarks in
        // benchmarks/Hindsight.Benchmarks) showed this second scan scaling linearly with the number of
        // tracked-but-unrelated entities in the context — real cost, not noise. Suppressing detection
        // here is safe either way: if the caller left AutoDetectChangesEnabled on, Guard's call already
        // did the work; if the caller turned it off themselves, this is a no-op. Always restored in
        // `finally`, never left disabled for the rest of SaveChanges.
        var tracker = context.ChangeTracker;
        var autoDetectChangesEnabled = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            var changes = TemporalChanges.Collect(tracker);
            if (changes.Count == 0)
            {
                return [];
            }

            var rows = new List<PendingHistoryRow>(changes.Count);
            foreach (var (entry, state) in changes)
            {
                if (TemporalWritePlan.For(entry.Metadata) is not { } plan)
                {
                    continue;
                }

                if (plan.KeyColumns.Count == 0)
                {
                    // HistoryEntityTypeConvention.ValidateTemporalEntityType rejects a temporal entity whose
                    // entire primary key is Exclude()-d at model build time, so that specific cause can no
                    // longer reach a SaveChanges. This stays as a defensive fallback rather than an assert:
                    // keyColumns can in principle also end up empty via a PK property with no column mapping,
                    // or one the history type never mirrored for some other reason, and neither of those is
                    // covered by that check. Skip rather than write a history row with no way to address the
                    // previous version.
                    continue;
                }

                var row = new PendingHistoryRow
                {
                    HistoryEntityType = plan.HistoryEntityType,
                    State = state,
                    Entry = state == EntityState.Deleted ? null : entry,
                    SourceEntityType = entry.Metadata,
                    VersionedColumns = plan.Columns,
                    KeyColumns = plan.KeyColumns,
                    PeriodStartColumn = plan.PeriodStartColumn,
                    PeriodEndColumn = plan.PeriodEndColumn,
                };

                if (state == EntityState.Deleted)
                {
                    // Placeholder only. EntityEntry.OriginalValues is the entity's real last-known state
                    // when it was loaded by a query, but for a "delete by id" that never loaded the entity
                    // (Remove(new T { Id = id }), or Attach then Remove) it is just whatever CLR-default
                    // stub values the caller's instance happened to hold — and the two cases are not
                    // reliably distinguishable from here. What's written here is only good enough to carry
                    // the (always-trustworthy, since the caller had to set it to identify the row) primary
                    // key through to DeletedRowSnapshotReader, which unconditionally overwrites every
                    // versioned column — key columns and nested members included — with the row's real
                    // values read fresh from the source table before the delete. Never write history rows
                    // from this loop's output without that step running first.
                    foreach (var column in row.VersionedColumns)
                    {
                        row.Values[column.Column] = column.IsNested
                            ? null
                            : entry.OriginalValues[(IProperty)column.Property];
                    }
                }

                rows.Add(row);
            }

            return rows;
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetectChangesEnabled;
        }
    }

    /// <summary>
    /// Re-reads current values for <c>Added</c>/<c>Modified</c> rows after the save completed, so
    /// store-generated keys and database defaults land in history.
    /// </summary>
    public static void FillGeneratedValues(IReadOnlyList<PendingHistoryRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.Entry is not { } entry)
            {
                continue;
            }

            foreach (var column in row.VersionedColumns)
            {
                row.Values[column.Column] = ReadCurrentValue(entry, column);
            }
        }
    }

    // A nested column is read off the entry that holds it: the owner's own entry for its complex
    // properties (EF tracks them there), the owned reference's entry for an owned member. A missing owned
    // reference (set to null, or never set) has no entry, and a null optional complex property has no
    // value: both contribute NULL columns — exactly what the main table holds for them.
    private static object? ReadCurrentValue(EntityEntry entry, VersionedColumn column)
    {
        var holder = entry;
        ComplexPropertyEntry? complex = null;
        foreach (var member in column.Path)
        {
            if (member is INavigation navigation)
            {
                holder = holder.Reference(navigation).TargetEntry;
                complex = null;
                if (holder is null)
                {
                    return null;
                }
            }
            else
            {
                var complexProperty = (IComplexProperty)member;
                complex = complex is null ? holder.ComplexProperty(complexProperty) : complex.ComplexProperty(complexProperty);
                if (complex.CurrentValue is null)
                {
                    return null;
                }
            }
        }

        return holder.CurrentValues[(IProperty)column.Property];
    }
}
