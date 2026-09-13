using Hindsight;
using InsuranceSample;
using Microsoft.EntityFrameworkCore;

namespace DocsSnippets;

public static class Overview
{
    public static async Task ShowAsync(ModelBuilder modelBuilder, AppDbContext db, Claim claim)
    {
        #region overview
        // Make an entity temporal; the next migration adds its history table.
        modelBuilder.Entity<Policy>().IsTemporal();

        // What did this policy look like when the claim happened?
        var policy = await db.Policies
            .AsOf(claim.OccurredAt)
            .SingleAsync(p => p.Id == claim.PolicyId);

        // Who changed it, when, and why?
        var audit = await db.History<Policy>()
            .Where(v => v.Entity.Id == claim.PolicyId)
            .Select(v => new { v.ValidFrom, v.ChangedBy, v.Reason, v.Entity.Status, v.Entity.Premium })
            .ToListAsync();
        #endregion overview
    }
}
