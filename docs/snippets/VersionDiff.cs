using Hindsight;
using InsuranceSample;
using Microsoft.EntityFrameworkCore;

namespace DocsSnippets;

public static class VersionDiff
{
    public static async Task DiffConsecutiveVersionsAsync(AppDbContext db, int policyId)
    {
        #region diff-versions
        var versions = await db.History<Policy>()
            .Where(v => v.Entity.Id == policyId && v.Operation != VersionOperation.Delete)
            .OrderBy(v => v.ValidFrom) // oldest first: Diff takes the older version first
            .ToListAsync();

        Version<Policy>? previous = null;
        foreach (var version in versions)
        {
            foreach (var change in db.Diff(previous, version))
            {
                Console.WriteLine(
                    $"{version.ValidFrom:u} {version.ChangedBy}: {change.Path} {change.OldValue} → {change.NewValue}");
            }

            previous = version;
        }
        #endregion diff-versions
    }

    public static async Task DiffAsOfAsync(AppDbContext db, int policyId, DateTimeOffset renewalDate)
    {
        #region diff-since
        var atRenewal = await db.Policies.AsOf(renewalDate).SingleAsync(p => p.Id == policyId);
        var now = await db.Policies.SingleAsync(p => p.Id == policyId);

        var changedSinceRenewal = db.Diff(atRenewal, now); // older snapshot first
        #endregion diff-since
    }
}
