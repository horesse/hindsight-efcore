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

    /// <summary>The tracked state at the time of the snapshot: <c>Added</c>, <c>Modified</c> or <c>Deleted</c>.</summary>
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
    /// History column name → source property name for every versioned (non-excluded) column mirrored
    /// onto the history table, including the primary-key columns.
    /// </summary>
    public required IReadOnlyList<KeyValuePair<string, string>> VersionedColumns { get; init; }

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
/// Builds the list of <see cref="PendingHistoryRow"/> from a context's change tracker. Pure: reads
/// metadata and tracked values, performs no I/O. A <c>Deleted</c> row's values are only a placeholder
/// at this point — see <see cref="DeletedRowSnapshotReader"/>, which must run before any row this
/// method returns is written to history.
/// </summary>
internal static class HistoryRowPlan
{
    public static IReadOnlyList<PendingHistoryRow> BuildPending(DbContext context)
    {
        List<PendingHistoryRow>? rows = null;
        var model = context.Model;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Metadata.FindAnnotation(HindsightAnnotationNames.IsTemporal)?.Value is not true)
            {
                continue;
            }

            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.Metadata[HindsightAnnotationNames.HistoryEntityType] is not string historyTypeName
                || model.FindEntityType(historyTypeName) is not { } historyType)
            {
                continue;
            }

            if (entry.State == EntityState.Modified && !HasVersionedModification(entry))
            {
                // DESIGN.md D5 / configuration.md: a SaveChanges that touched only excluded
                // properties writes no history row.
                continue;
            }

            var versionedColumns = new List<KeyValuePair<string, string>>();
            foreach (var property in entry.Metadata.GetProperties())
            {
                if (property.FindAnnotation(HindsightAnnotationNames.IsExcluded)?.Value is true)
                {
                    continue;
                }

                if (property.GetColumnName() is { } column && historyType.FindProperty(column) is not null)
                {
                    versionedColumns.Add(new KeyValuePair<string, string>(column, property.Name));
                }
            }

            var keyColumns = new List<string>();
            foreach (var keyProperty in entry.Metadata.FindPrimaryKey()!.Properties)
            {
                if (keyProperty.GetColumnName() is { } column && historyType.FindProperty(column) is not null)
                {
                    keyColumns.Add(column);
                }
            }

            if (keyColumns.Count == 0)
            {
                // Every primary-key column is excluded from history: there is no way to address the
                // previous version. Nothing sensible to write.
                continue;
            }

            var row = new PendingHistoryRow
            {
                HistoryEntityType = historyType,
                State = entry.State,
                Entry = entry.State == EntityState.Deleted ? null : entry,
                SourceEntityType = entry.Metadata,
                VersionedColumns = versionedColumns,
                KeyColumns = keyColumns,
                PeriodStartColumn = (string?)entry.Metadata[HindsightAnnotationNames.PeriodStartColumnName]
                    ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName,
                PeriodEndColumn = (string?)entry.Metadata[HindsightAnnotationNames.PeriodEndColumnName]
                    ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName,
            };

            if (entry.State == EntityState.Deleted)
            {
                // Placeholder only. EntityEntry.OriginalValues is the entity's real last-known state
                // when it was loaded by a query, but for a "delete by id" that never loaded the entity
                // (Remove(new T { Id = id }), or Attach then Remove) it is just whatever CLR-default
                // stub values the caller's instance happened to hold — and the two cases are not
                // reliably distinguishable from here. What's written here is only good enough to carry
                // the (always-trustworthy, since the caller had to set it to identify the row) primary
                // key through to DeletedRowSnapshotReader, which unconditionally overwrites every
                // versioned column — key columns included — with the row's real values read fresh from
                // the source table before the delete. Never write history rows from this loop's output
                // without that step running first.
                var original = entry.OriginalValues;
                foreach (var (column, propertyName) in row.VersionedColumns)
                {
                    row.Values[column] = original[propertyName];
                }
            }

            (rows ??= []).Add(row);
        }

        return rows ?? [];
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

            var current = entry.CurrentValues;
            foreach (var (column, propertyName) in row.VersionedColumns)
            {
                row.Values[column] = current[propertyName];
            }
        }
    }

    private static bool HasVersionedModification(EntityEntry entry)
    {
        foreach (var property in entry.Properties)
        {
            if (property.IsModified
                && property.Metadata.FindAnnotation(HindsightAnnotationNames.IsExcluded)?.Value is not true)
            {
                return true;
            }
        }

        return false;
    }
}
