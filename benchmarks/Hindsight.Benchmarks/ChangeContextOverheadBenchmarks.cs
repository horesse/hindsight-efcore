using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.Benchmarks;

/// <summary>
/// The price of attaching a change context (<c>who / why / correlation id</c>) to each
/// <c>SaveChanges</c>: for the interceptor writer an extra provider call and wider history
/// <c>INSERT</c>s, for the trigger writer one <c>set_config</c> round-trip per <c>SaveChanges</c>.
/// Fixed 100-row update; the provider returns a constant context with no I/O, so only the mechanism
/// is measured. <c>WithChangeContext = false</c> is the same case as the 100-row update in
/// <see cref="SaveChangesMutationBenchmarks"/> and is repeated here as the local baseline.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class ChangeContextOverheadBenchmarks
{
    private string _databaseName = "";
    private BenchmarkContext _context = null!;
    private List<Policy> _seeded = [];

    /// <summary>The history writer under test (the baseline <c>None</c> mode has no change context).</summary>
    [Params(HistoryMode.Interceptor, HistoryMode.Trigger)]
    public HistoryMode Mode { get; set; }

    /// <summary>Whether an <see cref="IChangeContextProvider"/> is registered.</summary>
    [Params(false, true)]
    public bool WithChangeContext { get; set; }

    /// <summary>Rows the measured update touches; fixed, so the two axes above stay the story.</summary>
    [Params(100)]
    public int EntityCount { get; set; }

    /// <summary>Creates the throwaway database and the schema once for this benchmark case.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _databaseName = $"bench_context_{Mode}_{WithChangeContext}".ToLowerInvariant();
        var connectionString = BenchmarkDatabase.CreateDatabase(_databaseName);
        _context = BenchmarkDatabase.NewContext(connectionString, Mode, WithChangeContext);
        _context.Database.EnsureCreated();
    }

    /// <summary>Disposes the context and drops the throwaway database.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _context.Dispose();
        BenchmarkDatabase.DropDatabase(_databaseName);
    }

    /// <summary>Truncates, then inserts a fresh set of tracked policies for the measured update.</summary>
    [IterationSetup]
    public void ReseedRows()
    {
        _context.ChangeTracker.Clear();
        _context.Database.ExecuteSqlRaw(BenchmarkDatabase.TruncateSql(Mode));

        var policies = new List<Policy>(EntityCount);
        for (var i = 0; i < EntityCount; i++)
        {
            policies.Add(new Policy
            {
                Number = $"ACME-{i}",
                Status = PolicyStatus.Draft,
                Premium = 100m + i,
            });
        }

        _context.Policies.AddRange(policies);
        _context.SaveChanges();
        _seeded = policies;
    }

    /// <summary>Bumps a versioned column on every seeded policy and saves once.</summary>
    [Benchmark]
    public void Update()
    {
        foreach (var policy in _seeded)
        {
            policy.Premium += 1m;
        }

        _context.SaveChanges();
    }
}
