using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Hindsight;

/// <summary>
/// LINQ entry points for reading Hindsight history.
/// </summary>
public static class HindsightQueryableExtensions
{
    internal static readonly MethodInfo AsOfMethod = typeof(HindsightQueryableExtensions)
        .GetMethod(nameof(MarkAsOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static readonly MethodInfo AllVersionsMethod = typeof(HindsightQueryableExtensions)
        .GetMethod(nameof(MarkAllVersions), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Returns each <typeparamref name="TEntity"/> as it stood at <paramref name="asOf"/>, read from
    /// the entity's Hindsight history table instead of the current table. Use it to answer "what did
    /// this row look like at that moment" — for an audit screen, a point-in-time report, or to
    /// reconstruct the input to a past decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The version returned for an instant <c>t</c> is the one whose half-open system-time period
    /// contains it: <c>valid_from &lt;= t &lt; valid_to</c>. An entity that had not been created yet at
    /// <paramref name="asOf"/>, or that had already been deleted, contributes no row.
    /// </para>
    /// <para>
    /// <c>AsOf</c> must be the first operator on the query, applied directly to a <see cref="DbSet{TEntity}"/>.
    /// Everything after it — <c>Where</c>, <c>OrderBy</c>, <c>Select</c>, <c>First</c>, <c>Any</c>,
    /// <c>Count</c> — composes as usual and translates to a single SQL query against the history table.
    /// Results are always no-tracking (DESIGN.md D7). Combining <c>AsOf</c> with <see cref="EntityFrameworkQueryableExtensions.Include{TEntity, TProperty}"/>
    /// throws <see cref="NotSupportedException"/> in v1 (DESIGN.md D8).
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The temporal entity type. It must be configured with
    /// <see cref="TemporalEntityTypeBuilderExtensions.IsTemporal{TEntity}"/>.</typeparam>
    /// <param name="source">The queryable, which must be a <see cref="DbSet{TEntity}"/> of a Hindsight context.</param>
    /// <param name="asOf">The instant to read the state at. Converted to UTC.</param>
    /// <returns>A queryable over the historical state at <paramref name="asOf"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Hindsight is not enabled on the context, <typeparamref name="TEntity"/> is not temporal, or
    /// <c>AsOf</c> is not the first operator on the query.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The query also uses <c>Include</c> / <c>ThenInclude</c> (DESIGN.md D8), or
    /// <typeparamref name="TEntity"/> takes part in an inheritance hierarchy (DESIGN.md D9).
    /// </exception>
    public static IQueryable<TEntity> AsOf<TEntity>(this IQueryable<TEntity> source, DateTimeOffset asOf)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);

        // Carry the instant as member access on a captured object, not Expression.Constant(asOf): EF
        // then treats it as a query parameter, so the compiled-query cache is shared across instants
        // and the value lands in SQL as a parameter (.claude/rules/sql-and-migrations.md).
        var parameter = new AsOfParameter(asOf.UtcDateTime);

        return source.Provider.CreateQuery<TEntity>(
            Expression.Call(
                AsOfMethod.MakeGenericMethod(typeof(TEntity)),
                source.Expression,
                Expression.Property(Expression.Constant(parameter), nameof(AsOfParameter.AsOfUtc))));
    }

    /// <summary>
    /// Returns every stored version of each <typeparamref name="TEntity"/> — the whole system-time
    /// timeline — read from the entity's Hindsight history table instead of the current table. Use it
    /// to render an entity's change history, diff two versions, or feed a timeline view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One row per <c>insert</c> and <c>update</c>. The <c>delete</c> tombstone (DESIGN.md D5) is not
    /// a state version and is excluded; a deleted entity's timeline ends at the version that was open
    /// when it was deleted. To see <i>when</i> and <i>by whom</i> a delete happened, use
    /// <c>History&lt;TEntity&gt;()</c>.
    /// </para>
    /// <para>
    /// <c>AllVersions</c> must be the first operator on the query, applied directly to a
    /// <see cref="DbSet{TEntity}"/>. Everything after it — <c>Where</c>, <c>OrderBy</c>, <c>Select</c>,
    /// <c>First</c>, <c>Any</c>, <c>Count</c> — composes as usual and translates to a single SQL query
    /// against the history table. Rows come back newest first (by <c>valid_from</c> descending); adding
    /// your own <c>OrderBy</c> / <c>OrderByDescending</c> replaces that ordering. Results are always
    /// no-tracking (DESIGN.md D7). Combining <c>AllVersions</c> with
    /// <see cref="EntityFrameworkQueryableExtensions.Include{TEntity, TProperty}"/> throws
    /// <see cref="NotSupportedException"/> in v1 (DESIGN.md D8).
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The temporal entity type. It must be configured with
    /// <see cref="TemporalEntityTypeBuilderExtensions.IsTemporal{TEntity}"/>.</typeparam>
    /// <param name="source">The queryable, which must be a <see cref="DbSet{TEntity}"/> of a Hindsight context.</param>
    /// <returns>A queryable over every stored version, newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Hindsight is not enabled on the context, <typeparamref name="TEntity"/> is not temporal, or
    /// <c>AllVersions</c> is not the first operator on the query.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The query also uses <c>Include</c> / <c>ThenInclude</c> (DESIGN.md D8), or
    /// <typeparamref name="TEntity"/> takes part in an inheritance hierarchy (DESIGN.md D9).
    /// </exception>
    public static IQueryable<TEntity> AllVersions<TEntity>(this IQueryable<TEntity> source)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Provider.CreateQuery<TEntity>(
            Expression.Call(
                AllVersionsMethod.MakeGenericMethod(typeof(TEntity)),
                source.Expression));
    }

    // Marker only: HindsightQueryExpressionInterceptor rewrites the AsOf(...) call before compilation.
    // Reaching this body means the interceptor is not in the pipeline.
    private static IQueryable<TEntity> MarkAsOf<TEntity>(IQueryable<TEntity> source, DateTime asOfUtc)
        where TEntity : class
        => throw new InvalidOperationException(
            "AsOf() was not translated by Hindsight. Enable Hindsight on the DbContext with "
            + "options.UseHindsight(), and call AsOf() on a DbSet of an entity configured with IsTemporal().");

    // Marker only: HindsightQueryExpressionInterceptor rewrites the AllVersions() call before compilation.
    // Reaching this body means the interceptor is not in the pipeline.
    private static IQueryable<TEntity> MarkAllVersions<TEntity>(IQueryable<TEntity> source)
        where TEntity : class
        => throw new InvalidOperationException(
            "AllVersions() was not translated by Hindsight. Enable Hindsight on the DbContext with "
            + "options.UseHindsight(), and call AllVersions() on a DbSet of an entity configured with IsTemporal().");

    private sealed class AsOfParameter(DateTime asOfUtc)
    {
        public DateTime AsOfUtc { get; } = asOfUtc;
    }
}
