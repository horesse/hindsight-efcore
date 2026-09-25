using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Migrations;

/// <summary>
/// Names and SQL for history retention (DESIGN.md D19): the <c>hindsight_retention_horizon</c> table, the
/// <c>hindsight_history_retained</c> guard function the historical queries call, and the statements
/// <c>PruneHistoryAsync</c> runs. Every identifier goes through <see cref="ISqlGenerationHelper"/>.
/// </summary>
internal static class RetentionSqlGenerator
{
    /// <summary>The model identity of the horizon table's entity type; <c>#</c> cannot collide with a CLR type name.</summary>
    public const string EntityName = "Hindsight#RetentionHorizon";

    public const string TableName = "hindsight_retention_horizon";

    public const string HistoryEntityColumn = "history_entity";

    public const string HorizonColumn = "horizon";

    public const string FunctionName = "hindsight_history_retained";

    /// <summary>
    /// The SQLSTATE the guard function raises. A user-defined code (PostgreSQL reserves none in class
    /// <c>HS</c>), so it is told apart from every error PostgreSQL itself raises.
    /// </summary>
    public const string HorizonViolationSqlState = "HS001";

    /// <summary>
    /// <c>hindsight_history_retained(history_entity, at)</c>: returns <see langword="true"/>, or raises
    /// <see cref="HorizonViolationSqlState"/> when <c>at</c> is before the horizon recorded for
    /// <c>history_entity</c>. A historical query calls it with no column argument, so PostgreSQL runs it
    /// as a one-time filter: once per scan, before any row is read, whether or not any row matches.
    /// <c>STABLE</c> so it is evaluated at execution, never folded at plan time; <c>PARALLEL SAFE</c> so it
    /// does not stop a parallel scan of a large history table.
    /// </summary>
    public static string CreateFunction(string? schema, ISqlGenerationHelper sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var function = sql.DelimitIdentifier(FunctionName, schema);
        var table = sql.DelimitIdentifier(TableName, schema);
        var historyEntity = sql.DelimitIdentifier(HistoryEntityColumn);
        var horizon = sql.DelimitIdentifier(HorizonColumn);

        return $"""
            CREATE OR REPLACE FUNCTION {function}(_history_entity text, _at timestamp with time zone) RETURNS boolean
            LANGUAGE plpgsql STABLE PARALLEL SAFE AS $hindsight$
            DECLARE
                _horizon timestamp with time zone;
            BEGIN
                SELECT r.{horizon} INTO _horizon FROM {table} AS r WHERE r.{historyEntity} = _history_entity;
                IF _horizon IS NOT NULL AND _at < _horizon THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '{HorizonViolationSqlState}',
                        MESSAGE = format('The history of %s was pruned before %s (its retention horizon), so a historical query for %s would answer from incomplete history.', _history_entity, _horizon, _at),
                        HINT = 'Query an instant at or after the retention horizon.';
                END IF;
                RETURN true;
            END;
            $hindsight$;
            """;
    }

    /// <summary>Drops the guard function from <paramref name="schema"/>, when the horizon table moved out of it.</summary>
    public static string DropFunction(string? schema, ISqlGenerationHelper sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        return $"DROP FUNCTION IF EXISTS {sql.DelimitIdentifier(FunctionName, schema)}(text, timestamp with time zone);";
    }

    /// <summary>
    /// Records <c>{1}</c> as the horizon of history entity <c>{0}</c>, never moving an existing horizon
    /// back, and returns the horizon now in force — or no row when <c>{1}</c> is later than the database's
    /// <c>now()</c>. Placeholders are for <c>SqlQueryRaw</c>.
    /// </summary>
    public static string AdvanceHorizon(string? schema, ISqlGenerationHelper sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var table = sql.DelimitIdentifier(TableName, schema);
        var historyEntity = sql.DelimitIdentifier(HistoryEntityColumn);
        var horizon = sql.DelimitIdentifier(HorizonColumn);

        return $"INSERT INTO {table} AS r ({historyEntity}, {horizon}) "
            + "SELECT {0}, {1} WHERE {1} <= now() "
            + $"ON CONFLICT ({historyEntity}) DO UPDATE SET {horizon} = GREATEST(r.{horizon}, EXCLUDED.{horizon}) "
            + $"RETURNING r.{horizon} AS \"Value\"";
    }

    /// <summary>The recorded horizon of history entity <c>{0}</c>, if any. Placeholder is for <c>SqlQueryRaw</c>.</summary>
    public static string ReadHorizon(string? schema, ISqlGenerationHelper sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var table = sql.DelimitIdentifier(TableName, schema);
        var historyEntity = sql.DelimitIdentifier(HistoryEntityColumn);
        var horizon = sql.DelimitIdentifier(HorizonColumn);

        return $"SELECT r.{horizon} AS \"Value\" FROM {table} AS r WHERE r.{historyEntity} = {{0}}";
    }

    /// <summary>
    /// Deletes at most <c>{1}</c> history rows whose period ended at or before <c>{0}</c>: closed versions
    /// and delete tombstones. The open version (<c>valid_to = 'infinity'</c>) never qualifies. The
    /// containment test is written on <c>tstzrange(valid_from, valid_to)</c> so the D14 GiST index can find
    /// the candidates; it is also true for every empty tombstone range, which the plain
    /// <c>valid_to &lt;= {0}</c> then narrows to the ones at or before the cutoff. Placeholders are for
    /// <c>ExecuteSqlRaw</c>.
    /// </summary>
    public static string DeleteBatch(
        string historyTable, string? historySchema, string periodStart, string periodEnd, ISqlGenerationHelper sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyTable);
        ArgumentNullException.ThrowIfNull(sql);

        var table = sql.DelimitIdentifier(historyTable, historySchema);
        var historyId = sql.DelimitIdentifier(HindsightHistoryColumns.HistoryId);
        var validFrom = sql.DelimitIdentifier(periodStart);
        var validTo = sql.DelimitIdentifier(periodEnd);

        return $"DELETE FROM {table} WHERE {historyId} IN ("
            + $"SELECT h.{historyId} FROM {table} AS h "
            + $"WHERE tstzrange(h.{validFrom}, h.{validTo}) <@ tstzrange('-infinity', {{0}}) AND h.{validTo} <= {{0}} "
            + "LIMIT {1})";
    }
}
