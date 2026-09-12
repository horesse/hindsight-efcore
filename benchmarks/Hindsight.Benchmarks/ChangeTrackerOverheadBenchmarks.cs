using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.Benchmarks;

/// <summary>
/// Isolates the cost a repeated <c>ChangeTracker.Entries()</c> call adds to <c>SaveChanges</c>: with
/// <c>AutoDetectChangesEnabled</c> on (the default), every call re-runs <c>DetectChanges()</c> over the
/// whole tracked graph, and both writer modes call <c>Entries()</c> more than once per
/// <c>SaveChanges</c> — <c>HistorySnapshotGuardInterceptor.Guard</c> plus either
/// <c>HistoryRowPlan.BuildPending</c> (Interceptor) or
/// <c>HistoryTriggerContextInterceptor.HasTemporalChange</c> (Trigger). <see cref="SaveChangesMutationBenchmarks"/>
/// measures the cost of writing history for N *changed* rows; this measures the cost of N *tracked but
/// untouched* rows sitting in the same context while only one unrelated row actually changes — the
/// scenario those repeated <c>Entries()</c> walks are the concern for, isolated from the cost of
/// writing history itself.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class ChangeTrackerOverheadBenchmarks
{
    private string _databaseName = "";
    private BenchmarkContext _context = null!;
    private Policy _changed = null!;

    /// <summary>The history writer under test (<see cref="HistoryMode.None"/> is plain EF Core, the baseline).</summary>
    [Params(HistoryMode.None, HistoryMode.Interceptor, HistoryMode.Trigger)]
    public HistoryMode Mode { get; set; }

    /// <summary>
    /// How many additional policies are tracked as <c>Unchanged</c> alongside the one policy the
    /// measured <c>SaveChanges</c> actually modifies.
    /// </summary>
    [Params(0, 100, 1_000, 10_000)]
    public int TrackedUnchangedCount { get; set; }

    /// <summary>Creates the throwaway database and the schema once for this benchmark case.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _databaseName = $"bench_tracker_{Mode}_{TrackedUnchangedCount}".ToLowerInvariant();
        var connectionString = BenchmarkDatabase.CreateDatabase(_databaseName);
        _context = BenchmarkDatabase.NewContext(connectionString, Mode);
        _context.Database.EnsureCreated();
    }

    /// <summary>Disposes the context and drops the throwaway database.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _context.Dispose();
        BenchmarkDatabase.DropDatabase(_databaseName);
    }

    /// <summary>
    /// Truncates, then inserts <see cref="TrackedUnchangedCount"/> policies plus one more — all become
    /// <c>Unchanged</c> once this seeding <c>SaveChanges</c> completes, exactly like entities loaded
    /// earlier in a long-lived context or unit of work.
    /// </summary>
    [IterationSetup]
    public void ReseedRows()
    {
        _context.ChangeTracker.Clear();
        _context.Database.ExecuteSqlRaw(BenchmarkDatabase.TruncateSql(Mode));

        var policies = new List<Policy>(TrackedUnchangedCount + 1);
        for (var i = 0; i < TrackedUnchangedCount; i++)
        {
            policies.Add(new Policy
            {
                Number = $"ACME-{i}",
                Status = PolicyStatus.Draft,
                Premium = 100m + i,
            });
        }

        var changed = new Policy { Number = "ACME-CHANGED", Status = PolicyStatus.Draft, Premium = 1m };
        policies.Add(changed);

        _context.Policies.AddRange(policies);
        _context.SaveChanges();
        _changed = changed;
    }

    /// <summary>
    /// Modifies the one tracked policy that actually changes — the other
    /// <see cref="TrackedUnchangedCount"/> stay <c>Unchanged</c> — and saves once.
    /// </summary>
    [Benchmark]
    public void Save()
    {
        _changed.Premium += 1m;
        _context.SaveChanges();
    }
}
