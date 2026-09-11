using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// What happens to a caller's <see cref="DbContext.ChangeTracker"/> when the history write itself fails
/// — as opposed to a change-context provider exception (<see cref="ChangeContextTests"/>), which throws
/// before the data change ever reaches the database. Only <see cref="HistoryWriter.Interceptor"/> can
/// fail here: it writes history in <c>SavedChanges</c>, after EF Core's own <c>SaveChanges</c> pipeline
/// has already sent the data change and (per <c>StateManager.SaveChanges</c>) called
/// <c>ChangeTracker.AcceptAllChanges()</c> — flipping every <c>Added</c>/<c>Modified</c> entry to
/// <c>Unchanged</c> and detaching every <c>Deleted</c> entry — strictly before the terminal
/// <c>SavedChanges</c>/<c>SavedChangesAsync</c> interceptor event fires. <see cref="HistoryWriter.Trigger"/>
/// has no equivalent gap: its history row is written by the trigger in the same statement/transaction as
/// the data change, so a failure there rolls back atomically with no post-hoc accept-vs-rollback race
/// (DESIGN.md D3).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistoryWriteFailureTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Failed_history_write_after_insert_rolls_back_the_data_and_restores_the_Added_state()
    {
        var provider = new RecordingChangeContextProvider(() => new ChangeContext { Extra = "not valid json" });
        await using var h = await CreateAsync(nameof(Failed_history_write_after_insert_rolls_back_the_data_and_restores_the_Added_state), provider);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        var entry = h.Db.Entry(policy);

        await Assert.ThrowsAsync<PostgresException>(() => h.Db.SaveChangesAsync(Ct));

        // The data INSERT succeeded (it carries no invalid jsonb); the history INSERT failed on the
        // malformed `extra` value. The interceptor owns the transaction, so both roll back together —
        // ground truth, not assumed (CLAUDE.md rule 2).
        Assert.Equal(0, await CountAsync(h.ConnectionString, "policies"));
        Assert.Equal(0, await CountAsync(h.ConnectionString, "policies_history"));

        // The fix under test: the entry must still read Added, not Unchanged/Detached, so a caller who
        // catches the exception and retries SaveChanges() actually retries instead of silently no-op-ing.
        Assert.Equal(EntityState.Added, entry.State);

        // A straight retry (context still tracks it, no reload) must now succeed and produce exactly the
        // history a first successful attempt would have.
        provider.Factory = () => ChangeContext.Empty;
        await h.Db.SaveChangesAsync(Ct);

        Assert.Equal(1, await CountAsync(h.ConnectionString, "policies"));
        Assert.Equal(1, await CountAsync(h.ConnectionString, "policies_history"));
        Assert.Equal(EntityState.Unchanged, entry.State);
    }

    [Fact]
    public async Task Failed_history_write_after_update_rolls_back_the_data_and_restores_the_Modified_state()
    {
        var provider = new RecordingChangeContextProvider(() => ChangeContext.Empty);
        await using var h = await CreateAsync(nameof(Failed_history_write_after_update_rolls_back_the_data_and_restores_the_Modified_state), provider);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        provider.Factory = () => new ChangeContext { Extra = "not valid json" };
        policy.Premium = 250m;
        var entry = h.Db.Entry(policy);

        await Assert.ThrowsAsync<PostgresException>(() => h.Db.SaveChangesAsync(Ct));

        // The update to the main row rolled back too: still the original premium in the database.
        Assert.Equal(100m, await ReadPremiumAsync(h.ConnectionString, policy.Id));
        Assert.Equal(1, await CountAsync(h.ConnectionString, "policies_history")); // only the insert's row

        Assert.Equal(EntityState.Modified, entry.State);
        Assert.True(entry.Property(nameof(Policy.Premium)).IsModified);

        provider.Factory = () => ChangeContext.Empty;
        await h.Db.SaveChangesAsync(Ct);

        Assert.Equal(250m, await ReadPremiumAsync(h.ConnectionString, policy.Id));
        Assert.Equal(2, await CountAsync(h.ConnectionString, "policies_history"));
        Assert.Equal(EntityState.Unchanged, entry.State);
    }

    [Fact]
    public async Task Failed_history_write_after_delete_rolls_back_the_data_but_the_entry_stays_detached()
    {
        var provider = new RecordingChangeContextProvider(() => ChangeContext.Empty);
        await using var h = await CreateAsync(nameof(Failed_history_write_after_delete_rolls_back_the_data_but_the_entry_stays_detached), provider);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        provider.Factory = () => new ChangeContext { Extra = "not valid json" };
        h.Db.Policies.Remove(policy);
        var entry = h.Db.Entry(policy);

        await Assert.ThrowsAsync<PostgresException>(() => h.Db.SaveChangesAsync(Ct));

        // The row is still there in the database — the delete rolled back with the tombstone write.
        Assert.Equal(1, await CountAsync(h.ConnectionString, "policies"));
        Assert.Equal(1, await CountAsync(h.ConnectionString, "policies_history"));

        // Documented sharp edge (docs/articles/limitations.md): EF Core's AcceptAllChanges already
        // detached the entry for a Deleted row before SavedChanges ran, and PendingHistoryRow never
        // kept an EntityEntry for a delete (its values are captured up front) — there is nothing to
        // re-mark. The entity is unrecoverably gone from the tracker even though the row survives in
        // the database; a caller must re-query, not just retry SaveChanges on the same instance.
        Assert.Equal(EntityState.Detached, entry.State);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Policy NewPolicy(string number = "ACME-1")
        => new() { Number = number, Status = PolicyStatus.Draft, Premium = 100m };

    private async Task<Harness> CreateAsync(string dbName, RecordingChangeContextProvider provider)
    {
        var cs = await postgres.CreateDatabaseAsync(dbName, Ct);

        var services = new Dictionary<Type, object>
        {
            [typeof(TimeProvider)] = new MutableTimeProvider(_t0),
            [typeof(RecordingChangeContextProvider)] = provider,
        };

        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .UseApplicationServiceProvider(new StubServiceProvider(services))
            .UseHindsight(hb => hb
                .WithChangeContext<RecordingChangeContextProvider>()
                .UseHistoryWriter(HistoryWriter.Interceptor))
            .Options;

        var db = new PolicyContext(options);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(cs, db);
    }

    private static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {table}";
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<decimal> ReadPremiumAsync(string connectionString, int policyId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select premium from policies where id = @id";
        cmd.Parameters.AddWithValue("id", policyId);
        return (decimal)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private sealed class Harness(string connectionString, PolicyContext db) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public PolicyContext Db { get; } = db;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class StubServiceProvider(Dictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.GetValueOrDefault(serviceType);
    }

    // Registered on the application service provider (SingleServiceProvider only holds TimeProvider), so
    // WithChangeContext<T> falls back to its parameterless constructor — the mutable Factory lets each
    // test flip between a failing and a succeeding ChangeContext without a new provider instance.
    private sealed class RecordingChangeContextProvider() : IChangeContextProvider
    {
        public RecordingChangeContextProvider(Func<ChangeContext> factory)
            : this() => Factory = factory;

        public Func<ChangeContext> Factory { get; set; } = () => ChangeContext.Empty;

        public ChangeContext GetChangeContext(DbContext context) => Factory();
    }

    private enum PolicyStatus
    {
        Draft,
        Active,
        Cancelled,
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public PolicyStatus Status { get; set; }
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
            policy.Property(p => p.Status).HasColumnName("status").HasConversion<string>();
            policy.Property(p => p.Premium).HasColumnName("premium").HasPrecision(18, 4);
            policy.IsTemporal();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start;
    }
}
