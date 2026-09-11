namespace Hindsight;

/// <summary>
/// Names of the model annotations Hindsight writes onto entity types and properties.
/// The convention that builds history entity types reads these back at model finalization.
/// </summary>
public static class HindsightAnnotationNames
{
    /// <summary>Prefix shared by every Hindsight annotation.</summary>
    public const string Prefix = "Hindsight:";

    /// <summary>Marks an entity type as system-versioned. Value: <see langword="true"/>.</summary>
    public const string IsTemporal = Prefix + "IsTemporal";

    /// <summary>Name of the history table. Value: <see cref="string"/>.</summary>
    public const string HistoryTableName = Prefix + "HistoryTableName";

    /// <summary>Schema of the history table. Value: <see cref="string"/> or <see langword="null"/>.</summary>
    public const string HistoryTableSchema = Prefix + "HistoryTableSchema";

    /// <summary>Column name for the start of the system-time period. Value: <see cref="string"/>.</summary>
    public const string PeriodStartColumnName = Prefix + "PeriodStart";

    /// <summary>Column name for the end of the system-time period. Value: <see cref="string"/>.</summary>
    public const string PeriodEndColumnName = Prefix + "PeriodEnd";

    /// <summary>
    /// Marks a property as excluded from versioning. Changes to excluded properties alone do not
    /// produce a history row. Value: <see langword="true"/>.
    /// </summary>
    public const string IsExcluded = Prefix + "IsExcluded";

    /// <summary>
    /// On a temporal entity type: the shared-type name of its generated history entity type.
    /// Written by the model-finalizing convention. Value: <see cref="string"/>.
    /// </summary>
    internal const string HistoryEntityType = Prefix + "HistoryEntityType";

    /// <summary>
    /// Marks the generated property-bag entity type as a Hindsight history table.
    /// Value: <see langword="true"/>.
    /// </summary>
    internal const string IsHistoryTable = Prefix + "IsHistoryTable";

    /// <summary>
    /// On a generated history property that is no longer backed by a live entity property: marks the
    /// column as retained from a previous model version (DESIGN.md D6). The convention re-adds such a
    /// column, nullable, on every build by reading the previous <c>ModelSnapshot</c>, so the migrations
    /// differ never drops it. Also valid on a generated history entity type itself, when its source
    /// entity is no longer temporal at all (<c>IsTemporal()</c> removed, or the entity type removed
    /// from the model) — the whole history entity type is cloned back from the snapshot instead of
    /// just one column, same reasoning, same guarantee. Value: <see langword="true"/>.
    /// </summary>
    internal const string Orphaned = Prefix + "Orphaned";

    /// <summary>
    /// On a generated history entity type: set for exactly one migration — the one where its source
    /// entity type stopped being temporal (<see cref="Orphaned"/> just became <see langword="true"/>
    /// for it, having not been before). In <see cref="HistoryWriter.Trigger"/> mode the physical trigger
    /// lives on the main table, not the history table, and the model diff between the temporal and
    /// de-temporalized versions has nothing to say about the now-orphaned history table (that is the
    /// point of <see cref="Orphaned"/>) — so nothing else would ever tell the migrations SQL generator to
    /// drop the now-stale trigger. Read once by <c>HindsightMigrationsSqlGenerator</c> to emit that drop,
    /// then never set again on any later migration for the same history entity type. Value:
    /// <see langword="true"/>.
    /// </summary>
    internal const string OrphanedTriggerPending = Prefix + "OrphanedTriggerPending";
}
