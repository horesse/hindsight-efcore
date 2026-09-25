using Hindsight.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight;

public static partial class HindsightDbContextExtensions
{
    private const int DefaultPruneBatchSize = 10_000;

    /// <summary>
    /// Deletes the history of <typeparamref name="TEntity"/> that ended at or before
    /// <paramref name="olderThan"/>: every closed version with <c>valid_to &lt;= olderThan</c> and every
    /// delete tombstone recorded by then. The current version of a live entity is never deleted. Use it
    /// from a scheduled job to bound the size of a history table; nothing else in Hindsight, and no
    /// migration, ever deletes history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before deleting anything it records <paramref name="olderThan"/> as the entity's retention horizon
    /// (it never moves an existing horizon back). From then on <c>AsOf</c>, <c>FromTo</c> and
    /// <c>ContainedIn</c> for an instant before the horizon fail with a <c>PostgresException</c> whose
    /// <c>SqlState</c> is <c>HS001</c> instead of answering from incomplete history, while every instant at
    /// or after it still returns exactly what it did before pruning. <c>AllVersions()</c> and <c>History&lt;T&gt;()</c> return the
    /// history that is left, which may start with an update rather than an insert; read the horizon with
    /// <see cref="GetHistoryHorizonAsync{TEntity}"/>. An entity deleted before the horizon is gone from
    /// history entirely.
    /// </para>
    /// <para>
    /// Rows are deleted in batches of <paramref name="batchSize"/>, each its own transaction, so a large
    /// table is not locked in one long transaction; a failure part-way leaves the horizon recorded and
    /// the rest for the next call. Called inside a transaction you opened, everything runs in that
    /// transaction instead.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">A temporal entity configured with <c>IsTemporal(t =&gt; t.WithRetention())</c>.</typeparam>
    /// <param name="context">The context whose database holds the history.</param>
    /// <param name="olderThan">
    /// The cutoff: history that ended at or before it is deleted. Must not be later than the database's
    /// current time, since the history after it is still being written.
    /// </param>
    /// <param name="batchSize">The maximum number of rows deleted per statement.</param>
    /// <param name="cancellationToken">A token to cancel the operation between batches.</param>
    /// <returns>The number of history rows deleted.</returns>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TEntity"/> is not temporal, or is not configured with <c>WithRetention()</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="olderThan"/> is later than the database's current time, or
    /// <paramref name="batchSize"/> is not positive.
    /// </exception>
    public static async Task<long> PruneHistoryAsync<TEntity>(
        this DbContext context,
        DateTimeOffset olderThan,
        int batchSize = DefaultPruneBatchSize,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (olderThan == DateTimeOffset.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(olderThan),
                olderThan,
                "The cutoff must be a finite instant; DateTimeOffset.MaxValue would select every current version.");
        }

        var retention = RetentionTarget.Resolve<TEntity>(context, nameof(PruneHistoryAsync));
        var sql = context.GetService<ISqlGenerationHelper>();
        var cutoff = olderThan.UtcDateTime;

        // The horizon goes first and commits on its own: once it is in force, the rows about to be
        // deleted are already unreachable for AsOf / FromTo / ContainedIn, so no query can observe a
        // half-pruned history as if it were complete.
        var recorded = await context.Database
            .SqlQueryRaw<DateTime>(RetentionSqlGenerator.AdvanceHorizon(retention.HorizonSchema, sql), retention.HistoryEntity, cutoff)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (recorded.Count == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(olderThan),
                olderThan,
                "The cutoff is later than the database's current time. History after now() is still being written; "
                + "prune only history that has already ended.");
        }

        var deleteBatch = RetentionSqlGenerator.DeleteBatch(
            retention.HistoryTable, retention.HistorySchema, retention.PeriodStart, retention.PeriodEnd, sql);

        long total = 0;
        int deleted;
        do
        {
            deleted = await context.Database
                .ExecuteSqlRawAsync(deleteBatch, [cutoff, batchSize], cancellationToken)
                .ConfigureAwait(false);
            total += deleted;
        }
        while (deleted == batchSize);

        return total;
    }

    /// <summary>
    /// Returns the retention horizon of <typeparamref name="TEntity"/>'s history — the instant before
    /// which <see cref="PruneHistoryAsync{TEntity}"/> removed it — or <see langword="null"/> when its
    /// history was never pruned and is complete. Use it to tell whether an <c>AllVersions()</c> or
    /// <c>History&lt;T&gt;()</c> timeline starts at the entity's creation or at the horizon, or to pick an
    /// instant that <c>AsOf</c> can still answer.
    /// </summary>
    /// <typeparam name="TEntity">A temporal entity type.</typeparam>
    /// <param name="context">The context whose database holds the history.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The horizon in UTC, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEntity"/> is not temporal.</exception>
    public static async Task<DateTimeOffset?> GetHistoryHorizonAsync<TEntity>(
        this DbContext context,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);

        var entityType = RetentionTarget.FindTemporal<TEntity>(context, nameof(GetHistoryHorizonAsync));
        if (entityType.FindAnnotation(HindsightAnnotationNames.HasRetention)?.Value is not true)
        {
            // Without WithRetention() PruneHistoryAsync refuses to run, so the history is complete.
            return null;
        }

        var retention = RetentionTarget.Resolve<TEntity>(context, nameof(GetHistoryHorizonAsync));
        var sql = context.GetService<ISqlGenerationHelper>();

        var horizons = await context.Database
            .SqlQueryRaw<DateTime>(RetentionSqlGenerator.ReadHorizon(retention.HorizonSchema, sql), retention.HistoryEntity)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return horizons.Count == 0
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(horizons[0], DateTimeKind.Utc));
    }

    // What the retention statements need from the model, resolved per call from annotations the
    // convention already computed (no model walk beyond two lookups).
    private sealed record RetentionTarget(
        string HistoryEntity,
        string HistoryTable,
        string? HistorySchema,
        string PeriodStart,
        string PeriodEnd,
        string? HorizonSchema)
    {
        public static IEntityType FindTemporal<TEntity>(DbContext context, string operation)
        {
            var entityType = context.Model.FindEntityType(typeof(TEntity));
            if (entityType?.FindAnnotation(HindsightAnnotationNames.HistoryEntityType) is null)
            {
                throw new InvalidOperationException(
                    $"{operation} requires '{typeof(TEntity).Name}' to be temporal, but it is not. Configure it with "
                    + "IsTemporal(t => t.WithRetention()) in OnModelCreating.");
            }

            return entityType;
        }

        public static RetentionTarget Resolve<TEntity>(DbContext context, string operation)
        {
            var entityType = FindTemporal<TEntity>(context, operation);
            if (entityType.FindAnnotation(HindsightAnnotationNames.HasRetention)?.Value is not true)
            {
                throw new InvalidOperationException(
                    $"{operation} requires '{entityType.DisplayName()}' to be configured with IsTemporal(t => t.WithRetention()). "
                    + "Without it a historical query before the pruned horizon could not detect the missing history "
                    + "and would answer from what is left. Add WithRetention() and a migration, then prune.");
            }

            var historyEntity = (string)entityType.FindAnnotation(HindsightAnnotationNames.HistoryEntityType)!.Value!;
            var history = context.Model.FindEntityType(historyEntity)
                ?? throw new InvalidOperationException(
                    $"{operation}: the history entity type '{historyEntity}' is missing from the model. This is a bug in Hindsight.");
            var horizon = context.Model.GetEntityTypes()
                .FirstOrDefault(e => e.FindAnnotation(HindsightAnnotationNames.IsRetentionHorizonTable)?.Value is true)
                ?? throw new InvalidOperationException(
                    $"{operation}: the retention-horizon table is missing from the model. This is a bug in Hindsight.");

            return new RetentionTarget(
                historyEntity,
                history.GetTableName()!,
                history.GetSchema(),
                entityType.FindAnnotation(HindsightAnnotationNames.PeriodStartColumnName)?.Value as string
                    ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName,
                entityType.FindAnnotation(HindsightAnnotationNames.PeriodEndColumnName)?.Value as string
                    ?? TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName,
                horizon.GetSchema());
        }
    }
}
