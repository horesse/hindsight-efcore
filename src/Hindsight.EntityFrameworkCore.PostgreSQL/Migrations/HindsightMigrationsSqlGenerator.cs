using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Migrations;

/// <summary>
/// A decorator over the provider's <see cref="IMigrationsSqlGenerator"/> that, from the same
/// <c>Hindsight:*</c> annotations the history table is built from, appends every history table's
/// period-range index (DESIGN.md D5) right after its <c>CreateTableOperation</c> — in both writer modes,
/// since the index serves <c>AsOf</c> / <c>History&lt;T&gt;</c> queries regardless of which writer
/// produced the rows. In <see cref="HistoryWriter.Trigger"/> mode it additionally appends the
/// <c>&lt;history_table&gt;_write()</c> function and <c>&lt;history_table&gt;_trg</c> trigger after the
/// same <c>CreateTableOperation</c>, re-emits <c>CREATE OR REPLACE FUNCTION</c> whenever a column is
/// added to, dropped from or renamed on the temporal entity, and drops the function — <c>CASCADE</c>,
/// taking the trigger on the main table with it — when the history table is dropped, or when its entity
/// type stops being temporal without the table itself being dropped
/// (<see cref="HindsightAnnotationNames.OrphanedTriggerPending"/>). It also appends, right after the same
/// <c>CreateTableOperation</c> (after the trigger, when one is also emitted), an
/// <c>INSERT INTO ... SELECT ...</c> that seeds an initial history version for every row already in the
/// main table — unconditionally, in both writer modes (DESIGN.md D6, "Existing non-empty table made
/// temporal"; <see cref="HistorySeedSqlGenerator"/>). When the model has the retention-horizon table
/// (DESIGN.md D19), it appends the <c>hindsight_history_retained</c> guard function after that table's
/// <c>CreateTableOperation</c>, and moves the function when the table moves to another schema.
/// </summary>
/// <remarks>
/// It is a decorator, not a subclass of <c>NpgsqlMigrationsSqlGenerator</c>: that type's only public
/// constructor takes an <c>Npgsql...Infrastructure.Internal</c> option, which golden rule 1 forbids
/// binding to. The extra DDL is injected as plain <see cref="SqlOperation"/> entries in the operation
/// list, then the whole list is handed to the inner generator — no <c>protected</c> override, no
/// internal type named here. Everything needed is read from the <see cref="IModel"/> passed to
/// <see cref="Generate"/>; annotations reach it verbatim on both the design-time model and a migration's
/// compiled <c>TargetModel</c> (DESIGN.md D6). The period-range index is
/// created exactly once, alongside the table: a history table's period columns and their types never
/// change after creation, so — unlike the trigger function — there is nothing to ever re-emit for it, and
/// a de-temporalized entity's history table (D6) keeps its index for free, because D6 never drops or
/// recreates that table in the first place. The seeding statement is the same: emitted exactly once, next
/// to the table's own creation, and never replayed by a later migration — a table already made temporal
/// keeps growing its history from that point through the ordinary writer path, not through seeding again.
/// </remarks>
internal sealed class HindsightMigrationsSqlGenerator(
    IMigrationsSqlGenerator inner,
    ISqlGenerationHelper sqlGenerationHelper,
    HistoryWriter historyWriter) : IMigrationsSqlGenerator
{
    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var indexes = model is null ? [] : CollectPeriodIndexModels(model);
        var seeds = model is null ? [] : CollectSeedModels(model);
        var triggers = model is null || historyWriter != HistoryWriter.Trigger ? [] : CollectTriggerModels(model);
        var orphanedDrops = model is null || historyWriter != HistoryWriter.Trigger
            ? []
            : CollectOrphanedTriggerDrops(model);
        var retentionHorizon = model is null ? null : FindRetentionHorizonTable(model);
        if (indexes.Count == 0 && seeds.Count == 0 && triggers.Count == 0 && orphanedDrops.Count == 0
            && retentionHorizon is null)
        {
            return inner.Generate(operations, model, options);
        }

        return inner.Generate(
            Rewrite(operations, indexes, seeds, triggers, orphanedDrops, retentionHorizon), model, options);
    }

    // DESIGN.md D19: the retention-horizon table, present once some entity opts in with WithRetention().
    // Its guard function is created right after it, in both writer modes — the function serves queries,
    // not writes.
    private static (string? Schema, string Table)? FindRetentionHorizonTable(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType[HindsightAnnotationNames.IsRetentionHorizonTable] is true && entityType.GetTableName() is { } table)
            {
                return (entityType.GetSchema(), table);
            }
        }

        return null;
    }

    // Every live history table (DESIGN.md D5): the period-range index has nothing to do with which
    // writer is configured, so — unlike CollectTriggerModels — this is never gated on historyWriter.
    private static List<HistoryPeriodIndexModel> CollectPeriodIndexModels(IModel model)
    {
        var indexes = new List<HistoryPeriodIndexModel>();

        foreach (var history in model.GetEntityTypes())
        {
            if (history[HindsightAnnotationNames.IsHistoryTable] is not true || history.GetTableName() is not { } historyTable)
            {
                continue;
            }

            var source = model.GetEntityTypes().FirstOrDefault(
                entityType => (string?)entityType[HindsightAnnotationNames.HistoryEntityType] == history.Name);
            if (source is null)
            {
                // Orphaned (DESIGN.md D6): its CreateTableOperation, and with it this index, was already
                // emitted back when the entity type was still live — nothing to do this build.
                continue;
            }

            var periodStart = (string?)source[HindsightAnnotationNames.PeriodStartColumnName]
                ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName;
            var periodEnd = (string?)source[HindsightAnnotationNames.PeriodEndColumnName]
                ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName;

            indexes.Add(new HistoryPeriodIndexModel(historyTable, history.GetSchema(), periodStart, periodEnd));
        }

        return indexes;
    }

    // Every live history table (DESIGN.md D6): seeding, like the period-range index above, has nothing
    // to do with which writer is configured — it is a plain INSERT...SELECT against the main table, not
    // something either writer executes — so this is never gated on historyWriter either. Unconditional by
    // design: a brand-new entity's main table is created empty in the same migration (the SELECT returns
    // zero rows, a harmless no-op), and an existing entity that just had IsTemporal() added is exactly the
    // case this seeds for. There is no attempt to tell the two apart.
    private static List<HistorySeedModel> CollectSeedModels(IModel model)
    {
        var seeds = new List<HistorySeedModel>();

        foreach (var history in model.GetEntityTypes())
        {
            if (history[HindsightAnnotationNames.IsHistoryTable] is not true || history.GetTableName() is not { } historyTable)
            {
                continue;
            }

            var source = model.GetEntityTypes().FirstOrDefault(
                entityType => (string?)entityType[HindsightAnnotationNames.HistoryEntityType] == history.Name);
            if (source is null || source.GetTableName() is not { } mainTable)
            {
                // Orphaned (DESIGN.md D6): its CreateTableOperation — and with it any seeding — already
                // happened back when the entity type was still live; nothing to do this build.
                continue;
            }

            var periodStart = (string?)source[HindsightAnnotationNames.PeriodStartColumnName]
                ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName;
            var periodEnd = (string?)source[HindsightAnnotationNames.PeriodEndColumnName]
                ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName;

            seeds.Add(new HistorySeedModel(
                historyTable,
                history.GetSchema(),
                mainTable,
                source.GetSchema(),
                CollectEntityColumns(history, periodStart, periodEnd),
                periodStart,
                periodEnd));
        }

        return seeds;
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

    // Shared by BuildModel (Trigger mode's versioned column set) and CollectSeedModels (both modes): the
    // columns a history table mirrors from its source, minus the fixed Hindsight columns (surrogate key,
    // operation, change context, the opt-in db_session_user), the period columns, and any column orphaned
    // by DESIGN.md D6 (no matching column on the main table to read NEW/OLD or SELECT from).
    private static List<string> CollectEntityColumns(IEntityType history, string periodStart, string periodEnd)
    {
        var fixedColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            HindsightHistoryColumns.HistoryId,
            HindsightHistoryColumns.Operation,
            HindsightHistoryColumns.ChangedBy,
            HindsightHistoryColumns.ChangedByName,
            HindsightHistoryColumns.CorrelationId,
            HindsightHistoryColumns.Reason,
            HindsightHistoryColumns.Extra,
            HindsightHistoryColumns.DbSessionUser,
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

        return entityColumns;
    }

    private static HistoryTriggerModel BuildModel(IEntityType history, IEntityType source)
    {
        var periodStart = (string?)source[HindsightAnnotationNames.PeriodStartColumnName]
            ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName;
        var periodEnd = (string?)source[HindsightAnnotationNames.PeriodEndColumnName]
            ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName;

        var entityColumns = CollectEntityColumns(history, periodStart, periodEnd);

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
        List<HistoryPeriodIndexModel> indexes,
        List<HistorySeedModel> seeds,
        List<HistoryTriggerModel> triggers,
        List<(string HistoryTable, string? HistorySchema)> orphanedDrops,
        (string? Schema, string Table)? retentionHorizon)
    {
        var byHistoryTableIndex = indexes.ToDictionary(index => (index.HistorySchema, index.HistoryTable));
        var byHistoryTableSeed = seeds.ToDictionary(seed => (seed.HistorySchema, seed.HistoryTable));
        var byHistoryTable = triggers.ToDictionary(trigger => (trigger.HistorySchema, trigger.HistoryTable));
        var byMainTable = triggers.ToDictionary(trigger => (trigger.MainSchema, trigger.MainTable));

        var result = new List<MigrationOperation>(
            operations.Count + (indexes.Count + seeds.Count + (triggers.Count * 2)));
        var createdOrDropped = new HashSet<(string?, string)>();
        var needsRefresh = new List<HistoryTriggerModel>();
        var renamedHistoryTables = new List<(string? OldSchema, string OldName, HistoryPeriodIndexModel Model)>();

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
                    when retentionHorizon is var (horizonSchema, horizonTable)
                        && create.Schema == horizonSchema
                        && create.Name == horizonTable:
                    result.Add(new SqlOperation
                    {
                        Sql = RetentionSqlGenerator.CreateFunction(horizonSchema, sqlGenerationHelper),
                    });
                    break;

                // The model's default schema changed, and the horizon table moved with it: the function
                // is a standalone object that ALTER TABLE ... SET SCHEMA leaves behind, and the query's
                // DbFunction mapping now names it in the new schema.
                case RenameTableOperation moveHorizon
                    when retentionHorizon is var (movedSchema, movedTable)
                        && (moveHorizon.NewSchema ?? moveHorizon.Schema) == movedSchema
                        && (moveHorizon.NewName ?? moveHorizon.Name) == movedTable:
                    result.Add(new SqlOperation
                    {
                        Sql = RetentionSqlGenerator.DropFunction(moveHorizon.Schema, sqlGenerationHelper),
                    });
                    result.Add(new SqlOperation
                    {
                        Sql = RetentionSqlGenerator.CreateFunction(movedSchema, sqlGenerationHelper),
                    });
                    break;

                case CreateTableOperation create
                    when byHistoryTableIndex.ContainsKey((create.Schema, create.Name))
                        || byHistoryTable.ContainsKey((create.Schema, create.Name))
                        || byHistoryTableSeed.ContainsKey((create.Schema, create.Name)):
                    if (byHistoryTableIndex.TryGetValue((create.Schema, create.Name), out var index))
                    {
                        result.Add(new SqlOperation
                        {
                            Sql = HistoryIndexSqlGenerator.CreatePeriodRangeIndex(index, sqlGenerationHelper),
                        });
                    }

                    if (byHistoryTable.TryGetValue((create.Schema, create.Name), out var created))
                    {
                        result.Add(new SqlOperation
                        {
                            Sql = HistoryTriggerSqlGenerator.CreateFunction(created, sqlGenerationHelper),
                        });
                        result.Add(new SqlOperation
                        {
                            Sql = HistoryTriggerSqlGenerator.CreateTrigger(created, sqlGenerationHelper),
                        });
                    }

                    // Seeding goes last: after the index (needed by the writer paths, not by this
                    // statement itself) and, in Trigger mode, after the trigger — matching the order the
                    // manual workaround this automates always documented (create the table, then seed it).
                    if (byHistoryTableSeed.TryGetValue((create.Schema, create.Name), out var seed))
                    {
                        result.Add(new SqlOperation
                        {
                            Sql = HistorySeedSqlGenerator.SeedInitialVersions(seed, sqlGenerationHelper),
                        });
                    }

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

                case RenameTableOperation renameTable
                    when renameTable.NewName is { } newTableName
                        && byHistoryTableIndex.TryGetValue((renameTable.NewSchema ?? renameTable.Schema, newTableName), out var renamedIndex):
                    // The history table itself was renamed (DESIGN.md D15: its identity in the model is
                    // stable, so a rename of the mapped table now reaches here instead of looking like an
                    // unrelated table dropping and appearing). Matched against byHistoryTableIndex — built
                    // regardless of writer mode, unlike byHistoryTable — because the period-range index
                    // exists in both writer modes and needs renaming in both; the Trigger-mode function and
                    // trigger, recreated below, are the writer-specific part of this same rename.
                    // Recreating the trigger has to wait until every operation has run (below): CREATE
                    // TRIGGER ... ON <main table> names the main table by its current (post-migration) name,
                    // and when the SAME migration also renames the main table, that rename's own
                    // RenameTableOperation can appear later in this list — emitting CREATE TRIGGER here, in
                    // place, would reference a table that doesn't exist under that name yet.
                    renamedHistoryTables.Add((renameTable.Schema, renameTable.Name, renamedIndex));
                    createdOrDropped.Add((renameTable.NewSchema ?? renameTable.Schema, newTableName));

                    // A main-table rename needs nothing here: the function body never references the
                    // main table by name (only NEW/OLD), and PostgreSQL tracks a trigger by the
                    // relation's OID, so it keeps firing on the renamed table with no DDL of its own —
                    // verified against real PostgreSQL (DESIGN.md D15).
                    break;
            }
        }

        // The history table itself was renamed — its name, its schema, or both (a RenameTableOperation
        // covers a pure schema move too: same Name, only NewSchema differs). In Trigger mode, its trigger
        // function's own name and its INSERT INTO target both embed the OLD table name literally, baked
        // in at the last CreateFunction (D3) — left alone, the next write would fail with "relation ...
        // does not exist". Drop the stale function (CASCADE takes the trigger that rode along with the
        // table rename) and recreate both under the new name/schema, only now that every rename in this
        // migration (including the main table's, if it has one of its own) has already run. A function is
        // its own standalone object, independent of the table that references it from a trigger — moving
        // the table's schema does not move the function — so the function is correctly found (to drop)
        // under the OLD schema and created under the NEW one.
        //
        // Then, in both writer modes, rename the period-range index: PostgreSQL's ALTER TABLE ... RENAME
        // leaves it under its old, now-stale name (verified against real PostgreSQL) — left alone, that
        // stale name becomes a landmine for the next entity whose default-derived history table name
        // happens to collide with it. Unlike the function, an index is NOT a standalone object: ALTER
        // TABLE ... SET SCHEMA moves it (and every other object owned by the table) into the new schema
        // together with the table itself, automatically, with no DDL of its own — confirmed against real
        // PostgreSQL: by the time this statement runs (after the table's own RenameTableOperation, earlier
        // in this same migration), the index already lives in the NEW schema even though it kept its OLD
        // name. So, unlike the function drop above, the index must be located under the NEW schema
        // (model.HistorySchema) — looking it up under the old one fails with "relation ... does not exist"
        // for a migration that changes schema, whether or not it also changes the name.
        foreach (var (oldSchema, oldName, model) in renamedHistoryTables)
        {
            if (byHistoryTable.TryGetValue((model.HistorySchema, model.HistoryTable), out var trigger))
            {
                result.Add(new SqlOperation
                {
                    Sql = HistoryTriggerSqlGenerator.DropFunction(oldName, oldSchema, sqlGenerationHelper),
                });
                result.Add(new SqlOperation
                {
                    Sql = HistoryTriggerSqlGenerator.CreateFunction(trigger, sqlGenerationHelper),
                });
                result.Add(new SqlOperation
                {
                    Sql = HistoryTriggerSqlGenerator.CreateTrigger(trigger, sqlGenerationHelper),
                });
            }

            // A pure schema move (RenameTableOperation.Name unchanged, only NewSchema differs) needs no
            // ALTER INDEX at all: the index's name is still exactly IndexName(oldName) == IndexName(model.
            // HistoryTable), and PostgreSQL already relocated it with the table. Renaming an index to its
            // own current name fails ("relation ... already exists", verified against real PostgreSQL) —
            // it is a rename, not an idempotent "ensure named" operation.
            if (!string.Equals(oldName, model.HistoryTable, StringComparison.Ordinal))
            {
                result.Add(new SqlOperation
                {
                    Sql = HistoryIndexSqlGenerator.RenamePeriodRangeIndex(
                        oldName, model.HistorySchema, model.HistoryTable, sqlGenerationHelper),
                });
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
