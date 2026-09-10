using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.Benchmarks;

/// <summary>
/// Cost of inserting new temporal entities with no history writer, the interceptor writer and the
/// trigger writer. Each measured call is one <c>SaveChanges</c> that adds <see cref="EntityCount"/>
/// policies; <c>[IterationSetup]</c> truncates the tables first and is not measured.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class SaveChangesInsertBenchmarks
{
    private string _databaseName = "";
    private BenchmarkContext _context = null!;

    /// <summary>The history writer under test.</summary>
    [Params(HistoryMode.None, HistoryMode.Interceptor, HistoryMode.Trigger)]
    public HistoryMode Mode { get; set; }

    /// <summary>How many policies a single <c>SaveChanges</c> inserts.</summary>
    [Params(1, 100, 1000)]
    public int EntityCount { get; set; }

    /// <summary>Creates the throwaway database and the schema once for this benchmark case.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _databaseName = $"bench_insert_{Mode}_{EntityCount}".ToLowerInvariant();
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

    /// <summary>Empties the tables and the change tracker before each measured insert.</summary>
    [IterationSetup]
    public void ResetTables()
    {
        _context.ChangeTracker.Clear();
        _context.Database.ExecuteSqlRaw(BenchmarkDatabase.TruncateSql(Mode));
    }

    /// <summary>Adds <see cref="EntityCount"/> policies and saves once.</summary>
    [Benchmark]
    public void Insert()
    {
        for (var i = 0; i < EntityCount; i++)
        {
            _context.Policies.Add(new Policy
            {
                Number = $"ACME-{i}",
                Status = PolicyStatus.Draft,
                Premium = 100m + i,
            });
        }

        _context.SaveChanges();
    }
}
