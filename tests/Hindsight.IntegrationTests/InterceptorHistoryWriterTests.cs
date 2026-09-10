using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// <see cref="HistoryWriter.Interceptor"/>-specific end-to-end tests on a real PostgreSQL: they inject
/// wall clock through <see cref="TimeProvider"/> and assert on the exact instants the interceptor
/// stamps, which only that writer honours. The behaviour shared with <see cref="HistoryWriter.Trigger"/>
/// — interval shape, tombstones, excluded columns, composite keys — is covered in both modes by
/// <see cref="TriggerHistoryWriterTests"/> (.claude/rules/tests.md).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InterceptorHistoryWriterTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Insert_writes_a_single_open_history_row(HistoryWriter writer)
    {
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(writer, nameof(Insert_writes_a_single_open_history_row), time);

        h.Db.Policies.Add(NewPolicy());
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString, policyId: 1);

        var only = Assert.Single(versions);
        Assert.Equal((short)1, only.Operation);
        Assert.Equal(_t0.UtcDateTime, only.ValidFrom);
        Assert.True(only.IsOpen);
        Assert.Equal("ACME-1", only.Number);
        Assert.Equal("Draft", only.Status);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Update_closes_the_previous_version_and_opens_a_contiguous_new_one(HistoryWriter writer)
    {
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(Update_closes_the_previous_version_and_opens_a_contiguous_new_one), time);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        time.Advance(TimeSpan.FromHours(1));
        policy.Status = PolicyStatus.Active;
        policy.Premium = 250.50m;
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString, policyId: 1);

        Assert.Equal(2, versions.Count);
        Assert.Equal((short)1, versions[0].Operation);
        Assert.Equal((short)2, versions[1].Operation);
        Assert.Equal(_t0.UtcDateTime, versions[0].ValidFrom);
        Assert.Equal(_t0.UtcDateTime.AddHours(1), versions[0].ValidTo); // closed exactly at the new timestamp
        Assert.Equal(versions[0].ValidTo, versions[1].ValidFrom);      // contiguous: no gap, no overlap
        Assert.True(versions[1].IsOpen);
        Assert.Single(versions, v => v.IsOpen);
        Assert.Equal("Draft", versions[0].Status);
        Assert.Equal("Active", versions[1].Status);
        Assert.Equal(250.50m, versions[1].Premium);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Delete_closes_the_previous_version_and_writes_an_empty_interval_tombstone(HistoryWriter writer)
    {
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(Delete_closes_the_previous_version_and_writes_an_empty_interval_tombstone), time);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        time.Advance(TimeSpan.FromHours(2));
        h.Db.Policies.Remove(policy);
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString, policyId: 1);

        Assert.Equal(2, versions.Count);
        Assert.DoesNotContain(versions, v => v.IsOpen);              // zero open versions for a deleted entity
        Assert.Equal(_t0.UtcDateTime.AddHours(2), versions[0].ValidTo);
        var tombstone = versions[1];
        Assert.Equal((short)3, tombstone.Operation);
        Assert.Equal(_t0.UtcDateTime.AddHours(2), tombstone.ValidFrom);
        Assert.Equal(tombstone.ValidFrom, tombstone.ValidTo);        // empty [ts, ts): never matches AsOf
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Update_touching_only_an_excluded_property_writes_no_history_row(HistoryWriter writer)
    {
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(Update_touching_only_an_excluded_property_writes_no_history_row), time);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        time.Advance(TimeSpan.FromMinutes(30));
        policy.UpdatedAt = time.GetUtcNow();
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString, policyId: 1);

        var only = Assert.Single(versions);
        Assert.Equal((short)1, only.Operation);
        Assert.True(only.IsOpen);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task All_rows_of_one_SaveChanges_share_a_single_timestamp(HistoryWriter writer)
    {
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(All_rows_of_one_SaveChanges_share_a_single_timestamp), time);

        h.Db.Policies.Add(NewPolicy("ACME-1"));
        h.Db.Policies.Add(NewPolicy("ACME-2"));
        await h.Db.SaveChangesAsync(Ct);

        var first = Assert.Single(await ReadHistoryAsync(h.ConnectionString, policyId: 1));
        var second = Assert.Single(await ReadHistoryAsync(h.ConnectionString, policyId: 2));

        Assert.Equal(first.ValidFrom, second.ValidFrom);
        Assert.Equal(_t0.UtcDateTime, first.ValidFrom);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task History_row_lives_in_the_same_transaction_as_the_data_change(HistoryWriter writer)
    {
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(History_row_lives_in_the_same_transaction_as_the_data_change), time);

        await using (var tx = await h.Db.Database.BeginTransactionAsync(Ct))
        {
            h.Db.Policies.Add(NewPolicy());
            await h.Db.SaveChangesAsync(Ct);

            // Same open connection + transaction: the history row is already visible here...
            await using var cmd = h.Db.Database.GetDbConnection().CreateCommand();
            cmd.Transaction = tx.GetDbTransaction();
            cmd.CommandText = "select count(*) from policies_history";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(Ct))!);

            await tx.RollbackAsync(Ct);
        }

        // ...and rolls back with it.
        await using var conn = new NpgsqlConnection(h.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var check = conn.CreateCommand();
        check.CommandText = "select count(*) from policies_history";
        Assert.Equal(0L, (long)(await check.ExecuteScalarAsync(Ct))!);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Repeated_SaveChanges_in_one_transaction_do_not_double_write(HistoryWriter writer)
    {
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(Repeated_SaveChanges_in_one_transaction_do_not_double_write), time);

        await using var tx = await h.Db.Database.BeginTransactionAsync(Ct);

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        time.Advance(TimeSpan.FromMinutes(10));
        policy.Status = PolicyStatus.Active;
        await h.Db.SaveChangesAsync(Ct);

        await tx.CommitAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString, policyId: 1);

        Assert.Equal(2, versions.Count);
        Assert.Equal(versions[0].ValidTo, versions[1].ValidFrom);
        Assert.Single(versions, v => v.IsOpen);
        Assert.Equal([(short)1, (short)2], versions.Select(v => v.Operation));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Enum_jsonb_and_array_columns_round_trip_into_history(HistoryWriter writer)
    {
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(Enum_jsonb_and_array_columns_round_trip_into_history), time);

        var policy = NewPolicy();
        policy.Tags = ["gold", "renewal"];
        policy.Metadata = """{"broker":"kb"}""";
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        var only = Assert.Single(await ReadHistoryAsync(h.ConnectionString, policyId: 1));

        Assert.Equal("Draft", only.Status);                 // enum stored as text, like the main table
        Assert.Equal(["gold", "renewal"], only.Tags);
        Assert.Equal("""{"broker": "kb"}""", only.Metadata); // jsonb normalises whitespace
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    public async Task Composite_key_update_closes_only_the_matching_version_chain(HistoryWriter writer)
    {
        var time = new MutableTimeProvider(_t0);
        await using var h = await CreateAsync(
            writer, nameof(Composite_key_update_closes_only_the_matching_version_chain), time);

        h.Db.Readings.Add(new Reading { SensorId = 1, Sequence = 1, Value = 10 });
        h.Db.Readings.Add(new Reading { SensorId = 2, Sequence = 1, Value = 20 });
        await h.Db.SaveChangesAsync(Ct);

        time.Advance(TimeSpan.FromMinutes(5));
        h.Db.Readings.Single(r => r.SensorId == 1).Value = 11;
        await h.Db.SaveChangesAsync(Ct);

        var sensor1 = await ReadReadingHistoryAsync(h.ConnectionString, sensorId: 1);
        var sensor2 = await ReadReadingHistoryAsync(h.ConnectionString, sensorId: 2);

        Assert.Equal(2, sensor1.Count);
        Assert.Equal(sensor1[0].ValidTo, sensor1[1].ValidFrom);
        Assert.Single(sensor1, v => v.IsOpen);

        var untouched = Assert.Single(sensor2);
        Assert.True(untouched.IsOpen); // the other chain was left alone
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Policy NewPolicy(string number = "ACME-1")
        => new() { Number = number, Status = PolicyStatus.Draft, Premium = 100m };

    private async Task<Harness> CreateAsync(HistoryWriter writer, string dbName, MutableTimeProvider time)
    {
        var cs = await postgres.CreateDatabaseAsync(dbName, Ct);
        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .UseApplicationServiceProvider(new SingleServiceProvider(typeof(TimeProvider), time))
            .UseHindsight(hb => hb.UseHistoryWriter(writer))
            .Options;

        var db = new PolicyContext(options);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(cs, db);
    }

    private static async Task<List<HistoryRow>> ReadHistoryAsync(string connectionString, int policyId)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select history_id, id, number, status, premium, tags, metadata, valid_from, valid_to, operation
            from policies_history
            where id = @id
            order by valid_from, history_id
            """;
        cmd.Parameters.AddWithValue("id", policyId);

        var rows = new List<HistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetFieldValue<string[]>(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTime>(7),
                reader.GetFieldValue<DateTime>(8),
                reader.GetInt16(9)));
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
            select sensor_id, sequence, value, valid_from, valid_to, operation
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
                0,
                reader.GetInt32(0),
                reader.GetInt32(1).ToString(),
                string.Empty,
                (decimal)reader.GetDouble(2),
                [],
                string.Empty,
                reader.GetFieldValue<DateTime>(3),
                reader.GetFieldValue<DateTime>(4),
                reader.GetInt16(5)));
        }

        return rows;
    }

    private sealed record HistoryRow(
        long HistoryId,
        int Id,
        string Number,
        string Status,
        decimal Premium,
        string[] Tags,
        string Metadata,
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

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type type) => type == serviceType ? instance : null;
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

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
