using Hindsight;
using InsuranceSample;
using Microsoft.EntityFrameworkCore;

namespace DocsSnippets;

public static class Querying
{
    public static async Task AsOfAsync(AppDbContext db, Claim claim)
    {
        #region as-of
        var policy = await db.Policies
            .AsOf(claim.OccurredAt)
            .SingleOrDefaultAsync(p => p.Id == claim.PolicyId);
        #endregion as-of
    }

    public static async Task AsOfComposedAsync(AppDbContext db, DateTimeOffset endOfLastQuarter)
    {
        #region as-of-composed
        var largeDrafts = await db.Policies
            .AsOf(endOfLastQuarter)
            .Where(p => p.Status == PolicyStatus.Draft && p.Premium > 10_000m)
            .OrderByDescending(p => p.Premium)
            .Select(p => p.Number)
            .ToListAsync();
        #endregion as-of-composed
    }

    public static async Task AllVersionsAsync(AppDbContext db, int policyId)
    {
        #region all-versions
        var timeline = await db.Policies
            .AllVersions()
            .Where(p => p.Id == policyId)
            .ToListAsync(); // newest first
        #endregion all-versions
    }

    public static async Task AllVersionsComposedAsync(AppDbContext db, int policyId)
    {
        #region all-versions-composed
        var premiumsEverCharged = await db.Policies
            .AllVersions()
            .Where(p => p.Id == policyId)
            .Select(p => p.Premium)
            .Distinct()
            .ToListAsync();
        #endregion all-versions-composed
    }

    public static async Task HistoryAsync(AppDbContext db, int policyId)
    {
        #region history
        var audit = await db.History<Policy>()
            .Where(v => v.Entity.Id == policyId)
            .Select(v => new { v.ValidFrom, v.ValidTo, v.Operation, v.ChangedBy, v.Reason, v.Entity.Status })
            .ToListAsync();
        #endregion history
    }

    public static async Task DeletionsAsync(AppDbContext db)
    {
        #region deletions
        var deletions = await db.History<Policy>()
            .Where(v => v.Operation == VersionOperation.Delete)
            .Select(v => new { v.Entity.Number, DeletedAt = v.ValidFrom, v.ChangedBy, v.Reason })
            .ToListAsync();
        #endregion deletions
    }

    public static async Task RollBackValueAsync(AppDbContext db, int policyId, DateTimeOffset before)
    {
        #region roll-back-a-value
        var snapshot = await db.Policies.AsOf(before).SingleAsync(p => p.Id == policyId);
        var current = await db.Policies.SingleAsync(p => p.Id == policyId);

        current.Premium = snapshot.Premium; // copy onto the tracked, current entity
        await db.SaveChangesAsync();        // a new version that carries the old premium
        #endregion roll-back-a-value
    }
}
