using System.Transactions;
using Hindsight;
using InsuranceSample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocsSnippets;

public static class Transactions
{
    public static async Task OwnTransactionAsync(AppDbContext db, Policy policy)
    {
        #region own-transaction
        await using var transaction = await db.Database.BeginTransactionAsync();

        policy.Status = PolicyStatus.Active;
        await db.SaveChangesAsync(); // history is written inside your transaction

        // ...more work in the same transaction...

        await transaction.CommitAsync(); // the data change and its history commit together
        #endregion own-transaction
    }

    public static void RetryOptions(IServiceCollection services, string connectionString)
    {
        #region retry-options
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
            .UseHindsight(hindsight => hindsight.UseHistoryWriter(HistoryWriter.Trigger)));
        #endregion retry-options
    }

    public static async Task RetryAsync(AppDbContext db, Policy policy)
    {
        #region retry-execution-strategy
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();

            policy.Status = PolicyStatus.Suspended;
            await db.SaveChangesAsync();

            await transaction.CommitAsync();
        });
        #endregion retry-execution-strategy
    }

    public static async Task AmbientAsync(AppDbContext db)
    {
        #region transaction-scope
        using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

        db.Policies.Add(new Policy { Number = "ACME-1002", Premium = 950m });
        await db.SaveChangesAsync();

        scope.Complete(); // without Complete(), the data change and its history roll back together
        #endregion transaction-scope
    }
}
