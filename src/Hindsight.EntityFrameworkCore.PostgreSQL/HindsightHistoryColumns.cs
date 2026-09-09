namespace Hindsight;

/// <summary>
/// Column names Hindsight adds to every history table (DESIGN.md D5). Unlike the period columns
/// (configurable via <see cref="TemporalEntityTypeBuilder{TEntity}.HasPeriodStart(string)"/> and
/// <see cref="TemporalEntityTypeBuilder{TEntity}.HasPeriodEnd(string)"/>), these are fixed.
/// </summary>
internal static class HindsightHistoryColumns
{
    /// <summary>Surrogate primary key of the history table: <c>bigint generated always as identity</c>.</summary>
    internal const string HistoryId = "history_id";

    /// <summary>The kind of change the row records: <c>1</c> insert, <c>2</c> update, <c>3</c> delete.</summary>
    internal const string Operation = "operation";

    /// <summary>Application-supplied identifier of the user who made the change.</summary>
    internal const string ChangedBy = "changed_by";

    /// <summary>Application-supplied display name of the user who made the change.</summary>
    internal const string ChangedByName = "changed_by_name";

    /// <summary>Application-supplied correlation identifier for the transaction.</summary>
    internal const string CorrelationId = "correlation_id";

    /// <summary>Application-supplied free-text reason for the change.</summary>
    internal const string Reason = "reason";

    /// <summary>Application-supplied structured context, stored as <c>jsonb</c>.</summary>
    internal const string Extra = "extra";
}
