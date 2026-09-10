using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.Benchmarks;

/// <summary>
/// Cost of updating and deleting existing temporal entities across the three writer modes. Each
/// measured call is one <c>SaveChanges</c> over <see cref="EntityCount"/> already-persisted policies;
/// <c>[IterationSetup]</c> truncates and re-seeds those rows and is not measured. Row counts stop at
/// 100 — the writer's per-row work is linear, so 1000 adds run time and re-seed cost without new
/// information.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class SaveChangesMutationBenchmarks
{
    private string _databaseName = "";
    private BenchmarkContext _context = null!;
    private List<Policy> _seeded = [];

    /// <summary>The history writer under test.</summary>
    [Params(HistoryMode.None, HistoryMode.Interceptor, HistoryMode.Trigger)]
    public HistoryMode Mode { get; set; }

    /// <summary>How many policies a single <c>SaveChanges</c> updates or deletes.</summary>
    [Params(1, 100)]
    public int EntityCount { get; set; }

    /// <summary>Creates the throwaway database and the schema once for this benchmark case.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _databaseName = $"bench_mutation_{Mode}_{EntityCount}".ToLowerInvariant();
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

    /// <summary>Truncates, then inserts a fresh set of tracked policies for the measured call to mutate.</summary>
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

    /// <summary>Removes every seeded policy and saves once.</summary>
    [Benchmark]
    public void Delete()
    {
        _context.Policies.RemoveRange(_seeded);
        _context.SaveChanges();
    }
}
