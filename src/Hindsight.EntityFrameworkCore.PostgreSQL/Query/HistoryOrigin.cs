using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Hindsight.Query;

/// <summary>
/// Marks the entity instances produced by an <c>AsOf()</c> / <c>AllVersions()</c> / <c>History&lt;T&gt;()</c>
/// query so <see cref="Writers.HistorySnapshotGuardInterceptor"/> can reject a <c>SaveChanges</c> that
/// tries to write one back (DESIGN.md D7). <see cref="HindsightQueryExpressionInterceptor"/> appends a
/// trailing client-evaluated <c>Select(<see cref="Tag{T}(T)"/>)</c> to a rewritten history query whose
/// result is the entity (or a <see cref="Version{T}"/> of it), so the mark is set as each instance
/// leaves the query — without a wrapper inside the projection, which would stop <c>Where</c> /
/// <c>OrderBy</c> / <c>Select</c> over the entity's members from translating.
/// </summary>
/// <remarks>
/// The marks live in a <see cref="ConditionalWeakTable{TKey, TValue}"/>: they add no field to the
/// user's type, do not keep the instance alive, and survive <c>ChangeTracker.Clear()</c>.
/// </remarks>
internal static class HistoryOrigin
{
    // Value is unused — this is a set. ConditionalWeakTable is the only GC-friendly identity set in
    // the BCL (keys compared by reference, entries released when the instance is collected).
    private static readonly ConditionalWeakTable<object, object?> _fromHistory = new();

    /// <summary>
    /// Records <paramref name="entity"/> as reconstructed from history and returns it unchanged.
    /// <see cref="HindsightQueryExpressionInterceptor"/> emits a call to this in a projection lambda
    /// that EF Core compiles into the query's shaper. Effectively internal — <see cref="HistoryOrigin"/>
    /// is an internal type; declared <see langword="public"/> only so a default-binding-flags
    /// <c>GetMethod</c> in <see cref="HistoryOriginTagger"/> can find it.
    /// </summary>
    [return: NotNullIfNotNull(nameof(entity))]
    public static T? Tag<T>(T? entity)
        where T : class
    {
        // A single-result operator (FirstOrDefault, an outer join) can yield null; nothing to tag.
        if (entity is not null)
        {
            // AddOrUpdate, not Add: the same instance can surface twice in one result set (e.g.
            // AsOf() on both sides of a self-join, or Concat of two history queries) and Add throws
            // on a duplicate.
            _fromHistory.AddOrUpdate(entity, null);
        }

        return entity;
    }

    /// <summary>
    /// Marks the <see cref="Version{TEntity}.Entity"/> carried by <paramref name="version"/> and returns
    /// the version unchanged. The <see cref="Version{TEntity}"/> wrapper itself is a plain projection
    /// result, never tracked; only the entity inside it can be re-attached. Effectively internal (see
    /// <see cref="Tag{T}"/>).
    /// </summary>
    [return: NotNullIfNotNull(nameof(version))]
    public static Version<T>? TagVersion<T>(Version<T>? version)
        where T : class
    {
        if (version?.Entity is { } entity)
        {
            _fromHistory.AddOrUpdate(entity, null);
        }

        return version;
    }

    /// <summary>
    /// Whether <paramref name="entity"/> is an instance handed out by a Hindsight historical query and
    /// must not be saved back.
    /// </summary>
    public static bool IsFromHistory(object entity)
        => _fromHistory.TryGetValue(entity, out _);
}
