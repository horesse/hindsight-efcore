using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Migrations;

/// <summary>
/// The <see cref="HistoryWriter.Trigger"/> half of "migrations come for free" (DESIGN.md D2, D3):
/// a decorator over the provider's <see cref="IMigrationsSqlGenerator"/> that, from the same
/// <c>Hindsight:*</c> annotations the history table is built from, appends the
/// <c>&lt;history_table&gt;_write()</c> function and <c>&lt;history_table&gt;_trg</c> trigger after the
/// history table is created, re-emits <c>CREATE OR REPLACE FUNCTION</c> whenever a column is added to,
/// dropped from or renamed on the temporal entity, and drops the function — <c>CASCADE</c>, taking the
/// trigger on the main table with it — when the history table is dropped, or when its entity type stops
/// being temporal without the table itself being dropped (<see cref="HindsightAnnotationNames.OrphanedTriggerPending"/>).
/// </summary>
/// <remarks>
/// It is a decorator, not a subclass of <c>NpgsqlMigrationsSqlGenerator</c>: that type's only public
/// constructor takes an <c>Npgsql...Infrastructure.Internal</c> option, which golden rule 1 forbids
/// binding to. The extra DDL is injected as plain <see cref="SqlOperation"/> entries in the operation
/// list, then the whole list is handed to the inner generator — no <c>protected</c> override, no
/// internal type named here. Everything needed is read from the <see cref="IModel"/> passed to
/// <see cref="Generate"/>; annotations reach it verbatim on both the design-time model and a migration's
/// compiled <c>TargetModel</c> (DESIGN.md D6, resolved by spike 2026-09-11).
/// </remarks>
internal sealed class HindsightMigrationsSqlGenerator(
    IMigrationsSqlGenerator inner,
    ISqlGenerationHelper sqlGenerationHelper) : IMigrationsSqlGenerator
{
    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var triggers = model is null ? [] : CollectTriggerModels(model);
        var orphanedDrops = model is null ? [] : CollectOrphanedTriggerDrops(model);
        if (triggers.Count == 0 && orphanedDrops.Count == 0)
        {
            return inner.Generate(operations, model, options);
        }

        return inner.Generate(Rewrite(operations, triggers, orphanedDrops), model, options);
    }

    // A history entity type whose source stopped being temporal this build (DESIGN.md D6): no live
    // source is left to match it against in CollectTriggerModels, so the differ has nothing to say
    // about it and the trigger on the main table would otherwise never be dropped.
    private static List<(string HistoryTable, string? HistorySchema)> CollectOrphanedTriggerDrops(IModel model)
    {
        var drops = new List<(string, string?)>();

        foreach (var history in model.GetEntityTypes())
        {
            if (history[HindsightAnnotationNames.IsHistoryTable] is true
                && history[HindsightAnnotationNames.OrphanedTriggerPending] is true
                && history.GetTableName() is { } tableName)
            {
                drops.Add((tableName, history.GetSchema()));
            }
        }

        return drops;
    }

    private static List<HistoryTriggerModel> CollectTriggerModels(IModel model)
    {
        var triggers = new List<HistoryTriggerModel>();

        foreach (var history in model.GetEntityTypes())
        {
            if (history[HindsightAnnotationNames.IsHistoryTable] is not true)
            {
                continue;
            }

            var source = model.GetEntityTypes().FirstOrDefault(
                entityType => (string?)entityType[HindsightAnnotationNames.HistoryEntityType] == history.Name);
            if (source?.FindPrimaryKey() is null || history.GetTableName() is null || source.GetTableName() is null)
            {
                continue;
            }

            triggers.Add(BuildModel(history, source));
        }

        return triggers;
    }

    private static HistoryTriggerModel BuildModel(IEntityType history, IEntityType source)
    {
        var periodStart = (string?)source[HindsightAnnotationNames.PeriodStartColumnName]
            ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName;
        var periodEnd = (string?)source[HindsightAnnotationNames.PeriodEndColumnName]
            ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName;

        var fixedColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            HindsightHistoryColumns.HistoryId,
            HindsightHistoryColumns.Operation,
            HindsightHistoryColumns.ChangedBy,
            HindsightHistoryColumns.ChangedByName,
            HindsightHistoryColumns.CorrelationId,
            HindsightHistoryColumns.Reason,
            HindsightHistoryColumns.Extra,
            periodStart,
            periodEnd,
        };

        var entityColumns = new List<string>();
        foreach (var property in history.GetProperties())
        {
            if (property[HindsightAnnotationNames.Orphaned] is true)
            {
                continue;
            }

            if (property.GetColumnName() is { } column && !fixedColumns.Contains(column))
            {
                entityColumns.Add(column);
            }
        }

        var keyColumns = new List<string>();
        foreach (var keyProperty in source.FindPrimaryKey()!.Properties)
        {
            if (keyProperty.GetColumnName() is { } column && history.FindProperty(column) is not null)
            {
                keyColumns.Add(column);
            }
        }

        return new HistoryTriggerModel(
            history.GetTableName()!,
            history.GetSchema(),
            source.GetTableName()!,
            source.GetSchema(),
            entityColumns,
            keyColumns,
            periodStart,
            periodEnd);
    }

    private List<MigrationOperation> Rewrite(
        IReadOnlyList<MigrationOperation> operations,
        List<HistoryTriggerModel> triggers,
        List<(string HistoryTable, string? HistorySchema)> orphanedDrops)
    {
        var byHistoryTable = triggers.ToDictionary(trigger => (trigger.HistorySchema, trigger.HistoryTable));
        var byMainTable = triggers.ToDictionary(trigger => (trigger.MainSchema, trigger.MainTable));

        var result = new List<MigrationOperation>(operations.Count + (triggers.Count * 2));
        var createdOrDropped = new HashSet<(string?, string)>();
        var needsRefresh = new List<HistoryTriggerModel>();

        void MarkRefresh(HistoryTriggerModel model)
        {
            if (!needsRefresh.Contains(model))
            {
                needsRefresh.Add(model);
            }
        }

        foreach (var operation in operations)
        {
            // DROP FUNCTION ... CASCADE goes out before the history table is dropped.
            if (operation is DropTableOperation drop
                && byHistoryTable.TryGetValue((drop.Schema, drop.Name), out var dropped))
            {
                result.Add(new SqlOperation
                {
                    Sql = HistoryTriggerSqlGenerator.DropFunction(dropped, sqlGenerationHelper),
                });
                createdOrDropped.Add((drop.Schema, drop.Name));
                result.Add(operation);
                continue;
            }

            result.Add(operation);

            switch (operation)
            {
                case CreateTableOperation create
                    when byHistoryTable.TryGetValue((create.Schema, create.Name), out var created):
                    result.Add(new SqlOperation
                    {
                        Sql = HistoryTriggerSqlGenerator.CreateFunction(created, sqlGenerationHelper),
                    });
                    result.Add(new SqlOperation
                    {
                        Sql = HistoryTriggerSqlGenerator.CreateTrigger(created, sqlGenerationHelper),
                    });
                    createdOrDropped.Add((create.Schema, create.Name));
                    break;

                case ColumnOperation column:
                    if (byHistoryTable.TryGetValue((column.Schema, column.Table), out var fromHistory))
                    {
                        MarkRefresh(fromHistory);
                    }
                    else if (byMainTable.TryGetValue((column.Schema, column.Table), out var fromMain))
                    {
                        MarkRefresh(fromMain);
                    }

                    break;

                case RenameColumnOperation rename:
                    if (byHistoryTable.TryGetValue((rename.Schema, rename.Table), out var renamedHistory))
                    {
                        MarkRefresh(renamedHistory);
                    }
                    else if (byMainTable.TryGetValue((rename.Schema, rename.Table), out var renamedMain))
                    {
                        MarkRefresh(renamedMain);
                    }

                    break;
            }
        }

        // The versioned column set changed: rebuild the function body once, after the column DDL, for
        // every affected history table that was not created or dropped in this same batch (DESIGN.md D3).
        foreach (var model in needsRefresh)
        {
            if (createdOrDropped.Contains((model.HistorySchema, model.HistoryTable)))
            {
                continue;
            }

            result.Add(new SqlOperation
            {
                Sql = HistoryTriggerSqlGenerator.CreateFunction(model, sqlGenerationHelper),
            });
        }

        // A history table orphaned this build (its source just stopped being temporal): the differ saw
        // no operation for it at all, so the drop is appended unconditionally rather than keyed off one.
        foreach (var (historyTable, historySchema) in orphanedDrops)
        {
            result.Add(new SqlOperation
            {
                Sql = HistoryTriggerSqlGenerator.DropFunction(historyTable, historySchema, sqlGenerationHelper),
            });
        }

        return result;
    }
}
