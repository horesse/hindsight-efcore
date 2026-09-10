using Microsoft.EntityFrameworkCore;

namespace Hindsight;

/// <summary>
/// The "who / why" of a <c>SaveChanges</c>, supplied by the application through an
/// <see cref="IChangeContextProvider"/> and written into the context columns of every history row the
/// call produces (DESIGN.md D3, D5). Each member maps to one history column; a <see langword="null"/>
/// member leaves that column <see langword="null"/>. Use <see cref="Empty"/> when there is nothing to
/// record.
/// </summary>
public sealed record ChangeContext
{
    /// <summary>
    /// A <see cref="ChangeContext"/> with every member <see langword="null"/>: the history rows get
    /// <see langword="null"/> in all context columns. Returned by an <see cref="IChangeContextProvider"/>
    /// that has nothing to contribute for the current <c>SaveChanges</c>.
    /// </summary>
    public static ChangeContext Empty { get; } = new();

    /// <summary>
    /// Application identifier of the user who made the change, written to <c>changed_by</c>. A
    /// <see cref="string"/> so the application is free to use a GUID, a numeric id, an email or a
    /// subject claim without Hindsight imposing a shape.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>Display name of the user who made the change, written to <c>changed_by_name</c>.</summary>
    public string? UserName { get; init; }

    /// <summary>
    /// Correlation identifier for the unit of work (for example a trace id or a request id), written
    /// to <c>correlation_id</c>.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Free-text reason for the change, written to <c>reason</c>. A <see cref="DbContext"/> scope
    /// opened with <see cref="HindsightDbContextExtensions.WithReason(DbContext, string)"/> overrides
    /// this value for the <c>SaveChanges</c> calls inside it.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Structured context as a JSON document, written verbatim to the <c>extra</c> <c>jsonb</c>
    /// column. The provider is responsible for serialization; the string must be valid JSON or
    /// PostgreSQL rejects the <c>SaveChanges</c>.
    /// </summary>
    public string? Extra { get; init; }
}
