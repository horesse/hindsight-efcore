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
    /// Versioned column values. Populated up front for <c>Deleted</c> (from original values); filled
    /// after the save for <c>Added</c>/<c>Modified</c> (from current values, so generated keys land).
    /// </summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Builds the list of <see cref="PendingHistoryRow"/> from a context's change tracker. Pure: reads
/// metadata and tracked values, performs no I/O.
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
                HistoryEntityType = historyType,
                State = entry.State,
                Entry = entry.State == EntityState.Deleted ? null : entry,
                VersionedColumns = versionedColumns,
                KeyColumns = keyColumns,
                PeriodStartColumn = (string?)entry.Metadata[HindsightAnnotationNames.PeriodStartColumnName]
                    ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName,
                PeriodEndColumn = (string?)entry.Metadata[HindsightAnnotationNames.PeriodEndColumnName]
                    ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName,
            };

            if (entry.State == EntityState.Deleted)
            {
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
