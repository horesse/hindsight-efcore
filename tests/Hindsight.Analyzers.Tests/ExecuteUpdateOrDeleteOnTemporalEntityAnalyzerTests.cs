using VerifyCS = Hindsight.Analyzers.Tests.CSharpAnalyzerVerifier<
    Hindsight.Analyzers.ExecuteUpdateOrDeleteOnTemporalEntityAnalyzer>;

namespace Hindsight.Analyzers.Tests;

/// <summary>
/// HDST001 (DESIGN.md D4). Every test compiles real Hindsight + EF Core API calls - see
/// <see cref="CSharpAnalyzerVerifier{TAnalyzer}"/> for the reference assemblies involved.
/// </summary>
public class ExecuteUpdateOrDeleteOnTemporalEntityAnalyzerTests
{
    private const string Preamble =
        """
        using System.Linq;
        using System.Threading.Tasks;
        using Hindsight;
        using Microsoft.EntityFrameworkCore;

        namespace TestNamespace
        {
            public class Policy
            {
                public int Id { get; set; }
                public string Status { get; set; } = string.Empty;
            }
        """;

    [Fact]
    public async Task ExecuteUpdateAsync_on_temporal_DbSet_property_fires()
    {
        var source = Preamble + """

            public class AppDbContext : DbContext
            {
                public DbSet<Policy> Policies => Set<Policy>();

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Policy>().IsTemporal();
                }
            }

            public class Repository
            {
                public async Task BulkCloseAsync(AppDbContext db)
                {
                    await {|#0:db.Policies.ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Closed"))|};
                }
            }
        }
        """;

        var expected = VerifyCS.Diagnostic()
            .WithLocation(0)
            .WithArguments("ExecuteUpdateAsync", "Policy");

        await VerifyCS.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_on_context_Set_of_temporal_entity_fires()
    {
        var source = Preamble + """

            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Policy>().IsTemporal();
                }
            }

            public class Repository
            {
                public async Task PurgeAsync(AppDbContext db)
                {
                    await {|#0:db.Set<Policy>().ExecuteDeleteAsync()|};
                }
            }
        }
        """;

        var expected = VerifyCS.Diagnostic()
            .WithLocation(0)
            .WithArguments("ExecuteDeleteAsync", "Policy");

        await VerifyCS.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ExecuteUpdate_on_composed_query_of_temporal_entity_fires()
    {
        var source = Preamble + """

            public class AppDbContext : DbContext
            {
                public DbSet<Policy> Policies => Set<Policy>();

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Policy>().IsTemporal();
                }
            }

            public class Repository
            {
                public void CloseExpired(AppDbContext db)
                {
                    {|#0:db.Policies.Where(p => p.Status == "Expired").ExecuteUpdate(s => s.SetProperty(p => p.Status, "Closed"))|};
                }
            }
        }
        """;

        var expected = VerifyCS.Diagnostic()
            .WithLocation(0)
            .WithArguments("ExecuteUpdate", "Policy");

        await VerifyCS.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ExecuteUpdateAsync_on_non_temporal_entity_does_not_fire()
    {
        var source = Preamble + """

            public class AppDbContext : DbContext
            {
                public DbSet<Policy> Policies => Set<Policy>();
            }

            public class Repository
            {
                public async Task BulkCloseAsync(AppDbContext db)
                {
                    await db.Policies.ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Closed"));
                }
            }
        }
        """;

        await VerifyCS.VerifyAnalyzerAsync(source);
    }

    [Fact]
    public async Task ExecuteUpdateAsync_still_fires_when_HistoryWriter_Trigger_is_configured()
    {
        // Documents the trade-off from DESIGN.md D4 / ExecuteUpdateOrDeleteOnTemporalEntityAnalyzer's own
        // doc comment: the analyzer cannot see which HistoryWriter is configured (a runtime call, not a
        // compile-time fact it can bind to a specific DbContext/entity pairing), so it fires regardless -
        // including here, where Trigger mode actually makes the call perfectly safe. Info severity, plus
        // the message text, is how that uncertainty is surfaced instead of silently guessing either way.
        var source = Preamble + """

            public class AppDbContext : DbContext
            {
                public DbSet<Policy> Policies => Set<Policy>();

                protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
                {
                    optionsBuilder.UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger));
                }

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Policy>().IsTemporal();
                }
            }

            public class Repository
            {
                public async Task BulkCloseAsync(AppDbContext db)
                {
                    await {|#0:db.Policies.ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, "Closed"))|};
                }
            }
        }
        """;

        var expected = VerifyCS.Diagnostic()
            .WithLocation(0)
            .WithArguments("ExecuteUpdateAsync", "Policy");

        await VerifyCS.VerifyAnalyzerAsync(source, expected);
    }

    [Fact]
    public async Task ExecuteUpdate_on_unrelated_type_with_same_method_name_does_not_fire()
    {
        var source = Preamble + """

            public class AppDbContext : DbContext
            {
                public DbSet<Policy> Policies => Set<Policy>();

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Policy>().IsTemporal();
                }
            }

            // A same-named, same-shaped method on an unrelated type - not EF Core's
            // EntityFrameworkQueryableExtensions - must never be mistaken for the real thing.
            public static class FakeQueryableExtensions
            {
                public static Task<int> ExecuteUpdateAsync<TSource>(this IQueryable<TSource> source)
                    => Task.FromResult(0);
            }

            public class Repository
            {
                public async Task NotEfAsync(AppDbContext db)
                {
                    await db.Policies.AsQueryable().ExecuteUpdateAsync();
                }
            }
        }
        """;

        await VerifyCS.VerifyAnalyzerAsync(source);
    }
}
