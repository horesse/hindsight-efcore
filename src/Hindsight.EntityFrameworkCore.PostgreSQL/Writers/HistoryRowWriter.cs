using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Writers;

/// <summary>
/// Turns <see cref="PendingHistoryRow"/> values into parameterised SQL and runs it on the context's
/// connection inside the current transaction. Identifiers go through
/// <see cref="ISqlGenerationHelper"/> and values through each history column's
/// <see cref="RelationalTypeMapping"/>; nothing is concatenated as a raw identifier
/// (.claude/rules/sql-and-migrations.md).
/// </summary>
internal static class HistoryRowWriter
{
    // The current-version marker (DESIGN.md D5). 'internal' rather than 'private' only to satisfy the
    // repo's private-field naming rule, which expects a leading underscore.
    internal const string Infinity = "'infinity'::timestamptz";

    public static void Write(
        DbContext context,
        DateTimeOffset timestamp,
        ChangeContext changeContext,
        IReadOnlyList<PendingHistoryRow> rows)
    {
        var (connection, transaction, sqlHelper) = Resolve(context);

        foreach (var row in rows)
        {
            foreach (var statement in BuildStatements(row, sqlHelper, timestamp, changeContext))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                statement.Apply(command);
                command.ExecuteNonQuery();
            }
        }
    }

    public static async Task WriteAsync(
        DbContext context,
        DateTimeOffset timestamp,
        ChangeContext changeContext,
        IReadOnlyList<PendingHistoryRow> rows,
        CancellationToken cancellationToken)
    {
        var (connection, transaction, sqlHelper) = Resolve(context);

        foreach (var row in rows)
        {
            foreach (var statement in BuildStatements(row, sqlHelper, timestamp, changeContext))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                statement.Apply(command);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static (DbConnection Connection, DbTransaction? Transaction, ISqlGenerationHelper SqlHelper) Resolve(
        DbContext context)
        => (context.Database.GetDbConnection(),
            context.Database.CurrentTransaction?.GetDbTransaction(),
            context.GetService<ISqlGenerationHelper>());

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

    private static IEnumerable<HistoryStatement> BuildStatements(
        PendingHistoryRow row,
        ISqlGenerationHelper sqlHelper,
        DateTimeOffset timestamp,
        ChangeContext changeContext)
    {
        var table = sqlHelper.DelimitIdentifier(row.HistoryEntityType.GetTableName()!, row.HistoryEntityType.GetSchema());
        var validFrom = sqlHelper.DelimitIdentifier(row.PeriodStartColumn);
        var validTo = sqlHelper.DelimitIdentifier(row.PeriodEndColumn);
        var operationColumn = sqlHelper.DelimitIdentifier(HindsightHistoryColumns.Operation);
        var timestampParameter = sqlHelper.GenerateParameterName("ts");

        if (row.State is EntityState.Modified or EntityState.Deleted)
        {
            yield return BuildCloseStatement(row, sqlHelper, table, validTo, timestampParameter, timestamp);
        }

        var operation = row.State switch
        {
            EntityState.Added => HistoryOperation.Insert,
            EntityState.Modified => HistoryOperation.Update,
            EntityState.Deleted => HistoryOperation.Delete,
            _ => throw new UnreachableException(),
        };

        const int extraColumnCount = 7; // 5 context columns + operation + timestamp
        var columns = new List<string>(row.VersionedColumns.Count + 5);
        var placeholders = new List<string>(row.VersionedColumns.Count + 5);
        var parameters = new List<HistoryParameter>(row.VersionedColumns.Count + extraColumnCount);

        var index = 0;
        foreach (var (column, _) in row.VersionedColumns)
        {
            var name = sqlHelper.GenerateParameterName("p" + index++);
            columns.Add(sqlHelper.DelimitIdentifier(column));
            placeholders.Add(name);
            parameters.Add(new HistoryParameter(
                name,
                row.Values.GetValueOrDefault(column),
                row.HistoryEntityType.FindProperty(column)!.GetRelationalTypeMapping()));
        }

        var contextIndex = 0;
        foreach (var (column, value) in ContextColumns(changeContext))
        {
            var name = sqlHelper.GenerateParameterName("c" + contextIndex++);
            columns.Add(sqlHelper.DelimitIdentifier(column));
            placeholders.Add(name);
            parameters.Add(new HistoryParameter(
                name,
                value,
                row.HistoryEntityType.FindProperty(column)?.GetRelationalTypeMapping()));
        }

        var operationParameter = sqlHelper.GenerateParameterName("op");
        parameters.Add(new HistoryParameter(operationParameter, (short)operation, Mapping: null));
        parameters.Add(new HistoryParameter(timestampParameter, timestamp, Mapping: null));

        // valid_to for the new row: 'infinity' for an open version. A delete tombstone gets the empty
        // interval [ts, ts) — it records the deletion and its context but never matches an AsOf
        // predicate (valid_from <= t AND valid_to > t), so the entity has zero open versions
        // afterwards (DESIGN.md D5).
        var newValidTo = row.State == EntityState.Deleted ? timestampParameter : Infinity;

        yield return new HistoryStatement(
            $"INSERT INTO {table} ({string.Join(", ", columns)}, {validFrom}, {validTo}, {operationColumn}) "
            + $"VALUES ({string.Join(", ", placeholders)}, {timestampParameter}, {newValidTo}, {operationParameter})",
            parameters);
    }

    private static HistoryStatement BuildCloseStatement(
        PendingHistoryRow row,
        ISqlGenerationHelper sqlHelper,
        string table,
        string validTo,
        string timestampParameter,
        DateTimeOffset timestamp)
    {
        var predicates = new List<string>(row.KeyColumns.Count + 1);
        var parameters = new List<HistoryParameter>(row.KeyColumns.Count + 1)
        {
            new(timestampParameter, timestamp, Mapping: null),
        };

        var index = 0;
        foreach (var column in row.KeyColumns)
        {
            var name = sqlHelper.GenerateParameterName("k" + index++);
            predicates.Add($"{sqlHelper.DelimitIdentifier(column)} = {name}");
            parameters.Add(new HistoryParameter(
                name,
                row.Values.GetValueOrDefault(column),
                row.HistoryEntityType.FindProperty(column)!.GetRelationalTypeMapping()));
        }

        predicates.Add($"{validTo} = {Infinity}");

        return new HistoryStatement(
            $"UPDATE {table} SET {validTo} = {timestampParameter} WHERE {string.Join(" AND ", predicates)}",
            parameters);
    }

    private sealed record HistoryParameter(string Name, object? Value, RelationalTypeMapping? Mapping)
    {
        public DbParameter Create(DbCommand command)
        {
            if (Mapping is not null)
            {
                return Mapping.CreateParameter(command, Name, Value);
            }

            var parameter = command.CreateParameter();
            parameter.ParameterName = Name;
            parameter.Value = Value ?? DBNull.Value;
            return parameter;
        }
    }

    private sealed record HistoryStatement(string Sql, IReadOnlyList<HistoryParameter> Parameters)
    {
        public void Apply(DbCommand command)
        {
            command.CommandText = Sql;
            foreach (var parameter in Parameters)
            {
                command.Parameters.Add(parameter.Create(command));
            }
        }
    }
}
