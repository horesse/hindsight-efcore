using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// The opt-in <c>db_session_user</c> audit column (DESIGN.md D16): populated by PostgreSQL's own
/// <c>DEFAULT session_user</c>, not by either writer's explicit column list, so it must come back
/// populated in both <see cref="HistoryWriter.Interceptor"/> and <see cref="HistoryWriter.Trigger"/>
/// mode, and — Trigger-only — for <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> and raw SQL, which never go
/// through <c>SaveChanges</c> at all (DESIGN.md D4). An entity that does not call
/// <see cref="TemporalEntityTypeBuilder{TEntity}.WithDbSessionUser"/> gets no such column at all.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbSessionUserTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Insert_populates_db_session_user_with_the_connections_actual_session_user(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Insert_populates_db_session_user_with_the_connections_actual_session_user));

        h.Db.Policies.Add(NewPolicy());
        await h.Db.SaveChangesAsync(Ct);

        var expected = await CurrentSessionUserAsync(h.ConnectionString);
        var row = Assert.Single(await ReadDbSessionUserAsync(h.ConnectionString));
        Assert.Equal(expected, row.DbSessionUser);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Update_and_delete_also_populate_db_session_user(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Update_and_delete_also_populate_db_session_user));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        policy.Premium = 250m;
        await h.Db.SaveChangesAsync(Ct);

        h.Db.Policies.Remove(policy);
        await h.Db.SaveChangesAsync(Ct);

        var expected = await CurrentSessionUserAsync(h.ConnectionString);
        var rows = await ReadDbSessionUserAsync(h.ConnectionString);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(expected, r.DbSessionUser));
    }

    [Fact]
    public async Task ExecuteUpdate_under_the_trigger_writer_also_populates_db_session_user()
    {
        await using var h = await CreateAsync(HistoryWriter.Trigger, nameof(ExecuteUpdate_under_the_trigger_writer_also_populates_db_session_user));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        await h.Db.Policies.Where(p => p.Id == policy.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Status, "Active"), Ct);

        var expected = await CurrentSessionUserAsync(h.ConnectionString);
        var rows = await ReadDbSessionUserAsync(h.ConnectionString);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(expected, r.DbSessionUser));
    }

    [Fact]
    public async Task ExecuteDelete_under_the_trigger_writer_also_populates_db_session_user()
    {
        await using var h = await CreateAsync(HistoryWriter.Trigger, nameof(ExecuteDelete_under_the_trigger_writer_also_populates_db_session_user));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        await h.Db.Policies.Where(p => p.Id == policy.Id).ExecuteDeleteAsync(Ct);

        var expected = await CurrentSessionUserAsync(h.ConnectionString);
        var rows = await ReadDbSessionUserAsync(h.ConnectionString);
        var tombstone = rows[^1];
        Assert.Equal((short)3, tombstone.Operation);
        Assert.Equal(expected, tombstone.DbSessionUser);
    }

    [Fact]
    public async Task Raw_sql_under_the_trigger_writer_also_populates_db_session_user()
    {
        var cs = await postgres.CreateDatabaseAsync(nameof(Raw_sql_under_the_trigger_writer_also_populates_db_session_user), Ct);
        await using (var db = NewContext(cs, HistoryWriter.Trigger))
        {
            await db.Database.EnsureCreatedAsync(Ct);
        }

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await ExecAsync(conn, "insert into policies (id, number, status, premium) values (1, 'ACME-1', 'Draft', 100)");

        var expected = await CurrentSessionUserAsync(cs);
        var row = Assert.Single(await ReadDbSessionUserAsync(cs));
        Assert.Equal(expected, row.DbSessionUser);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task History_T_projects_DbSessionUser_when_the_entity_opted_in(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(History_T_projects_DbSessionUser_when_the_entity_opted_in));

        h.Db.Policies.Add(NewPolicy());
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var expected = await CurrentSessionUserAsync(h.ConnectionString);
        var version = Assert.Single(await h.Db.History<Policy>().ToListAsync(Ct));
        Assert.Equal(expected, version.DbSessionUser);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task History_T_projects_null_DbSessionUser_when_the_entity_did_not_opt_in(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync(
            TrimmedDbName(nameof(History_T_projects_null_DbSessionUser_when_the_entity_did_not_opt_in), writer), Ct);
        await using var db = new NoOptInPolicyContext(new DbContextOptionsBuilder<NoOptInPolicyContext>()
            .UseNpgsql(cs)
            .EnableServiceProviderCaching(false)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options);
        await db.Database.EnsureCreatedAsync(Ct);

        db.Policies.Add(NewPolicy());
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        var version = Assert.Single(await db.History<Policy>().ToListAsync(Ct));
        Assert.Null(version.DbSessionUser);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task An_entity_that_does_not_opt_in_has_no_db_session_user_column(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync(
            TrimmedDbName(nameof(An_entity_that_does_not_opt_in_has_no_db_session_user_column), writer), Ct);
        await using var db = new NoOptInPolicyContext(new DbContextOptionsBuilder<NoOptInPolicyContext>()
            .UseNpgsql(cs)
            .EnableServiceProviderCaching(false)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options);
        await db.Database.EnsureCreatedAsync(Ct);

        db.Policies.Add(NewPolicy());
        await db.SaveChangesAsync(Ct);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select count(*) from information_schema.columns
            where table_name = 'policies_history' and column_name = 'db_session_user'
            """;
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }

    private static Policy NewPolicy(string number = "ACME-1")
        => new() { Number = number, Status = "Draft", Premium = 100m };

    private async Task<Harness> CreateAsync(HistoryWriter writer, string dbName)
    {
        var cs = await postgres.CreateDatabaseAsync(TrimmedDbName(dbName, writer), Ct);
        var db = NewContext(cs, writer);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(cs, db);
    }

    // PostgreSQL truncates identifiers at 63 bytes; keep room for a per-writer suffix so the two
    // [Theory] runs of the same test method do not collide on the database name.
    private static string TrimmedDbName(string dbName, HistoryWriter writer)
    {
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var trimmed = dbName.Length > 58 ? dbName[..58] : dbName;
        return trimmed + suffix;
    }

    private static PolicyContext NewContext(string connectionString, HistoryWriter writer)
    {
        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(connectionString)
            .EnableServiceProviderCaching(false)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;
        return new PolicyContext(options);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> CurrentSessionUserAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select session_user";
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<HistoryRow>> ReadDbSessionUserAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select db_session_user, operation
            from policies_history
            order by valid_from, history_id
            """;

        var rows = new List<HistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(reader.GetString(0), reader.GetInt16(1)));
        }

        return rows;
    }

    private sealed record HistoryRow(string DbSessionUser, short Operation);

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
        public string Status { get; set; } = "Draft";
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
            policy.Property(p => p.Status).HasColumnName("status");
            policy.Property(p => p.Premium).HasColumnName("premium").HasPrecision(18, 4);
            policy.IsTemporal(t => t.WithDbSessionUser());
        }
    }

    // A second, separate entity/context so its history table never has the column at all — proves the
    // opt-in is per entity, not a global switch (DESIGN.md D16).
    private sealed class NoOptInPolicyContext(DbContextOptions<NoOptInPolicyContext> options) : DbContext(options)
    {
        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");
            policy.Property(p => p.Status).HasColumnName("status");
            policy.Property(p => p.Premium).HasColumnName("premium").HasPrecision(18, 4);
            policy.IsTemporal();
        }
    }
}
