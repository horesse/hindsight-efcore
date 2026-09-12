using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Writers;

/// <summary>
/// Overwrites <see cref="PendingHistoryRow.Values"/> for every <see cref="HistoryOperation.Delete"/>
/// row with the row's real, currently-committed column values, read fresh from the source table by
/// primary key — <em>never</em> from <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry.OriginalValues"/>.
/// <para>
/// <c>OriginalValues</c> is the entity's real last-known state only when the entity was loaded by a
/// query first. The common "delete by id" shorthand — <c>Remove(new T { Id = id })</c>, or
/// <c>Attach</c> then <c>Remove</c> — never loads the entity, so <c>OriginalValues</c> on that stub is
/// just whatever CLR-default values the caller's instance happened to hold: <see langword="default"/>
/// for every non-key property, not the database's last version. EF Core exposes no reliable public
/// signal that distinguishes "loaded, then removed" from "attached as a stub, then removed" — both end
/// up with <c>OriginalValues == CurrentValues</c> — so this reader does not try to guess; it always
/// re-reads, for every <c>Deleted</c> row, rather than risk a fabricated tombstone (CLAUDE.md rule 2).
/// This is what DESIGN.md D5/D12 promise: the delete tombstone carries the entity's real pre-delete
/// values, not whatever happened to be sitting in the caller's stub.
/// </para>
/// <para>
/// Runs from <see cref="HistoryWriterInterceptor"/>'s <c>SavingChanges</c>/<c>SavingChangesAsync</c>,
/// after the writer's own transaction (or the caller's ambient one) is open but before EF Core sends
/// the <c>DELETE</c> — the row still exists, and the read happens in the same transaction that is
/// about to delete it. <c>SELECT … FOR UPDATE</c> locks each row until that <c>DELETE</c> commits or
/// rolls back, closing the window where a concurrent transaction could change the row between this
/// read and the delete. If a row is not found — already deleted concurrently, or a stub whose key
/// does not match any row — this throws rather than write an empty/fabricated tombstone.
/// </para>
/// <para>
/// Cost: one extra round trip per <c>SaveChanges</c> that deletes at least one temporal entity, paid
/// even when the entity was already loaded — there is no reliable way to skip it only for the stub
/// case. <c>Added</c>/<c>Modified</c> rows are unaffected: <see cref="HistoryRowPlan.FillGeneratedValues"/>
/// already re-reads <c>CurrentValues</c> after the save, which reflects what was actually just written.
/// </para>
/// </summary>
internal static class DeletedRowSnapshotReader
{
    public static void Read(DbContext context, IReadOnlyList<PendingHistoryRow> rows)
    {
        var deleted = DeletedRows(rows);
        if (deleted is null)
        {
            return;
        }

        var (connection, transaction, sqlHelper) = Resolve(context);
        var templates = new TemplateCache(sqlHelper);

        using var parameterFactory = connection.CreateCommand();
        using var batch = connection.CreateBatch();
        batch.Transaction = transaction;

        foreach (var row in deleted)
        {
            AppendRow(batch, parameterFactory, templates, row);
        }

        using var reader = batch.ExecuteReader();
        Apply(reader, deleted, templates);
    }

    public static async Task ReadAsync(
        DbContext context, IReadOnlyList<PendingHistoryRow> rows, CancellationToken cancellationToken)
    {
        var deleted = DeletedRows(rows);
        if (deleted is null)
        {
            return;
        }

        var (connection, transaction, sqlHelper) = Resolve(context);
        var templates = new TemplateCache(sqlHelper);

        await using var parameterFactory = connection.CreateCommand();
        await using var batch = connection.CreateBatch();
        batch.Transaction = transaction;

        foreach (var row in deleted)
        {
            AppendRow(batch, parameterFactory, templates, row);
        }

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        await ApplyAsync(reader, deleted, templates, cancellationToken);
    }

    private static List<PendingHistoryRow>? DeletedRows(IReadOnlyList<PendingHistoryRow> rows)
    {
        List<PendingHistoryRow>? deleted = null;
        foreach (var row in rows)
        {
            if (row.State == EntityState.Deleted)
            {
                (deleted ??= []).Add(row);
            }
        }

        return deleted;
    }

    private static (DbConnection Connection, DbTransaction? Transaction, ISqlGenerationHelper SqlHelper) Resolve(
        DbContext context)
        => (context.Database.GetDbConnection(),
            context.Database.CurrentTransaction?.GetDbTransaction(),
            context.GetService<ISqlGenerationHelper>());

    private static void AppendRow(DbBatch batch, DbCommand parameterFactory, TemplateCache templates, PendingHistoryRow row)
    {
        var template = templates.For(row.SourceEntityType);

        var command = batch.CreateBatchCommand();
        command.CommandText = template.Sql;

        foreach (var key in template.KeyBindings)
        {
            command.Parameters.Add(Parameter(
                parameterFactory, key.ParameterName, row.Values.GetValueOrDefault(key.ColumnName), key.Mapping));
        }

        batch.BatchCommands.Add(command);
    }

    private static DbParameter Parameter(DbCommand factory, string name, object? value, RelationalTypeMapping mapping)
        => mapping.CreateParameter(factory, name, value);

    private static void Apply(DbDataReader reader, List<PendingHistoryRow> rows, TemplateCache templates)
    {
        foreach (var row in rows)
        {
            if (!reader.Read())
            {
                throw RowNotFound(row);
            }

            ApplyCurrentResult(reader, row, templates.For(row.SourceEntityType));
            reader.NextResult();
        }
    }

    private static async Task ApplyAsync(
        DbDataReader reader, List<PendingHistoryRow> rows, TemplateCache templates, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw RowNotFound(row);
            }

            ApplyCurrentResult(reader, row, templates.For(row.SourceEntityType));
            await reader.NextResultAsync(cancellationToken);
        }
    }

    private static void ApplyCurrentResult(DbDataReader reader, PendingHistoryRow row, SelectTemplate template)
    {
        for (var i = 0; i < template.SelectColumns.Count; i++)
        {
            var (column, mapping) = template.SelectColumns[i];
            var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
            row.Values[column] = mapping.Converter is { } converter ? converter.ConvertFromProvider(raw) : raw;
        }
    }

    private static InvalidOperationException RowNotFound(PendingHistoryRow row)
    {
        var table = row.SourceEntityType.GetTableName();
        return new InvalidOperationException(
            $"Hindsight could not record a delete tombstone for '{row.SourceEntityType.DisplayName()}': "
            + $"no matching row was found in '{table}' when it re-read the row's current values just "
            + "before deleting it. Either another transaction deleted the same row concurrently, or "
            + "the entity was removed without ever being loaded (e.g. Remove(new T { Id = id })) and "
            + "its key does not match any existing row — EF Core's own DELETE would fail the same way. "
            + "Load the entity first (e.g. FindAsync) before removing it.");
    }

    // One parameter name / column / type-mapping triple for the WHERE predicate.
    private readonly record struct ColumnBinding(string ParameterName, string ColumnName, RelationalTypeMapping Mapping);

    // The invariant SQL and bindings for one source entity type, reused for every deleted row of that
    // type within a SaveChanges.
    private sealed class SelectTemplate
    {
        public required string Sql { get; init; }

        public required List<(string Column, RelationalTypeMapping Mapping)> SelectColumns { get; init; }

        public required List<ColumnBinding> KeyBindings { get; init; }
    }

    private sealed class TemplateCache(ISqlGenerationHelper sqlHelper)
    {
        private readonly Dictionary<IEntityType, SelectTemplate> _templates = [];

        public SelectTemplate For(IEntityType entityType)
        {
            if (!_templates.TryGetValue(entityType, out var template))
            {
                template = Build(entityType);
                _templates.Add(entityType, template);
            }

            return template;
        }

        private SelectTemplate Build(IEntityType entityType)
        {
            var table = sqlHelper.DelimitIdentifier(entityType.GetTableName()!, entityType.GetSchema());

            // Mirrors HistoryRowPlan.BuildPending's VersionedColumns filter exactly: an excluded
            // property never becomes a history column, so there is nothing to re-read it for.
            var selectColumns = new List<(string, RelationalTypeMapping)>();
            var columnList = new List<string>();
            foreach (var property in entityType.GetProperties())
            {
                if (property.FindAnnotation(HindsightAnnotationNames.IsExcluded)?.Value is true)
                {
                    continue;
                }

                if (property.GetColumnName() is not { } column)
                {
                    continue;
                }

                columnList.Add(sqlHelper.DelimitIdentifier(column));
                selectColumns.Add((column, property.GetRelationalTypeMapping()));
            }

            var keyBindings = new List<ColumnBinding>();
            var predicates = new List<string>();
            var keyIndex = 0;
            foreach (var keyProperty in entityType.FindPrimaryKey()!.Properties)
            {
                var column = keyProperty.GetColumnName()!;
                var name = sqlHelper.GenerateParameterName("k" + keyIndex++);
                predicates.Add($"{sqlHelper.DelimitIdentifier(column)} = {name}");
                keyBindings.Add(new ColumnBinding(name, column, keyProperty.GetRelationalTypeMapping()));
            }

            var sql =
                $"SELECT {string.Join(", ", columnList)} FROM {table} "
                + $"WHERE {string.Join(" AND ", predicates)} FOR UPDATE";

            return new SelectTemplate
            {
                Sql = sql,
                SelectColumns = selectColumns,
                KeyBindings = keyBindings,
            };
        }
    }
}
