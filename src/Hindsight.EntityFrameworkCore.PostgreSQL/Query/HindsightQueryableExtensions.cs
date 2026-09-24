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

    internal static readonly MethodInfo FromToMethod = typeof(HindsightQueryableExtensions)
        .GetMethod(nameof(MarkFromTo), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static readonly MethodInfo ContainedInMethod = typeof(HindsightQueryableExtensions)
        .GetMethod(nameof(MarkContainedIn), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static readonly MethodInfo HistoryMethod = typeof(HindsightQueryableExtensions)
        .GetMethod(nameof(MarkHistory), BindingFlags.NonPublic | BindingFlags.Static)!;

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
    /// throws <see cref="NotSupportedException"/> (DESIGN.md D8).
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
        // and the value lands in SQL as a parameter, not a literal.
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
    /// <see cref="NotSupportedException"/> (DESIGN.md D8).
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

    /// <summary>
    /// Returns every stored version of each <typeparamref name="TEntity"/> that was valid at any
    /// instant in the window <c>[<paramref name="from"/>, <paramref name="to"/>)</c>, read from the
    /// entity's Hindsight history table. Use it for audit questions over a period — "every state this
    /// policy was in during Q3", "which policies were active at any point last month".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both the version's period and the window are half-open, and a version is returned when they
    /// overlap: <c>valid_from &lt; to</c> and <c>valid_to &gt; from</c>. A version that ended exactly
    /// at <paramref name="from"/>, or started exactly at <paramref name="to"/>, was not valid at any
    /// instant of the window and is not returned; the current version is returned when it started
    /// before <paramref name="to"/>. An empty window (<paramref name="from"/> equal to
    /// <paramref name="to"/>) contains no instant and returns nothing — to read a single instant, use
    /// <see cref="AsOf{TEntity}"/>. The semantics match SQL Server's
    /// <c>FOR SYSTEM_TIME FROM … TO …</c>, except for that empty window.
    /// </para>
    /// <para>
    /// Like <see cref="AllVersions{TEntity}"/>, the <c>delete</c> tombstone (DESIGN.md D5) is not a
    /// state version and is excluded, rows come back newest first (by <c>valid_from</c> descending; your
    /// own <c>OrderBy</c> replaces that), and results are always no-tracking (DESIGN.md D7). The
    /// predicate is served by the history table's period-range GiST index (DESIGN.md D14).
    /// </para>
    /// <para>
    /// <c>FromTo</c> must be the first operator on the query, applied directly to a
    /// <see cref="DbSet{TEntity}"/>. Everything after it composes into a single SQL query against the
    /// history table. Combining it with <see cref="EntityFrameworkQueryableExtensions.Include{TEntity, TProperty}"/>
    /// throws <see cref="NotSupportedException"/> (DESIGN.md D8).
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The temporal entity type. It must be configured with
    /// <see cref="TemporalEntityTypeBuilderExtensions.IsTemporal{TEntity}"/>.</typeparam>
    /// <param name="source">The queryable, which must be a <see cref="DbSet{TEntity}"/> of a Hindsight context.</param>
    /// <param name="from">The start of the window, inclusive. Converted to UTC.</param>
    /// <param name="to">The end of the window, exclusive. Converted to UTC. <see cref="DateTimeOffset.MaxValue"/>
    /// is PostgreSQL <c>'infinity'</c>: no upper bound.</param>
    /// <returns>A queryable over every version valid at some instant of the window, newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="from"/> is later than <paramref name="to"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Hindsight is not enabled on the context, <typeparamref name="TEntity"/> is not temporal, or
    /// <c>FromTo</c> is not the first operator on the query.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The query also uses <c>Include</c> / <c>ThenInclude</c> (DESIGN.md D8), or
    /// <typeparamref name="TEntity"/> takes part in an inheritance hierarchy (DESIGN.md D9).
    /// </exception>
    public static IQueryable<TEntity> FromTo<TEntity>(this IQueryable<TEntity> source, DateTimeOffset from, DateTimeOffset to)
        where TEntity : class
        => CreateRangeQuery(source, FromToMethod, from, to);

    /// <summary>
    /// Returns every stored version of each <typeparamref name="TEntity"/> whose whole period lies
    /// inside the window <c>[<paramref name="from"/>, <paramref name="to"/>)</c> — versions that both
    /// started and ended within it — read from the entity's Hindsight history table. Use it for "what
    /// was changed, and changed again, during this window": short-lived states, corrections made and
    /// superseded inside a reporting period.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A version <c>[valid_from, valid_to)</c> is returned when <c>valid_from &gt;= from</c> and
    /// <c>valid_to &lt;= to</c>. A version that started exactly at <paramref name="from"/> or ended
    /// exactly at <paramref name="to"/> is inside; one that started before <paramref name="from"/> or
    /// is still current is not (unless <paramref name="to"/> is <see cref="DateTimeOffset.MaxValue"/>,
    /// which is PostgreSQL <c>'infinity'</c>). An empty window (<paramref name="from"/> equal to
    /// <paramref name="to"/>) returns nothing. The semantics match SQL Server's
    /// <c>FOR SYSTEM_TIME CONTAINED IN (…)</c>.
    /// </para>
    /// <para>
    /// Like <see cref="AllVersions{TEntity}"/>, the <c>delete</c> tombstone (DESIGN.md D5) is excluded,
    /// rows come back newest first (by <c>valid_from</c> descending; your own <c>OrderBy</c> replaces
    /// that), and results are always no-tracking (DESIGN.md D7). The predicate is served by the history
    /// table's period-range GiST index (DESIGN.md D14).
    /// </para>
    /// <para>
    /// <c>ContainedIn</c> must be the first operator on the query, applied directly to a
    /// <see cref="DbSet{TEntity}"/>. Everything after it composes into a single SQL query against the
    /// history table. Combining it with <see cref="EntityFrameworkQueryableExtensions.Include{TEntity, TProperty}"/>
    /// throws <see cref="NotSupportedException"/> (DESIGN.md D8).
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The temporal entity type. It must be configured with
    /// <see cref="TemporalEntityTypeBuilderExtensions.IsTemporal{TEntity}"/>.</typeparam>
    /// <param name="source">The queryable, which must be a <see cref="DbSet{TEntity}"/> of a Hindsight context.</param>
    /// <param name="from">The start of the window, inclusive. Converted to UTC.</param>
    /// <param name="to">The end of the window: a version ending exactly here is inside it. Converted to
    /// UTC. <see cref="DateTimeOffset.MaxValue"/> is PostgreSQL <c>'infinity'</c>: no upper bound.</param>
    /// <returns>A queryable over every version whose period lies within the window, newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="from"/> is later than <paramref name="to"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Hindsight is not enabled on the context, <typeparamref name="TEntity"/> is not temporal, or
    /// <c>ContainedIn</c> is not the first operator on the query.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The query also uses <c>Include</c> / <c>ThenInclude</c> (DESIGN.md D8), or
    /// <typeparamref name="TEntity"/> takes part in an inheritance hierarchy (DESIGN.md D9).
    /// </exception>
    public static IQueryable<TEntity> ContainedIn<TEntity>(this IQueryable<TEntity> source, DateTimeOffset from, DateTimeOffset to)
        where TEntity : class
        => CreateRangeQuery(source, ContainedInMethod, from, to);

    /// <summary>
    /// Returns every stored version of each <typeparamref name="TEntity"/> as a
    /// <see cref="Version{TEntity}"/> — the entity snapshot for that version plus the version's
    /// system-time period (<see cref="Version{TEntity}.ValidFrom"/> / <see cref="Version{TEntity}.ValidTo"/>)
    /// and change-context metadata (<see cref="Version{TEntity}.Operation"/>,
    /// <see cref="Version{TEntity}.ChangedBy"/>, <see cref="Version{TEntity}.Reason"/>, …). Use it for
    /// an audit trail, a "who changed this and when" screen, or a delete log — anything that needs the
    /// version metadata and not just the column values that <see cref="AllVersions{TEntity}"/> returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One row per stored history row, <b>including the <c>delete</c> tombstone</b> (DESIGN.md D5),
    /// which carries the <i>when</i> / <i>who</i> of a deletion as
    /// <see cref="Version{TEntity}.Operation"/> <see cref="VersionOperation.Delete"/> with an empty
    /// interval. Rows come back newest first (by <c>valid_from</c> descending, then insertion order);
    /// adding your own <c>OrderBy</c> / <c>OrderByDescending</c> replaces that ordering.
    /// </para>
    /// <para>
    /// The query is composed against the history table: <c>Where</c>, <c>OrderBy</c> and <c>Select</c>
    /// over both the metadata members and <see cref="Version{TEntity}.Entity"/>'s properties
    /// (<c>Where(v =&gt; v.Entity.Id == id)</c>) translate to a single SQL query. Results are always
    /// no-tracking (DESIGN.md D7). Combining <c>History</c> with
    /// <see cref="EntityFrameworkQueryableExtensions.Include{TEntity, TProperty}"/> or
    /// <c>AsTracking</c> throws (DESIGN.md D8, D7).
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The temporal entity type. It must be configured with
    /// <see cref="TemporalEntityTypeBuilderExtensions.IsTemporal{TEntity}"/>.</typeparam>
    /// <param name="context">A Hindsight-enabled <see cref="DbContext"/>.</param>
    /// <returns>A queryable over every stored version, newest first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Hindsight is not enabled on the context, or <typeparamref name="TEntity"/> is not temporal.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The query also uses <c>Include</c> / <c>ThenInclude</c> (DESIGN.md D8), or
    /// <typeparamref name="TEntity"/> takes part in an inheritance hierarchy (DESIGN.md D9) or has
    /// owned / complex members.
    /// </exception>
    public static IQueryable<Version<TEntity>> History<TEntity>(this DbContext context)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        IQueryable<TEntity> source = context.Set<TEntity>();

        return source.Provider.CreateQuery<Version<TEntity>>(
            Expression.Call(
                HistoryMethod.MakeGenericMethod(typeof(TEntity)),
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

    // Marker only: HindsightQueryExpressionInterceptor rewrites the FromTo(...) call before compilation.
    // Reaching this body means the interceptor is not in the pipeline.
    private static IQueryable<TEntity> MarkFromTo<TEntity>(IQueryable<TEntity> source, DateTime fromUtc, DateTime toUtc)
        where TEntity : class
        => throw new InvalidOperationException(
            "FromTo() was not translated by Hindsight. Enable Hindsight on the DbContext with "
            + "options.UseHindsight(), and call FromTo() on a DbSet of an entity configured with IsTemporal().");

    // Marker only: HindsightQueryExpressionInterceptor rewrites the ContainedIn(...) call before compilation.
    // Reaching this body means the interceptor is not in the pipeline.
    private static IQueryable<TEntity> MarkContainedIn<TEntity>(IQueryable<TEntity> source, DateTime fromUtc, DateTime toUtc)
        where TEntity : class
        => throw new InvalidOperationException(
            "ContainedIn() was not translated by Hindsight. Enable Hindsight on the DbContext with "
            + "options.UseHindsight(), and call ContainedIn() on a DbSet of an entity configured with IsTemporal().");

    // Marker only: HindsightQueryExpressionInterceptor rewrites the History<T>() call before compilation.
    // Reaching this body means the interceptor is not in the pipeline.
    private static IQueryable<Version<TEntity>> MarkHistory<TEntity>(IQueryable<TEntity> source)
        where TEntity : class
        => throw new InvalidOperationException(
            "History<T>() was not translated by Hindsight. Enable Hindsight on the DbContext with "
            + "options.UseHindsight(), and call History<T>() for an entity configured with IsTemporal().");

    private static IQueryable<TEntity> CreateRangeQuery<TEntity>(
        IQueryable<TEntity> source, MethodInfo marker, DateTimeOffset from, DateTimeOffset to)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);

        // PostgreSQL rejects tstzrange(lower, upper) with lower > upper only at execution time, with a
        // message that names neither the operator nor the argument; fail at the call instead.
        if (from > to)
        {
            throw new ArgumentOutOfRangeException(
                nameof(from),
                from,
                $"The window start must not be later than its end ({to:O}). An empty window (from == to) is "
                + "allowed and returns no versions.");
        }

        // Both bounds as member access on one captured object, not Expression.Constant: EF turns each
        // into a query parameter, so the compiled-query cache is shared across windows (as for AsOf).
        var captured = Expression.Constant(new RangeParameter(from.UtcDateTime, to.UtcDateTime));

        return source.Provider.CreateQuery<TEntity>(
            Expression.Call(
                marker.MakeGenericMethod(typeof(TEntity)),
                source.Expression,
                Expression.Property(captured, nameof(RangeParameter.FromUtc)),
                Expression.Property(captured, nameof(RangeParameter.ToUtc))));
    }

    private sealed class AsOfParameter(DateTime asOfUtc)
    {
        public DateTime AsOfUtc { get; } = asOfUtc;
    }

    private sealed class RangeParameter(DateTime fromUtc, DateTime toUtc)
    {
        public DateTime FromUtc { get; } = fromUtc;

        public DateTime ToUtc { get; } = toUtc;
    }
}
