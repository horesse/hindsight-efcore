using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Writers;

/// <summary>
/// Turns <see cref="PendingHistoryRow"/> values into parameterised SQL and runs it on the context's
/// connection inside the current transaction. Identifiers go through
/// <see cref="ISqlGenerationHelper"/> and values through each history column's
/// <see cref="RelationalTypeMapping"/>; nothing is concatenated as a raw identifier
/// (.claude/rules/sql-and-migrations.md).
/// <para>
/// Every statement of one <c>SaveChanges</c> is sent in a single <see cref="DbBatch"/> — one network
/// round-trip — instead of a <see cref="DbCommand"/> per statement. Rows are chunked at
/// <see cref="RowsPerBatch"/> so a batch stays well under PostgreSQL's 65535-parameters-per-<c>Bind</c>
/// protocol limit; each chunk is one round-trip. The invariant SQL text and column list for each
/// <c>(history entity type, state)</c> pair is built once per <c>SaveChanges</c> and reused for every
/// row of that group — only the parameter values differ per row.
/// </para>
/// <para>
/// A <c>Modified</c> or <c>Deleted</c> row is one statement: a data-modifying CTE closes the open
/// version with <c>valid_to = GREATEST(@ts, valid_from + 1µs)</c> and the outer <c>INSERT</c> starts
/// the new version at the value the CTE actually wrote. That is the same clamp the Trigger writer
/// applies, and it is what keeps a version chain contiguous and strictly positive when the
/// <c>@ts</c> this <c>SaveChanges</c> captured is older than the open version's <c>valid_from</c>:
/// a transaction that captured its timestamp, then waited on the row lock while another one committed
/// a newer version, or a clock that stepped backwards between two saves (DESIGN.md D3).
/// </para>
/// </summary>
internal static class HistoryRowWriter
{
    // The current-version marker (DESIGN.md D5). 'internal' rather than 'private' only to satisfy the
    // repo's private-field naming rule, which expects a leading underscore.
    internal const string Infinity = "'infinity'::timestamptz";

    // Each row is one statement carrying the versioned + 5 context + operation + timestamp parameters
    // and, for updates and deletes, the key parameters of the close CTE. 512 rows keeps even a very
    // wide entity an order of magnitude under the 65535-parameter Bind limit; a batch is one
    // round-trip however many statements it carries. 'internal' rather than 'private', like
    // <see cref="Infinity"/>, only to satisfy the repo's private-field naming rule.
    internal const int RowsPerBatch = 512;

    public static void Write(
        DbContext context,
        DateTimeOffset timestamp,
        ChangeContext changeContext,
        IReadOnlyList<PendingHistoryRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var (connection, transaction, sqlHelper) = Resolve(context);
        var templates = new TemplateCache(sqlHelper, changeContext);

        using var parameterFactory = connection.CreateCommand();

        for (var start = 0; start < rows.Count; start += RowsPerBatch)
        {
            var end = Math.Min(start + RowsPerBatch, rows.Count);

            using var batch = connection.CreateBatch();
            batch.Transaction = transaction;

            for (var i = start; i < end; i++)
            {
                AppendRow(batch, parameterFactory, templates, rows[i], timestamp);
            }

            batch.ExecuteNonQuery();
        }
    }

    public static async Task WriteAsync(
        DbContext context,
        DateTimeOffset timestamp,
        ChangeContext changeContext,
        IReadOnlyList<PendingHistoryRow> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var (connection, transaction, sqlHelper) = Resolve(context);
        var templates = new TemplateCache(sqlHelper, changeContext);

        await using var parameterFactory = connection.CreateCommand();

        for (var start = 0; start < rows.Count; start += RowsPerBatch)
        {
            var end = Math.Min(start + RowsPerBatch, rows.Count);

            await using var batch = connection.CreateBatch();
            batch.Transaction = transaction;

            for (var i = start; i < end; i++)
            {
                AppendRow(batch, parameterFactory, templates, rows[i], timestamp);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static (DbConnection Connection, DbTransaction? Transaction, ISqlGenerationHelper SqlHelper) Resolve(
        DbContext context)
        => (context.Database.GetDbConnection(),
            context.Database.CurrentTransaction?.GetDbTransaction(),
            context.GetService<ISqlGenerationHelper>());

    // Adds the one statement a pending row expands to onto the batch: a plain INSERT for an added
    // entity; a "close previous version" CTE feeding the INSERT of the new version for updates and
    // deletes. SQL text and column bindings come from the per-(type, state) template; only the values
    // are per row. The timestamp parameter is referenced twice in the CTE form (the clamp and the
    // no-open-version fallback) but bound once — Npgsql matches placeholders by name.
    private static void AppendRow(
        DbBatch batch,
        DbCommand parameterFactory,
        TemplateCache templates,
        PendingHistoryRow row,
        DateTimeOffset timestamp)
    {
        var template = templates.For(row);

        var command = batch.CreateBatchCommand();
        command.CommandText = template.Sql;

        foreach (var binding in template.VersionedBindings)
        {
            command.Parameters.Add(Parameter(
                parameterFactory, binding.ParameterName, row.Values.GetValueOrDefault(binding.ColumnName), binding.Mapping));
        }

        var contextValues = templates.ContextValues;
        for (var i = 0; i < template.ContextBindings.Count; i++)
        {
            command.Parameters.Add(Parameter(
                parameterFactory, template.ContextBindings[i].ParameterName, contextValues[i], template.ContextBindings[i].Mapping));
        }

        command.Parameters.Add(Parameter(parameterFactory, template.OperationParameter, template.Operation, mapping: null));
        command.Parameters.Add(Parameter(parameterFactory, template.TimestampParameter, timestamp, mapping: null));

        foreach (var key in template.KeyBindings)
        {
            command.Parameters.Add(Parameter(
                parameterFactory, key.ParameterName, row.Values.GetValueOrDefault(key.ColumnName), key.Mapping));
        }

        batch.BatchCommands.Add(command);
    }

    private static DbParameter Parameter(
        DbCommand factory, string name, object? value, RelationalTypeMapping? mapping)
    {
        if (mapping is not null)
        {
            return mapping.CreateParameter(factory, name, value);
        }

        var parameter = factory.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }

    // The change-context columns, in a fixed order, paired with their ChangeContext member. Written
    // only on the INSERT of the new version (DESIGN.md D5); the "close previous version" UPDATE leaves
    // the earlier row's context untouched.
    private static IEnumerable<KeyValuePair<string, string?>> ContextColumns(ChangeContext changeContext)
    {
        yield return new(HindsightHistoryColumns.ChangedBy, changeContext.UserId);
        yield return new(HindsightHistoryColumns.ChangedByName, changeContext.UserName);
        yield return new(HindsightHistoryColumns.CorrelationId, changeContext.CorrelationId);
        yield return new(HindsightHistoryColumns.Reason, changeContext.Reason);
        yield return new(HindsightHistoryColumns.Extra, changeContext.Extra);
    }

    // One parameter name / column / type-mapping triple. <see cref="ColumnName"/> keys into
    // <see cref="PendingHistoryRow.Values"/> for versioned and key columns; it is unused for context
    // columns, whose values are call-global and taken positionally from
    // <see cref="TemplateCache.ContextValues"/>.
    private readonly record struct ColumnBinding(string ParameterName, string ColumnName, RelationalTypeMapping? Mapping);

    // The invariant SQL and bindings for one (history entity type, state) within a SaveChanges. Built
    // once by TemplateCache and reused for every pending row of that group. KeyBindings is empty for
    // Added rows, whose statement has no close CTE.
    private sealed class HistoryTemplate
    {
        public required string Sql { get; init; }

        public required string TimestampParameter { get; init; }

        public required string OperationParameter { get; init; }

        public required short Operation { get; init; }

        public required List<ColumnBinding> VersionedBindings { get; init; }

        public required List<ColumnBinding> ContextBindings { get; init; }

        public required List<ColumnBinding> KeyBindings { get; init; }
    }

    // Builds and memoises one HistoryTemplate per (history entity type, state) for the current
    // SaveChanges, and holds that call's change-context values — identical for every row it writes.
    private sealed class TemplateCache(ISqlGenerationHelper sqlHelper, ChangeContext changeContext)
    {
        private readonly Dictionary<(IEntityType Type, EntityState State), HistoryTemplate> _templates = [];

        public string?[] ContextValues { get; } = ContextColumns(changeContext).Select(c => c.Value).ToArray();

        public HistoryTemplate For(PendingHistoryRow row)
        {
            var key = (row.HistoryEntityType, row.State);
            if (!_templates.TryGetValue(key, out var template))
            {
                template = Build(row);
                _templates.Add(key, template);
            }

            return template;
        }

        private HistoryTemplate Build(PendingHistoryRow row)
        {
            var historyType = row.HistoryEntityType;
            var table = sqlHelper.DelimitIdentifier(historyType.GetTableName()!, historyType.GetSchema());
            var validFrom = sqlHelper.DelimitIdentifier(row.PeriodStartColumn);
            var validTo = sqlHelper.DelimitIdentifier(row.PeriodEndColumn);
            var operationColumn = sqlHelper.DelimitIdentifier(HindsightHistoryColumns.Operation);
            var timestampParameter = sqlHelper.GenerateParameterName("ts");
            var operationParameter = sqlHelper.GenerateParameterName("op");

            var columns = new List<string>(row.VersionedColumns.Count + 5);
            var placeholders = new List<string>(row.VersionedColumns.Count + 5);
            var versionedBindings = new List<ColumnBinding>(row.VersionedColumns.Count);

            var index = 0;
            foreach (var (column, _) in row.VersionedColumns)
            {
                var name = sqlHelper.GenerateParameterName("p" + index++);
                columns.Add(sqlHelper.DelimitIdentifier(column));
                placeholders.Add(name);
                versionedBindings.Add(new ColumnBinding(
                    name, column, historyType.FindProperty(column)!.GetRelationalTypeMapping()));
            }

            var contextBindings = new List<ColumnBinding>(5);
            var contextIndex = 0;
            foreach (var (column, _) in ContextColumns(changeContext))
            {
                var name = sqlHelper.GenerateParameterName("c" + contextIndex++);
                columns.Add(sqlHelper.DelimitIdentifier(column));
                placeholders.Add(name);
                contextBindings.Add(new ColumnBinding(
                    name, column, historyType.FindProperty(column)?.GetRelationalTypeMapping()));
            }

            var operation = row.State switch
            {
                EntityState.Added => HistoryOperation.Insert,
                EntityState.Modified => HistoryOperation.Update,
                EntityState.Deleted => HistoryOperation.Delete,
                _ => throw new UnreachableException(),
            };

            var insertInto = $"INSERT INTO {table} ({string.Join(", ", columns)}, {validFrom}, {validTo}, {operationColumn})";
            var valueList = string.Join(", ", placeholders);

            if (row.State == EntityState.Added)
            {
                return new HistoryTemplate
                {
                    Sql = $"{insertInto} VALUES ({valueList}, {timestampParameter}, {Infinity}, {operationParameter})",
                    TimestampParameter = timestampParameter,
                    OperationParameter = operationParameter,
                    Operation = (short)operation,
                    VersionedBindings = versionedBindings,
                    ContextBindings = contextBindings,
                    KeyBindings = [],
                };
            }

            var predicates = new List<string>(row.KeyColumns.Count + 1);
            var keyBindings = new List<ColumnBinding>(row.KeyColumns.Count);

            var keyIndex = 0;
            foreach (var column in row.KeyColumns)
            {
                var name = sqlHelper.GenerateParameterName("k" + keyIndex++);
                predicates.Add($"{sqlHelper.DelimitIdentifier(column)} = {name}");
                keyBindings.Add(new ColumnBinding(
                    name, column, historyType.FindProperty(column)!.GetRelationalTypeMapping()));
            }

            predicates.Add($"{validTo} = {Infinity}");

            // Close the open version no earlier than 1µs after it started, whatever @ts says, and
            // start the new version at exactly the instant the old one was closed with. When there
            // is no open version to close (history that predates IsTemporal(), or a chain ended by a
            // hand-written migration), the new version starts at @ts.
            var closeSql =
                $"UPDATE {table} SET {validTo} = GREATEST({timestampParameter}, {validFrom} + INTERVAL '1 microsecond') "
                + $"WHERE {string.Join(" AND ", predicates)} RETURNING {validTo}";
            var closedAt = $"COALESCE((SELECT {validTo} FROM closed), {timestampParameter})";

            // valid_to for the new row: 'infinity' for an open version. A delete tombstone gets the
            // empty interval [closed_at, closed_at) — it records the deletion and its context but never
            // matches an AsOf predicate (valid_from <= t AND valid_to > t), so the entity has zero open
            // versions afterwards (DESIGN.md D5).
            var newValidTo = row.State == EntityState.Deleted ? closedAt : Infinity;

            return new HistoryTemplate
            {
                Sql = $"WITH closed AS ({closeSql}) {insertInto} SELECT {valueList}, {closedAt}, {newValidTo}, {operationParameter}",
                TimestampParameter = timestampParameter,
                OperationParameter = operationParameter,
                Operation = (short)operation,
                VersionedBindings = versionedBindings,
                ContextBindings = contextBindings,
                KeyBindings = keyBindings,
            };
        }
    }
}
