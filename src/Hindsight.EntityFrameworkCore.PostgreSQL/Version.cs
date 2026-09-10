namespace Hindsight;

/// <summary>
/// The kind of change a stored history version records, mirrored from the history table's
/// <c>operation</c> column (DESIGN.md D5). Returned on <see cref="Version{TEntity}.Operation"/> by
/// <see cref="HindsightQueryableExtensions.History{TEntity}"/>.
/// </summary>
public enum VersionOperation : short
{
    /// <summary>The row was created. The version's period opens here and nothing was closed.</summary>
    Insert = 1,

    /// <summary>A versioned column changed. The previous version was closed and this one opened.</summary>
    Update = 2,

    /// <summary>
    /// The row was deleted. This is the tombstone (DESIGN.md D5): an empty interval
    /// <c>[valid_from, valid_to)</c> with <c>valid_from == valid_to</c>, carrying the entity's last
    /// column values and the change context of the delete. It is a delete marker, not a state
    /// snapshot, so <see cref="HindsightQueryableExtensions.AllVersions{TEntity}"/> omits it while
    /// <see cref="HindsightQueryableExtensions.History{TEntity}"/> returns it.
    /// </summary>
    Delete = 3,
}

/// <summary>
/// One stored version of a <typeparamref name="TEntity"/> read from its Hindsight history table by
/// <see cref="HindsightQueryableExtensions.History{TEntity}"/>: the entity snapshot for that version
/// plus the version's system-time period and change-context metadata. Use it when you need the
/// <i>when</i> / <i>who</i> / <i>why</i> of a change, not just the values — an audit trail, a "who
/// last touched this" screen, or a delete log.
/// </summary>
/// <remarks>
/// A <see cref="Version{TEntity}"/> is a detached, no-tracking projection (DESIGN.md D7); it is not a
/// mapped entity type and is never tracked or saved back. Instances are produced only by
/// <see cref="HindsightQueryableExtensions.History{TEntity}"/>.
/// </remarks>
/// <typeparam name="TEntity">The temporal entity type the version belongs to.</typeparam>
public sealed class Version<TEntity>
    where TEntity : class
{
    /// <summary>
    /// The entity as it stood for this version, reconstructed from the history row's versioned
    /// columns. For a <see cref="VersionOperation.Delete"/> tombstone these are the values the row
    /// held immediately before it was deleted.
    /// </summary>
    public required TEntity Entity { get; init; }

    /// <summary>
    /// Inclusive start of the version's system-time period (UTC). The instant the row began to hold
    /// these values in the database.
    /// </summary>
    public DateTimeOffset ValidFrom { get; init; }

    /// <summary>
    /// Exclusive end of the version's system-time period (UTC), half-open <c>[ValidFrom, ValidTo)</c>.
    /// For the version that is currently live this is <see cref="DateTimeOffset.MaxValue"/> (the
    /// history table stores <c>'infinity'</c>); see <see cref="IsCurrent"/>. For a
    /// <see cref="VersionOperation.Delete"/> tombstone it equals <see cref="ValidFrom"/> — an empty
    /// interval.
    /// </summary>
    public DateTimeOffset ValidTo { get; init; }

    /// <summary>
    /// <see langword="true"/> when this version is the one currently live in the main table — its
    /// period is still open (<see cref="ValidTo"/> is <see cref="DateTimeOffset.MaxValue"/>). Always
    /// <see langword="false"/> for a closed version and for a <see cref="VersionOperation.Delete"/>
    /// tombstone.
    /// </summary>
    public bool IsCurrent => ValidTo == DateTimeOffset.MaxValue;

    /// <summary>The kind of change that produced this version.</summary>
    public VersionOperation Operation { get; init; }

    /// <summary>
    /// Application-supplied identifier of the user who made the change, from the <c>changed_by</c>
    /// column; <see langword="null"/> when no <see cref="IChangeContextProvider"/> supplied one.
    /// </summary>
    public string? ChangedBy { get; init; }

    /// <summary>
    /// Application-supplied display name of the user who made the change, from the
    /// <c>changed_by_name</c> column; <see langword="null"/> when none was supplied.
    /// </summary>
    public string? ChangedByName { get; init; }

    /// <summary>
    /// Application-supplied correlation identifier for the transaction, from the <c>correlation_id</c>
    /// column; <see langword="null"/> when none was supplied.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Application-supplied free-text reason for the change, from the <c>reason</c> column;
    /// <see langword="null"/> when none was supplied.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Application-supplied structured context as a JSON string, from the <c>extra</c> <c>jsonb</c>
    /// column; <see langword="null"/> when none was supplied.
    /// </summary>
    public string? Extra { get; init; }
}
