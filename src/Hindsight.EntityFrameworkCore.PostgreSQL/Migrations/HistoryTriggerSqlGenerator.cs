using System.Text;
using Hindsight.Writers;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Migrations;

/// <summary>
/// The set of tables and columns a single history trigger has to bridge: the main table it fires on,
/// the history table it writes into, and the versioned entity columns copied between them (DESIGN.md D3).
/// Built by <see cref="HindsightMigrationsSqlGenerator"/> from the finalized model and consumed by the
/// pure SQL builders on <see cref="HistoryTriggerSqlGenerator"/>.
/// </summary>
/// <param name="HistoryTable">Name of the generated history table.</param>
/// <param name="HistorySchema">Schema of the history table, or <see langword="null"/> for the default.</param>
/// <param name="MainTable">Name of the temporal entity's own table — the trigger fires on this one.</param>
/// <param name="MainSchema">Schema of the main table, or <see langword="null"/> for the default.</param>
/// <param name="EntityColumns">
/// The versioned entity columns, in history-table order: every mirrored column minus the fixed
/// Hindsight columns, the period columns and any <see cref="HindsightAnnotationNames.Orphaned"/> column
/// (an orphan has no matching column on the main table, so <c>NEW</c>/<c>OLD</c> cannot reference it —
/// DESIGN.md D6). New history rows leave orphaned columns <see langword="null"/>.
/// </param>
/// <param name="KeyColumns">
/// The source primary-key columns present on the history table, used to find the open version to close.
/// </param>
/// <param name="PeriodStartColumn">Name of the period-start column on the history table.</param>
/// <param name="PeriodEndColumn">Name of the period-end column on the history table.</param>
internal sealed record HistoryTriggerModel(
    string HistoryTable,
    string? HistorySchema,
    string MainTable,
    string? MainSchema,
    IReadOnlyList<string> EntityColumns,
    IReadOnlyList<string> KeyColumns,
    string PeriodStartColumn,
    string PeriodEndColumn);

/// <summary>
/// Builds the plpgsql that the Trigger writer relies on (DESIGN.md D3): a
/// <c>&lt;history_table&gt;_write()</c> function and a <c>&lt;history_table&gt;_trg</c>
/// <c>AFTER INSERT OR UPDATE OR DELETE</c> trigger on the main table. All identifiers pass through
/// <see cref="ISqlGenerationHelper.DelimitIdentifier(string)"/>; the function body is dollar-quoted so
/// nothing inside it needs escaping (.claude/rules/sql-and-migrations.md).
/// </summary>
internal static class HistoryTriggerSqlGenerator
{
    // The current-version marker (DESIGN.md D5) and the timestamptz store type, spelled exactly as the
    // history table declares them so a Verify snapshot of the DDL stays byte-stable. 'internal' rather
    // than 'private' only to satisfy the repo's private-field naming rule (leading underscore).
    internal const string Infinity = "'infinity'::timestamp with time zone";
    internal const string TimestamptzType = "timestamp with time zone";
    internal const string DollarTag = "$hindsight$";

    // set_config / current_setting keys. The trailing 'true' (missing_ok) on every read is mandatory:
    // a transaction that pushed no context must still succeed (.claude/rules/sql-and-migrations.md).
    internal const string ChangedByKey = "hindsight.changed_by";
    internal const string ChangedByNameKey = "hindsight.changed_by_name";
    internal const string CorrelationIdKey = "hindsight.correlation_id";
    internal const string ReasonKey = "hindsight.reason";
    internal const string ExtraKey = "hindsight.extra";

    /// <summary>The name of the trigger function for a history table: <c>&lt;history_table&gt;_write</c>.</summary>
    public static string FunctionName(string historyTable) => historyTable + "_write";

    /// <summary>The name of the trigger on the main table: <c>&lt;history_table&gt;_trg</c>.</summary>
    public static string TriggerName(string historyTable) => historyTable + "_trg";

    /// <summary>
    /// <c>CREATE OR REPLACE FUNCTION &lt;history_table&gt;_write() RETURNS trigger</c>. Idempotent, so it
    /// is safe to re-emit whenever the versioned column set changes.
    /// </summary>
    public static string CreateFunction(HistoryTriggerModel model, ISqlGenerationHelper sql)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sql);

        var function = sql.DelimitIdentifier(FunctionName(model.HistoryTable), model.HistorySchema);
        var historyTable = sql.DelimitIdentifier(model.HistoryTable, model.HistorySchema);
        var validFrom = sql.DelimitIdentifier(model.PeriodStartColumn);
        var validTo = sql.DelimitIdentifier(model.PeriodEndColumn);
        var operation = sql.DelimitIdentifier(HindsightHistoryColumns.Operation);
        var changedBy = sql.DelimitIdentifier(HindsightHistoryColumns.ChangedBy);
        var changedByName = sql.DelimitIdentifier(HindsightHistoryColumns.ChangedByName);
        var correlationId = sql.DelimitIdentifier(HindsightHistoryColumns.CorrelationId);
        var reason = sql.DelimitIdentifier(HindsightHistoryColumns.Reason);
        var extra = sql.DelimitIdentifier(HindsightHistoryColumns.Extra);

        var entity = model.EntityColumns.Select(sql.DelimitIdentifier).ToList();
        var insertColumns = string.Join(
            ", ",
            entity.Concat([validFrom, validTo, operation, changedBy, changedByName, correlationId, reason, extra]));

        string ValuesFrom(string record, int operationCode, string periodStart, string periodEnd)
        {
            var values = model.EntityColumns
                .Select(column => record + "." + sql.DelimitIdentifier(column))
                .Concat(
                [
                    periodStart,
                    periodEnd,
                    operationCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "_changed_by",
                    "_changed_by_name",
                    "_correlation_id",
                    "_reason",
                    "_extra",
                ]);
            return string.Join(", ", values);
        }

        // Close the still-open version of the row being updated/deleted. GREATEST(now(), valid_from +
        // 1µs) guarantees a strictly positive interval even if two transactions see the same now();
        // the closed value is captured so the next version starts exactly where the previous one ended
        // (.claude/rules/sql-and-migrations.md).
        var keyMatch = string.Join(
            " AND ",
            model.KeyColumns.Select(column => $"{sql.DelimitIdentifier(column)} = OLD.{sql.DelimitIdentifier(column)}"));
        var closeStatement =
            $"UPDATE {historyTable} SET {validTo} = GREATEST(_now, {validFrom} + INTERVAL '1 microsecond') "
            + $"WHERE {keyMatch} AND {validTo} = {Infinity} "
            + $"RETURNING {validTo} INTO _closed_at";

        // Skip writing a version when no versioned column actually changed: an update that only touched
        // excluded columns must produce no history row (DESIGN.md D5).
        var newTuple = "(" + string.Join(", ", model.EntityColumns.Select(c => "NEW." + sql.DelimitIdentifier(c))) + ")";
        var oldTuple = "(" + string.Join(", ", model.EntityColumns.Select(c => "OLD." + sql.DelimitIdentifier(c))) + ")";

        var body = new StringBuilder();
        body.Append("CREATE OR REPLACE FUNCTION ").Append(function).Append("() RETURNS trigger\n");
        body.Append("LANGUAGE plpgsql AS ").Append(DollarTag).Append('\n');
        body.Append("DECLARE\n");
        body.Append("    _now ").Append(TimestamptzType).Append(" := now();\n");
        body.Append("    _closed_at ").Append(TimestamptzType).Append(";\n");
        body.Append("    _changed_by text := nullif(current_setting('").Append(ChangedByKey).Append("', true), '');\n");
        body.Append("    _changed_by_name text := nullif(current_setting('").Append(ChangedByNameKey).Append("', true), '');\n");
        body.Append("    _correlation_id text := nullif(current_setting('").Append(CorrelationIdKey).Append("', true), '');\n");
        body.Append("    _reason text := nullif(current_setting('").Append(ReasonKey).Append("', true), '');\n");
        body.Append("    _extra jsonb := nullif(current_setting('").Append(ExtraKey).Append("', true), '')::jsonb;\n");
        body.Append("BEGIN\n");
        body.Append("    IF (TG_OP = 'INSERT') THEN\n");
        body.Append("        INSERT INTO ").Append(historyTable).Append(" (").Append(insertColumns).Append(")\n");
        body.Append("        VALUES (").Append(ValuesFrom("NEW", (int)HistoryOperation.Insert, "_now", Infinity)).Append(");\n");
        body.Append("        RETURN NEW;\n");
        body.Append("    ELSIF (TG_OP = 'UPDATE') THEN\n");
        body.Append("        IF ").Append(newTuple).Append(" IS NOT DISTINCT FROM ").Append(oldTuple).Append(" THEN\n");
        body.Append("            RETURN NEW;\n");
        body.Append("        END IF;\n");
        body.Append("        ").Append(closeStatement).Append(";\n");
        body.Append("        INSERT INTO ").Append(historyTable).Append(" (").Append(insertColumns).Append(")\n");
        body.Append("        VALUES (").Append(ValuesFrom("NEW", (int)HistoryOperation.Update, "COALESCE(_closed_at, _now)", Infinity)).Append(");\n");
        body.Append("        RETURN NEW;\n");
        body.Append("    ELSIF (TG_OP = 'DELETE') THEN\n");
        body.Append("        ").Append(closeStatement).Append(";\n");
        body.Append("        INSERT INTO ").Append(historyTable).Append(" (").Append(insertColumns).Append(")\n");
        body.Append("        VALUES (").Append(ValuesFrom("OLD", (int)HistoryOperation.Delete, "COALESCE(_closed_at, _now)", "COALESCE(_closed_at, _now)")).Append(");\n");
        body.Append("        RETURN OLD;\n");
        body.Append("    END IF;\n");
        body.Append("    RETURN NULL;\n");
        body.Append("END;\n");
        body.Append(DollarTag).Append(';');
        return body.ToString();
    }

    /// <summary>
    /// <c>CREATE OR REPLACE TRIGGER &lt;history_table&gt;_trg AFTER INSERT OR UPDATE OR DELETE ON
    /// &lt;main_table&gt; FOR EACH ROW</c>. <c>CREATE OR REPLACE TRIGGER</c> is PostgreSQL 14+.
    /// </summary>
    public static string CreateTrigger(HistoryTriggerModel model, ISqlGenerationHelper sql)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sql);

        var trigger = sql.DelimitIdentifier(TriggerName(model.HistoryTable));
        var mainTable = sql.DelimitIdentifier(model.MainTable, model.MainSchema);
        var function = sql.DelimitIdentifier(FunctionName(model.HistoryTable), model.HistorySchema);

        return $"CREATE OR REPLACE TRIGGER {trigger} AFTER INSERT OR UPDATE OR DELETE ON {mainTable} "
            + $"FOR EACH ROW EXECUTE FUNCTION {function}();";
    }

    /// <summary>
    /// <c>DROP FUNCTION IF EXISTS &lt;history_table&gt;_write() CASCADE</c> — <c>CASCADE</c> takes the
    /// trigger on the main table with it. Emitted when the history table itself is dropped.
    /// </summary>
    public static string DropFunction(HistoryTriggerModel model, ISqlGenerationHelper sql)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sql);

        var function = sql.DelimitIdentifier(FunctionName(model.HistoryTable), model.HistorySchema);
        return $"DROP FUNCTION IF EXISTS {function}() CASCADE;";
    }
}
