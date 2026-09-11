using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Migrations;

/// <summary>
/// The history table and period-column names a period-range index is built from. Built by
/// <see cref="HindsightMigrationsSqlGenerator"/> from the finalized model and consumed by the pure SQL
/// builder on <see cref="HistoryIndexSqlGenerator"/>.
/// </summary>
/// <param name="HistoryTable">Name of the generated history table.</param>
/// <param name="HistorySchema">Schema of the history table, or <see langword="null"/> for the default.</param>
/// <param name="PeriodStartColumn">Name of the period-start column on the history table.</param>
/// <param name="PeriodEndColumn">Name of the period-end column on the history table.</param>
internal sealed record HistoryPeriodIndexModel(
    string HistoryTable,
    string? HistorySchema,
    string PeriodStartColumn,
    string PeriodEndColumn);

/// <summary>
/// Builds the <c>CREATE INDEX ... USING gist</c> DDL for a history table's period-overlap predicate
/// (DESIGN.md D5, .claude/rules/sql-and-migrations.md). <c>AsOf(t)</c> and <c>History&lt;T&gt;</c>'s
/// range-overlap test (<c>valid_from &lt;= t AND valid_to &gt; t</c>) is a range-containment query;
/// without a range index a query that cannot also filter on the version index's leading key columns
/// forces a sequential scan of the whole history table.
/// </summary>
/// <remarks>
/// The index is a single column, <c>gist (tstzrange(valid_from, valid_to))</c> — no primary-key columns
/// included. Range types have a native GiST opclass in PostgreSQL core, so this needs no extension
/// (README's "no database extensions, no superuser" non-goal); a composite GiST index that also covered
/// the key columns would need <c>btree_gist</c> for the scalar key part, which would violate it. A
/// single-column range index also works unchanged for a composite primary key (it never references key
/// columns at all), so there is exactly one index shape regardless of how the temporal entity is keyed.
/// </remarks>
internal static class HistoryIndexSqlGenerator
{
    /// <summary>The name of the period-range index for a history table: <c>ix_&lt;history_table&gt;_period</c>.</summary>
    public static string IndexName(string historyTable) => "ix_" + historyTable + "_period";

    /// <summary>
    /// <c>CREATE INDEX ix_&lt;history_table&gt;_period ON &lt;history_table&gt; USING gist
    /// (tstzrange(valid_from, valid_to))</c>. Emitted once, right after the history table's
    /// <c>CreateTableOperation</c> — there is nothing to re-emit later, since neither the period columns
    /// nor their types ever change.
    /// </summary>
    public static string CreatePeriodRangeIndex(HistoryPeriodIndexModel model, ISqlGenerationHelper sql)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sql);

        var index = sql.DelimitIdentifier(IndexName(model.HistoryTable));
        var table = sql.DelimitIdentifier(model.HistoryTable, model.HistorySchema);
        var validFrom = sql.DelimitIdentifier(model.PeriodStartColumn);
        var validTo = sql.DelimitIdentifier(model.PeriodEndColumn);

        return $"CREATE INDEX {index} ON {table} USING gist (tstzrange({validFrom}, {validTo}));";
    }
}
