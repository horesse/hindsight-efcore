using Hindsight;
using InsuranceSample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocsSnippets;

public static class GettingStarted
{
    public static void Register(IServiceCollection services, string connectionString)
    {
        #region register
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseHindsight(hindsight => hindsight.UseHistoryWriter(HistoryWriter.Trigger)));
        #endregion register
    }

    public static async Task WriteAsync(AppDbContext db)
    {
        #region write
        var policy = new Policy { Number = "ACME-1001", Status = PolicyStatus.Draft, Premium = 1_200m };
        db.Policies.Add(policy);
        await db.SaveChangesAsync(); // version 1 opens

        policy.Status = PolicyStatus.Active;
        await db.SaveChangesAsync(); // version 1 closes, version 2 opens
        #endregion write
    }

    public static async Task QueryAsync(AppDbContext db, int policyId, DateTimeOffset lastMonday)
    {
        #region query
        // The policy as the database held it last Monday (null if it did not exist yet).
        var then = await db.Policies
            .AsOf(lastMonday)
            .SingleOrDefaultAsync(p => p.Id == policyId);

        // Every version: when it was current, and what happened.
        var versions = await db.History<Policy>()
            .Where(v => v.Entity.Id == policyId)
            .Select(v => new { v.ValidFrom, v.ValidTo, v.Operation, v.Entity.Status })
            .ToListAsync();
        #endregion query
    }
}
