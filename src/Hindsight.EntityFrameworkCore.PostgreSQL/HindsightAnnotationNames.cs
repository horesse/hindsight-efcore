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
}
