using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// Pins down the ordering claim in docs/articles/limitations.md → Known trade-offs: a
/// <see cref="SaveChangesInterceptor"/> added with <c>optionsBuilder.AddInterceptors(...)</c> always
/// runs after Hindsight's own (<c>HindsightOptionsExtension</c> registers
/// <c>HistorySnapshotGuardInterceptor</c> and the history writer through <c>ApplyServices</c>, ahead of
/// the explicit <c>AddInterceptors</c> list). <see cref="AuditStampInterceptor"/> reproduces the
/// concrete failure: it finds a reloaded, <c>Unchanged</c> <see cref="Policy"/> in its own
/// <c>SavingChanges</c> and flips it to <c>Modified</c> there — after
/// <c>HistoryRowPlan.BuildPending</c> (<see cref="HistoryWriter.Interceptor"/>) has already decided the
/// entity produces no history row. <see cref="HistoryWriter.Trigger"/> has no such gap: the physical
/// trigger fires on the real <c>UPDATE</c>, after every C# interceptor (this one included) has already
/// run, regardless of registration order.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CustomInterceptorOrderingTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Interceptor_mode_writes_no_history_row_for_a_change_a_later_registered_interceptor_makes_in_SavingChanges()
    {
        await using var h = await CreateAsync(
            HistoryWriter.Interceptor,
            nameof(Interceptor_mode_writes_no_history_row_for_a_change_a_later_registered_interceptor_makes_in_SavingChanges));

        h.Db.Policies.Add(new Policy { Number = "ACME-1", Premium = 100m });
        await h.Db.SaveChangesAsync(Ct); // v1
        h.Db.ChangeTracker.Clear();

        // Nothing is changed through the DbContext before this call: the entity is Unchanged when
        // AuditStampInterceptor.SavingChanges runs (after Hindsight's own SavingChanges handlers).
        await h.Db.Policies.SingleAsync(Ct);
        await h.Db.SaveChangesAsync(Ct);

        Assert.Equal(999m, await ReadMainTablePremiumAsync(h.ConnectionString)); // the UPDATE really ran
        Assert.Equal(1L, await CountHistoryRowsAsync(h.ConnectionString));       // ...but no new history row
    }

    [Fact]
    public async Task Trigger_mode_writes_a_history_row_for_the_same_change_because_the_physical_UPDATE_already_happened()
    {
        await using var h = await CreateAsync(
            HistoryWriter.Trigger,
            nameof(Trigger_mode_writes_a_history_row_for_the_same_change_because_the_physical_UPDATE_already_happened));

        h.Db.Policies.Add(new Policy { Number = "ACME-1", Premium = 100m });
        await h.Db.SaveChangesAsync(Ct); // v1
        h.Db.ChangeTracker.Clear();

        await h.Db.Policies.SingleAsync(Ct);
        await h.Db.SaveChangesAsync(Ct);

        Assert.Equal(999m, await ReadMainTablePremiumAsync(h.ConnectionString));
        Assert.Equal(2L, await CountHistoryRowsAsync(h.ConnectionString)); // the trigger saw it
    }

    private async Task<Harness> CreateAsync(HistoryWriter writer, string dbName)
    {
        var cs = await postgres.CreateDatabaseAsync(dbName, Ct);
        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .EnableServiceProviderCaching(false)
            .UseHindsight(hb => hb.UseHistoryWriter(writer))
            // Chained after UseHindsight(...), matching the "typical pattern" from the limitation —
            // but the doc's point (and Scenario E/F of the manual probe behind it) is that this runs
            // after Hindsight's own interceptors regardless of chain order.
            .AddInterceptors(new AuditStampInterceptor())
            .Options;

        var db = new PolicyContext(options);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(cs, db);
    }

    private static async Task<decimal> ReadMainTablePremiumAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select premium from policies limit 1";
        return (decimal)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<long> CountHistoryRowsAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from policies_history";
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private sealed class Harness(string connectionString, PolicyContext db) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public PolicyContext Db { get; } = db;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public decimal Premium { get; set; }
    }

    private sealed class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options)
    {
        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");
            policy.Property(p => p.Premium).HasColumnName("premium").HasPrecision(18, 4);
            policy.IsTemporal();
        }
    }

    // The audit-stamp footgun from docs/articles/limitations.md: a SaveChangesInterceptor that reacts
    // to some out-of-band signal (here, simply "this Policy is tracked") by moving an Unchanged entity
    // to Modified from inside its own SavingChanges — after Hindsight's SavingChanges handlers, added
    // first through HindsightOptionsExtension.ApplyServices, have already run and taken their snapshot.
    private sealed class AuditStampInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            Stamp(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Stamp(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private static void Stamp(DbContext? context)
        {
            if (context is null)
            {
                return;
            }

            foreach (var entry in context.ChangeTracker.Entries<Policy>())
            {
                if (entry.State != EntityState.Unchanged)
                {
                    continue;
                }

                entry.Entity.Premium = 999m;
                entry.State = EntityState.Modified;
            }
        }
    }
}
