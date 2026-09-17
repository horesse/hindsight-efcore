using System.Globalization;
using Hindsight.Writers;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Migrations;

/// <summary>
/// The tables and columns an initial-version seeding statement needs: the main table to read existing
/// rows from and the history table to write them into (DESIGN.md D6, "Existing non-empty table made
/// temporal"). Built by <see cref="HindsightMigrationsSqlGenerator"/> from the finalized model and
/// consumed by the pure SQL builder on <see cref="HistorySeedSqlGenerator"/>.
/// </summary>
/// <param name="HistoryTable">Name of the generated history table.</param>
/// <param name="HistorySchema">Schema of the history table, or <see langword="null"/> for the default.</param>
/// <param name="MainTable">Name of the temporal entity's own table.</param>
/// <param name="MainSchema">Schema of the main table, or <see langword="null"/> for the default.</param>
/// <param name="Columns">
/// The versioned entity columns, in history-table order — every mirrored column minus the fixed
/// Hindsight columns, the period columns, and any <see cref="HindsightAnnotationNames.Orphaned"/> column
/// (an orphan has no matching column on the main table to seed from — DESIGN.md D6). A composite primary
/// key needs no special handling: its columns are ordinary entity columns and appear here like any other.
/// </param>
/// <param name="PeriodStartColumn">Name of the period-start column on the history table.</param>
/// <param name="PeriodEndColumn">Name of the period-end column on the history table.</param>
internal sealed record HistorySeedModel(
    string HistoryTable,
    string? HistorySchema,
    string MainTable,
    string? MainSchema,
    IReadOnlyList<string> Columns,
    string PeriodStartColumn,
    string PeriodEndColumn);

/// <summary>
/// Builds the <c>INSERT INTO ... SELECT ...</c> that seeds an initial history version for every row
/// already in the main table (DESIGN.md D6). Emitted once, right after a history table's
/// <c>CreateTableOperation</c> — and, in <see cref="HistoryWriter.Trigger"/> mode, after the trigger
/// function/trigger it also gets (<see cref="HistoryTriggerSqlGenerator"/>) — unconditionally, for every
/// newly created history table, in both writer modes: whether the entity is brand new (its main table is
/// created empty in the very same migration, so the <c>SELECT</c> naturally returns zero rows — a
/// harmless no-op) or an existing entity that just had <c>IsTemporal()</c> added (the case this exists
/// for: its main table already has rows). Reliably telling those two cases apart at migration-generation
/// time is unnecessary complexity the unconditional statement avoids — it is correct, and cheap, either
/// way.
/// </summary>
internal static class HistorySeedSqlGenerator
{
    /// <summary>
    /// <c>INSERT INTO &lt;history_table&gt; (...) SELECT ..., now(), 'infinity', 1 FROM &lt;main_table&gt;</c>
    /// — one seeded version per existing row, dated "known since this migration"
    /// (<c>valid_from = now()</c>, matching the manual workaround this automates: <c>AsOf</c> before that
    /// instant still correctly returns nothing). <c>operation = 1</c> records it as an insert, like any
    /// other first version. The change-context columns (<c>changed_by</c>, <c>changed_by_name</c>,
    /// <c>correlation_id</c>, <c>reason</c>, <c>extra</c>, and the opt-in <c>db_session_user</c>) are left
    /// out of both the column list and the <c>SELECT</c>, so they come back <see langword="null"/> — a
    /// migration-time seed has no change context to attribute — and <c>db_session_user</c>, if the entity
    /// opted in, gets its own <c>DEFAULT session_user</c> exactly as it would for any other <c>INSERT</c>
    /// that does not name it (DESIGN.md D16).
    /// </summary>
    public static string SeedInitialVersions(HistorySeedModel model, ISqlGenerationHelper sql)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sql);

        var historyTable = sql.DelimitIdentifier(model.HistoryTable, model.HistorySchema);
        var mainTable = sql.DelimitIdentifier(model.MainTable, model.MainSchema);
        var validFrom = sql.DelimitIdentifier(model.PeriodStartColumn);
        var validTo = sql.DelimitIdentifier(model.PeriodEndColumn);
        var operation = sql.DelimitIdentifier(HindsightHistoryColumns.Operation);

        var columns = model.Columns.Select(sql.DelimitIdentifier).ToList();
        var insertColumns = string.Join(", ", columns.Concat([validFrom, validTo, operation]));
        var selectColumns = string.Join(", ", columns);
        var insertOperation = ((int)HistoryOperation.Insert).ToString(CultureInfo.InvariantCulture);

        return $"INSERT INTO {historyTable} ({insertColumns})\n"
            + $"SELECT {selectColumns}, now(), 'infinity', {insertOperation}\n"
            + $"FROM {mainTable};";
    }
}
