using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// The history writers seen through their <em>interval shape</em> rather than through injected wall
/// clock: every scenario that writes history runs in both <see cref="HistoryWriter.Interceptor"/> and
/// <see cref="HistoryWriter.Trigger"/> mode and asserts contiguity, openness, tombstones and operation
/// codes — never an absolute timestamp (.claude/rules/tests.md). Trigger-only reach (raw SQL,
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c>, concurrency) has its own cases below (DESIGN.md D3, D4).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TriggerHistoryWriterTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Insert_writes_a_single_open_history_row(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Insert_writes_a_single_open_history_row));

        h.Db.Policies.Add(NewPolicy());
        await h.Db.SaveChangesAsync(Ct);

        var only = Assert.Single(await ReadPolicyHistoryAsync(h.ConnectionString));
        Assert.Equal((short)1, only.Operation);
        Assert.True(only.IsOpen);
        Assert.Equal("ACME-1", only.Number);
        Assert.Equal("Draft", only.Status);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Update_closes_the_previous_version_and_opens_a_contiguous_new_one(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Update_closes_the_previous_version_and_opens_a_contiguous_new_one));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        policy.Status = PolicyStatus.Active;
        policy.Premium = 250.50m;
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadPolicyHistoryAsync(h.ConnectionString);

        Assert.Equal([(short)1, (short)2], versions.Select(v => v.Operation));
        Assert.False(versions[0].IsOpen);
        Assert.Equal(versions[0].ValidTo, versions[1].ValidFrom);   // contiguous: no gap, no overlap
        Assert.True(versions[0].ValidTo > versions[0].ValidFrom);   // strictly positive interval
        Assert.Single(versions, v => v.IsOpen);
        Assert.Equal("Draft", versions[0].Status);
        Assert.Equal("Active", versions[1].Status);
        Assert.Equal(250.50m, versions[1].Premium);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Delete_writes_an_empty_interval_tombstone_and_leaves_no_open_row(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Delete_writes_an_empty_interval_tombstone_and_leaves_no_open_row));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        h.Db.Policies.Remove(policy);
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadPolicyHistoryAsync(h.ConnectionString);

        Assert.Equal(2, versions.Count);
        Assert.DoesNotContain(versions, v => v.IsOpen);             // deleted entity: zero open versions
        Assert.Equal(versions[0].ValidTo, versions[1].ValidFrom);   // tombstone starts where the last real version ended
        var tombstone = versions[1];
        Assert.Equal((short)3, tombstone.Operation);
        Assert.Equal(tombstone.ValidFrom, tombstone.ValidTo);       // empty [ts, ts): never matches AsOf
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Update_touching_only_an_excluded_property_writes_no_history_row(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Update_touching_only_an_excluded_property_writes_no_history_row));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        policy.UpdatedAt = DateTimeOffset.UtcNow;
        await h.Db.SaveChangesAsync(Ct);

        var only = Assert.Single(await ReadPolicyHistoryAsync(h.ConnectionString));
        Assert.Equal((short)1, only.Operation);
        Assert.True(only.IsOpen);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Composite_key_update_closes_only_the_matching_version_chain(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Composite_key_update_closes_only_the_matching_version_chain));

        h.Db.Readings.Add(new Reading { SensorId = 1, Sequence = 1, Value = 10 });
        h.Db.Readings.Add(new Reading { SensorId = 2, Sequence = 1, Value = 20 });
        await h.Db.SaveChangesAsync(Ct);

        h.Db.Readings.Single(r => r.SensorId == 1).Value = 11;
        await h.Db.SaveChangesAsync(Ct);

        var sensor1 = await ReadReadingHistoryAsync(h.ConnectionString, sensorId: 1);
        var sensor2 = await ReadReadingHistoryAsync(h.ConnectionString, sensorId: 2);

        Assert.Equal(2, sensor1.Count);
        Assert.Equal(sensor1[0].ValidTo, sensor1[1].ValidFrom);
        Assert.Single(sensor1, v => v.IsOpen);

        var untouched = Assert.Single(sensor2);
        Assert.True(untouched.IsOpen);                              // the other chain was left alone
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Enum_jsonb_and_array_columns_round_trip_into_history(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Enum_jsonb_and_array_columns_round_trip_into_history));

        var policy = NewPolicy();
        policy.Tags = ["gold", "renewal"];
        policy.Metadata = """{"broker":"kb"}""";
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        var only = Assert.Single(await ReadPolicyHistoryAsync(h.ConnectionString));
        Assert.Equal("Draft", only.Status);                        // enum stored as text, like the main table
        Assert.Equal(["gold", "renewal"], only.Tags);
        Assert.Equal("""{"broker": "kb"}""", only.Metadata);       // jsonb normalises whitespace
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task History_row_lives_in_the_same_transaction_as_the_data_change(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(History_row_lives_in_the_same_transaction_as_the_data_change));

        await using (var tx = await h.Db.Database.BeginTransactionAsync(Ct))
        {
            h.Db.Policies.Add(NewPolicy());
            await h.Db.SaveChangesAsync(Ct);
            await tx.RollbackAsync(Ct);
        }

        await using var conn = new NpgsqlConnection(h.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var check = conn.CreateCommand();
        check.CommandText = "select count(*) from policies_history";
        Assert.Equal(0L, (long)(await check.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task ExecuteUpdate_writes_history_in_trigger_mode()
    {
        await using var h = await CreateAsync(HistoryWriter.Trigger, nameof(ExecuteUpdate_writes_history_in_trigger_mode));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        await h.Db.Policies.Where(p => p.Id == policy.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, PolicyStatus.Active), Ct);

        var versions = await ReadPolicyHistoryAsync(h.ConnectionString);
        Assert.Equal([(short)1, (short)2], versions.Select(v => v.Operation));   // bulk update caught by the trigger
        Assert.Equal(versions[0].ValidTo, versions[1].ValidFrom);
        Assert.Single(versions, v => v.IsOpen);
        Assert.Equal("Active", versions[1].Status);
        Assert.Null(versions[1].ChangedBy);                          // no change context on a bulk write (DESIGN.md D4)
    }

    [Fact]
    public async Task ExecuteDelete_writes_a_tombstone_in_trigger_mode()
    {
        await using var h = await CreateAsync(HistoryWriter.Trigger, nameof(ExecuteDelete_writes_a_tombstone_in_trigger_mode));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        await h.Db.Policies.Where(p => p.Id == policy.Id).ExecuteDeleteAsync(Ct);

        var versions = await ReadPolicyHistoryAsync(h.ConnectionString);
        Assert.DoesNotContain(versions, v => v.IsOpen);
        var tombstone = versions[^1];
        Assert.Equal((short)3, tombstone.Operation);
        Assert.Equal(tombstone.ValidFrom, tombstone.ValidTo);
    }

    [Fact]
    public async Task Concurrent_updates_in_separate_transactions_produce_contiguous_intervals()
    {
        var cs = await postgres.CreateDatabaseAsync(nameof(Concurrent_updates_in_separate_transactions_produce_contiguous_intervals), Ct);
        await using (var seed = NewContext(cs, HistoryWriter.Trigger))
        {
            await seed.Database.EnsureCreatedAsync(Ct);
            seed.Policies.Add(NewPolicy());
            await seed.SaveChangesAsync(Ct);
        }

        // Two overlapping transactions on the same row: the second blocks on the first's row lock,
        // then runs its trigger. GREATEST(now(), valid_from + 1µs) must still yield a positive,
        // contiguous interval.
        await using var a = NewContext(cs, HistoryWriter.Trigger);
        await using var b = NewContext(cs, HistoryWriter.Trigger);
        await using var txA = await a.Database.BeginTransactionAsync(Ct);
        (await a.Policies.SingleAsync(Ct)).Premium = 200m;
        await a.SaveChangesAsync(Ct);

        var bWork = Task.Run(
            async () =>
            {
                await using var txB = await b.Database.BeginTransactionAsync(Ct);
                (await b.Policies.SingleAsync(Ct)).Premium = 300m;
                await b.SaveChangesAsync(Ct);
                await txB.CommitAsync(Ct);
            },
            Ct);

        await Task.Delay(200, Ct);
        await txA.CommitAsync(Ct);
        await bWork;

        var versions = await ReadPolicyHistoryAsync(cs);
        Assert.Equal([(short)1, (short)2, (short)2], versions.Select(v => v.Operation));
        for (var i = 1; i < versions.Count; i++)
        {
            Assert.Equal(versions[i - 1].ValidTo, versions[i].ValidFrom);   // contiguous
            Assert.True(versions[i - 1].ValidTo > versions[i - 1].ValidFrom);
        }

        Assert.Single(versions, v => v.IsOpen);
        Assert.Equal(300m, versions[^1].Premium);
    }

    private static Policy NewPolicy(string number = "ACME-1")
        => new() { Number = number, Status = PolicyStatus.Draft, Premium = 100m };

    private async Task<Harness> CreateAsync(HistoryWriter writer, string dbName)
    {
        // PostgreSQL truncates identifiers at 63 bytes; keep room for a per-writer suffix so the two
        // [Theory] runs do not collide on the database name.
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var trimmed = dbName.Length > 58 ? dbName[..58] : dbName;
        var cs = await postgres.CreateDatabaseAsync(trimmed + suffix, Ct);
        var db = NewContext(cs, writer);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(cs, db);
    }

    private static PolicyContext NewContext(string connectionString, HistoryWriter writer)
    {
        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(connectionString)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;
        return new PolicyContext(options);
    }

    private static async Task<List<HistoryRow>> ReadPolicyHistoryAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select number, status, premium, tags, metadata, changed_by, valid_from, valid_to, operation
            from policies_history
            order by valid_from, history_id
            """;

        var rows = new List<HistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDecimal(2),
                reader.GetFieldValue<string[]>(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTime>(6),
                reader.GetFieldValue<DateTime>(7),
                reader.GetInt16(8)));
        }

        return rows;
    }

    private static async Task<List<HistoryRow>> ReadReadingHistoryAsync(string connectionString, int sensorId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select sequence, value, valid_from, valid_to, operation
            from readings_history
            where sensor_id = @id
            order by valid_from, history_id
            """;
        cmd.Parameters.AddWithValue("id", sensorId);

        var rows = new List<HistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(
                reader.GetInt32(0).ToString(),
                string.Empty,
                (decimal)reader.GetDouble(1),
                [],
                string.Empty,
                null,
                reader.GetFieldValue<DateTime>(2),
                reader.GetFieldValue<DateTime>(3),
                reader.GetInt16(4)));
        }

        return rows;
    }

    private sealed record HistoryRow(
        string Number,
        string Status,
        decimal Premium,
        string[] Tags,
        string Metadata,
        string? ChangedBy,
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
        public string[] Tags { get; set; } = [];
        public string Metadata { get; set; } = "{}";
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class Reading
    {
        public int SensorId { get; set; }
        public int Sequence { get; set; }
        public double Value { get; set; }
    }

    private sealed class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options)
    {
        public DbSet<Policy> Policies => Set<Policy>();

        public DbSet<Reading> Readings => Set<Reading>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");
            policy.Property(p => p.Status).HasColumnName("status").HasConversion<string>();
            policy.Property(p => p.Premium).HasColumnName("premium").HasPrecision(18, 4);
            policy.Property(p => p.Tags).HasColumnName("tags");
            policy.Property(p => p.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            policy.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            policy.IsTemporal(t => t.Exclude(p => p.UpdatedAt));

            var reading = modelBuilder.Entity<Reading>();
            reading.ToTable("readings");
            reading.HasKey(r => new { r.SensorId, r.Sequence });
            reading.Property(r => r.SensorId).HasColumnName("sensor_id");
            reading.Property(r => r.Sequence).HasColumnName("sequence");
            reading.Property(r => r.Value).HasColumnName("value");
            reading.IsTemporal();
        }
    }
}
