using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// End-to-end tests for the change-context columns (<c>changed_by</c>, <c>changed_by_name</c>,
/// <c>correlation_id</c>, <c>reason</c>, <c>extra</c>) on a real PostgreSQL, run in both
/// <see cref="HistoryWriter.Interceptor"/> mode (the provider is read in the interceptor and written
/// into each <c>INSERT</c>) and <see cref="HistoryWriter.Trigger"/> mode (the provider is read once per
/// <c>SaveChanges</c> and pushed into the transaction with <c>set_config</c>, then read back by the
/// trigger). Assertions are on the recorded values, not on wall-clock timing (.claude/rules/tests.md).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChangeContextTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Provider_values_populate_the_context_columns_on_insert(HistoryWriter writer)
    {
        var provider = new RecordingChangeContextProvider(() => new ChangeContext
        {
            UserId = "user-42",
            UserName = "Ada Lovelace",
            CorrelationId = "corr-1",
            Reason = "initial load",
            Extra = """{"ip":"10.0.0.1"}""",
        });
        await using var h = await CreateAsync(writer, nameof(Provider_values_populate_the_context_columns_on_insert), new(_t0), provider);

        h.Db.Policies.Add(NewPolicy());
        await h.Db.SaveChangesAsync(Ct);

        var row = Assert.Single(await ReadContextAsync(h.ConnectionString, policyId: 1));
        Assert.Equal("user-42", row.ChangedBy);
        Assert.Equal("Ada Lovelace", row.ChangedByName);
        Assert.Equal("corr-1", row.CorrelationId);
        Assert.Equal("initial load", row.Reason);
        Assert.Equal("""{"ip": "10.0.0.1"}""", row.Extra); // jsonb normalises whitespace
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Update_stamps_the_new_open_row_and_leaves_the_closed_row_untouched(HistoryWriter writer)
    {
        var current = new ChangeContext { UserId = "u1", Reason = "created" };
        var provider = new RecordingChangeContextProvider(() => current);
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(Update_stamps_the_new_open_row_and_leaves_the_closed_row_untouched), time, provider);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        time.Advance(TimeSpan.FromHours(1));
        current = new ChangeContext { UserId = "u2", Reason = "premium adjusted" };
        policy.Premium = 250m;
        await h.Db.SaveChangesAsync(Ct);

        var rows = await ReadContextAsync(h.ConnectionString, policyId: 1);
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsOpen);
        Assert.Equal("u1", rows[0].ChangedBy);          // the close UPDATE did not overwrite the first row
        Assert.Equal("created", rows[0].Reason);
        Assert.True(rows[1].IsOpen);
        Assert.Equal("u2", rows[1].ChangedBy);
        Assert.Equal("premium adjusted", rows[1].Reason);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Delete_tombstone_carries_the_context(HistoryWriter writer)
    {
        var provider = new RecordingChangeContextProvider(() => new ChangeContext { UserId = "remover", Reason = "gdpr erasure" });
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(writer, nameof(Delete_tombstone_carries_the_context), time, provider);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        time.Advance(TimeSpan.FromHours(2));
        h.Db.Policies.Remove(policy);
        await h.Db.SaveChangesAsync(Ct);

        var rows = await ReadContextAsync(h.ConnectionString, policyId: 1);
        var tombstone = rows[^1];
        Assert.Equal((short)3, tombstone.Operation);
        Assert.Equal(tombstone.ValidFrom, tombstone.ValidTo); // empty interval
        Assert.Equal("remover", tombstone.ChangedBy);
        Assert.Equal("gdpr erasure", tombstone.Reason);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task WithReason_overrides_the_provider_reason_only_inside_the_scope(HistoryWriter writer)
    {
        var provider = new RecordingChangeContextProvider(() => new ChangeContext { UserId = "u", Reason = "provider reason" });
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(WithReason_overrides_the_provider_reason_only_inside_the_scope), time, provider);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        time.Advance(TimeSpan.FromMinutes(10));
        using (h.Db.WithReason("scoped reason"))
        {
            policy.Premium = 111m;
            await h.Db.SaveChangesAsync(Ct);
        }

        time.Advance(TimeSpan.FromMinutes(10));
        policy.Premium = 222m;
        await h.Db.SaveChangesAsync(Ct);

        var rows = await ReadContextAsync(h.ConnectionString, policyId: 1);
        Assert.Equal(3, rows.Count);
        Assert.Equal("provider reason", rows[0].Reason);
        Assert.Equal("scoped reason", rows[1].Reason);   // only the save inside the using
        Assert.Equal("provider reason", rows[2].Reason);
        Assert.All(rows, r => Assert.Equal("u", r.ChangedBy)); // the rest of the context is unaffected
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Without_a_provider_the_context_columns_are_null_and_SaveChanges_succeeds(HistoryWriter writer)
    {
        await using var h = await CreateAsync(
            writer, nameof(Without_a_provider_the_context_columns_are_null_and_SaveChanges_succeeds), new(_t0), provider: null);

        h.Db.Policies.Add(NewPolicy());
        await h.Db.SaveChangesAsync(Ct);

        var row = Assert.Single(await ReadContextAsync(h.ConnectionString, policyId: 1));
        Assert.Null(row.ChangedBy);
        Assert.Null(row.ChangedByName);
        Assert.Null(row.CorrelationId);
        Assert.Null(row.Reason);
        Assert.Null(row.Extra);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task WithReason_works_without_a_registered_provider(HistoryWriter writer)
    {
        await using var h = await CreateAsync(
            writer, nameof(WithReason_works_without_a_registered_provider), new(_t0), provider: null);

        using (h.Db.WithReason("manual fix"))
        {
            h.Db.Policies.Add(NewPolicy());
            await h.Db.SaveChangesAsync(Ct);
        }

        var row = Assert.Single(await ReadContextAsync(h.ConnectionString, policyId: 1));
        Assert.Equal("manual fix", row.Reason);
        Assert.Null(row.ChangedBy);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task The_provider_is_called_once_per_SaveChanges_regardless_of_row_count(HistoryWriter writer)
    {
        var provider = new RecordingChangeContextProvider(() => new ChangeContext { UserId = "u" });
        await using var h = await CreateAsync(
            writer, nameof(The_provider_is_called_once_per_SaveChanges_regardless_of_row_count), new(_t0), provider);

        h.Db.Policies.Add(NewPolicy("ACME-1"));
        h.Db.Policies.Add(NewPolicy("ACME-2"));
        h.Db.Policies.Add(NewPolicy("ACME-3"));
        await h.Db.SaveChangesAsync(Ct);

        Assert.Equal(1, provider.Calls); // one call for three history rows, not three
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task A_provider_exception_propagates_and_nothing_is_committed(HistoryWriter writer)
    {
        var provider = new RecordingChangeContextProvider(
            () => throw new InvalidOperationException("provider blew up"));
        await using var h = await CreateAsync(
            writer, nameof(A_provider_exception_propagates_and_nothing_is_committed), new(_t0), provider);

        h.Db.Policies.Add(NewPolicy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
        Assert.Equal("provider blew up", ex.Message);

        await using var conn = new NpgsqlConnection(h.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select (select count(*) from policies) + (select count(*) from policies_history)";
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Policy NewPolicy(string number = "ACME-1")
        => new() { Number = number, Status = PolicyStatus.Draft, Premium = 100m };

    private async Task<Harness> CreateAsync(
        HistoryWriter writer, string dbName, MutableTimeProvider time, RecordingChangeContextProvider? provider)
    {
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var trimmed = dbName.Length > 58 ? dbName[..58] : dbName;
        var cs = await postgres.CreateDatabaseAsync(trimmed + suffix, Ct);

        var services = new Dictionary<Type, object> { [typeof(TimeProvider)] = time };
        if (provider is not null)
        {
            services[typeof(RecordingChangeContextProvider)] = provider;
        }

        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .UseApplicationServiceProvider(new StubServiceProvider(services))
            .UseHindsight(hb =>
            {
                if (provider is not null)
                {
                    hb.WithChangeContext<RecordingChangeContextProvider>();
                }

                hb.UseHistoryWriter(writer);
            })
            .Options;

        var db = new PolicyContext(options);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(cs, db);
    }

    private static async Task<List<ContextRow>> ReadContextAsync(string connectionString, int policyId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select changed_by, changed_by_name, correlation_id, reason, extra, valid_from, valid_to, operation
            from policies_history
            where id = @id
            order by valid_from, history_id
            """;
        cmd.Parameters.AddWithValue("id", policyId);

        var rows = new List<ContextRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new ContextRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<DateTime>(5),
                reader.GetFieldValue<DateTime>(6),
                reader.GetInt16(7)));
        }

        return rows;
    }

    private sealed record ContextRow(
        string? ChangedBy,
        string? ChangedByName,
        string? CorrelationId,
        string? Reason,
        string? Extra,
        DateTime ValidFrom,
        DateTime ValidTo,
        short Operation)
    {
        public bool IsOpen => ValidTo == DateTime.MaxValue;
    }

    private sealed class Harness(string connectionString, PolicyContext db) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public PolicyContext Db { get; } = db;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class RecordingChangeContextProvider(Func<ChangeContext> factory) : IChangeContextProvider
    {
        public int Calls { get; private set; }

        public ChangeContext GetChangeContext(DbContext context)
        {
            Calls++;
            return factory();
        }
    }

    private sealed class StubServiceProvider(Dictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.GetValueOrDefault(serviceType);
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
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
