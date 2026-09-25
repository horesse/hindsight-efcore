using Hindsight;
using InsuranceSample;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocsSnippets;

/// <summary>A context whose policy history may be pruned.</summary>
public sealed class RetentionDbContext(DbContextOptions<RetentionDbContext> options) : DbContext(options)
{
    public DbSet<Policy> Policies => Set<Policy>();

    #region with-retention
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Policy>().IsTemporal(t => t.WithRetention());
    }
    #endregion with-retention
}

public static class Retention
{
    public static async Task PruneAsync(RetentionDbContext db, CancellationToken cancellationToken)
    {
        #region prune-history
        // In a scheduled job: keep seven years of policy history.
        var cutoff = DateTimeOffset.UtcNow.AddYears(-7);
        long deleted = await db.PruneHistoryAsync<Policy>(cutoff, cancellationToken: cancellationToken);
        #endregion prune-history
    }

    public static async Task<Policy?> ReadHorizonAsync(RetentionDbContext db, int policyId, DateTimeOffset at)
    {
        #region history-horizon
        DateTimeOffset? horizon = await db.GetHistoryHorizonAsync<Policy>();
        if (horizon is not null && at < horizon)
        {
            return null; // pruned: AsOf(at) would throw
        }

        return await db.Policies.AsOf(at).SingleOrDefaultAsync(p => p.Id == policyId);
        #endregion history-horizon
    }

    public static async Task<Policy?> CatchHorizonErrorAsync(RetentionDbContext db, int policyId, DateTimeOffset at)
    {
        #region horizon-error
        try
        {
            return await db.Policies.AsOf(at).SingleOrDefaultAsync(p => p.Id == policyId);
        }
        catch (PostgresException ex) when (ex.SqlState == "HS001")
        {
            // `at` is before the retention horizon: the history that would answer was pruned.
            throw new InvalidOperationException($"Policy history before {at:u} is no longer kept.", ex);
        }
        #endregion horizon-error
    }

    public static async Task DetachPartitionAsync(RetentionDbContext db, CancellationToken cancellationToken)
    {
        #region detach-partition
        // Drop the partition of versions that ended in January 2019, and record the horizon, atomically.
        var partitionEnd = new DateTimeOffset(2019, 2, 1, 0, 0, 0, TimeSpan.Zero);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE policies_history DETACH PARTITION policies_history_2019_01", cancellationToken);
            await db.PruneHistoryAsync<Policy>(partitionEnd, cancellationToken: cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync("DROP TABLE policies_history_2019_01", cancellationToken);
        #endregion detach-partition
    }
}
