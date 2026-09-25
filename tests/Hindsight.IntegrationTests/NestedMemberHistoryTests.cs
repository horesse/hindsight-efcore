using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// DESIGN.md D9 on a real database: a temporal entity with table-split complex properties (nested and
/// optional) and owned references (nested) is versioned column by column. Every write scenario runs in
/// both writer modes; the read side is checked for <c>AsOf</c>, <c>AllVersions</c>, <c>FromTo</c> and
/// <c>History&lt;T&gt;</c>. Instants for point-in-time reads are taken from the history itself, so the
/// tests assert intervals and reconstructed state, never wall-clock time.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NestedMemberHistoryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task History_table_mirrors_every_nested_column_nullable_with_the_main_table_type()
    {
        await using var h = await CreateAsync(HistoryWriter.Interceptor, nameof(History_table_mirrors_every_nested_column_nullable_with_the_main_table_type));

        var main = await ReadColumnsAsync(h.ConnectionString, "policies");
        var history = await ReadColumnsAsync(h.ConnectionString, "policies_history");

        string[] nested =
        [
            "address_street", "address_city", "address_geo_lat", "billing_iban", "billing_day",
            "holder_name", "holder_contact_phone",
        ];
        foreach (var column in nested)
        {
            Assert.True(history.TryGetValue(column, out var mirrored), $"history is missing column '{column}'");
            Assert.Equal(main[column].Type, mirrored.Type);
            Assert.True(mirrored.Nullable, $"history column '{column}' should be nullable");
        }

        Assert.Equal("numeric(9,6)", history["address_geo_lat"].Type);
        Assert.Equal(1, history.Keys.Count(c => c == "id"));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Insert_writes_every_nested_value(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Insert_writes_every_nested_value));

        h.Db.Policies.Add(NewPolicy());
        await h.Db.SaveChangesAsync(Ct);

        var only = Assert.Single(await ReadHistoryAsync(h.ConnectionString));
        Assert.Equal((short)1, only.Operation);
        Assert.Equal(("Main St", "Minsk", 53.9m), (only.Street, only.City, only.Lat));
        Assert.Null(only.Iban);
        Assert.Equal(("Ann", "+375"), (only.HolderName, only.Phone));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Changing_only_a_complex_member_writes_one_contiguous_new_version(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Changing_only_a_complex_member_writes_one_contiguous_new_version));
        var policy = await AddAsync(h);

        policy.Address.City = "Paris";
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        AssertChain(versions, 1, 2);
        Assert.Equal(["Minsk", "Paris"], versions.Select(v => v.City));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Changing_only_a_nested_complex_member_writes_one_new_version(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Changing_only_a_nested_complex_member_writes_one_new_version));
        var policy = await AddAsync(h);

        policy.Address.Geo.Lat = 48.856613m;
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        AssertChain(versions, 1, 2);
        Assert.Equal(48.856613m, versions[1].Lat);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Setting_and_clearing_an_optional_complex_property_writes_a_version_each_time(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Setting_and_clearing_an_optional_complex_property_writes_a_version_each_time));
        var policy = await AddAsync(h);

        policy.Billing = new Billing { Iban = "BY00", Day = 5 };
        await h.Db.SaveChangesAsync(Ct);
        policy.Billing = null;
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        AssertChain(versions, 1, 2, 2);
        Assert.Equal([null, "BY00", null], versions.Select(v => v.Iban));
        Assert.Equal([null, 5, null], versions.Select(v => v.Day));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Replacing_a_complex_property_with_an_equal_value_writes_no_version(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Replacing_a_complex_property_with_an_equal_value_writes_no_version));
        var policy = await AddAsync(h);

        policy.Address = new Address { Street = "Main St", City = "Minsk", Geo = new Geo { Lat = 53.9m } };
        await h.Db.SaveChangesAsync(Ct);

        AssertChain(await ReadHistoryAsync(h.ConnectionString), 1);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Changing_only_an_owned_member_writes_one_new_version_of_the_owner(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Changing_only_an_owned_member_writes_one_new_version_of_the_owner));
        var policy = await AddAsync(h);

        policy.Holder!.Name = "Bob";
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        AssertChain(versions, 1, 2);
        Assert.Equal(["Ann", "Bob"], versions.Select(v => v.HolderName));
        Assert.Equal("Minsk", versions[1].City);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Changing_only_a_nested_owned_member_writes_one_new_version(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Changing_only_a_nested_owned_member_writes_one_new_version));
        var policy = await AddAsync(h);

        policy.Holder!.Contact!.Phone = "+33";
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        AssertChain(versions, 1, 2);
        Assert.Equal(["+375", "+33"], versions.Select(v => v.Phone));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Replacing_an_owned_reference_writes_exactly_one_version(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Replacing_an_owned_reference_writes_exactly_one_version));
        var policy = await AddAsync(h);

        policy.Holder = new Holder { Name = "Carl", Contact = new Contact { Phone = "+49" } };
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        AssertChain(versions, 1, 2);
        Assert.Equal(("Carl", "+49"), (versions[1].HolderName, versions[1].Phone));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Clearing_an_owned_reference_writes_a_version_with_its_columns_null(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Clearing_an_owned_reference_writes_a_version_with_its_columns_null));
        var policy = await AddAsync(h);

        policy.Holder = null;
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        AssertChain(versions, 1, 2);
        Assert.Equal((null, null), (versions[1].HolderName, versions[1].Phone));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Changing_the_owner_and_its_owned_member_together_writes_one_version(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Changing_the_owner_and_its_owned_member_together_writes_one_version));
        var policy = await AddAsync(h);

        policy.Number = "ACME-2";
        policy.Holder!.Name = "Bob";
        await h.Db.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        AssertChain(versions, 1, 2);
        Assert.Equal(("ACME-2", "Bob"), (versions[1].Number, versions[1].HolderName));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Delete_by_id_without_loading_writes_the_real_nested_values_on_the_tombstone(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Delete_by_id_without_loading_writes_the_real_nested_values_on_the_tombstone));
        var policy = await AddAsync(h);

        await using var deleter = NewContext(h.ConnectionString, writer);
        deleter.Policies.Remove(new Policy { Id = policy.Id });
        await deleter.SaveChangesAsync(Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        var tombstone = versions[^1];
        Assert.Equal((short)3, tombstone.Operation);
        Assert.Equal(tombstone.ValidFrom, tombstone.ValidTo);
        Assert.Equal(("Minsk", 53.9m, "Ann", "+375"), (tombstone.City, tombstone.Lat, tombstone.HolderName, tombstone.Phone));
    }

    [Fact]
    public async Task Raw_sql_update_of_a_nested_column_is_versioned_by_the_trigger()
    {
        await using var h = await CreateAsync(HistoryWriter.Trigger, nameof(Raw_sql_update_of_a_nested_column_is_versioned_by_the_trigger));
        var policy = await AddAsync(h);

        await h.Db.Database.ExecuteSqlAsync($"update policies set holder_contact_phone = '+1' where id = {policy.Id}", Ct);

        var versions = await ReadHistoryAsync(h.ConnectionString);
        AssertChain(versions, 1, 2);
        Assert.Equal("+1", versions[1].Phone);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task AsOf_reconstructs_complex_and_owned_members_at_each_version(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(AsOf_reconstructs_complex_and_owned_members_at_each_version));
        await SeedFourVersionsAsync(h);
        var instants = await VersionStartsAsync(h);

        var v1 = await h.Db.Policies.AsOf(instants[0]).SingleAsync(Ct);
        Assert.Equal(("Main St", "Minsk", 53.9m), (v1.Address.Street, v1.Address.City, v1.Address.Geo.Lat));
        Assert.Null(v1.Billing);
        Assert.Equal(("Ann", "+375"), (v1.Holder!.Name, v1.Holder.Contact!.Phone));

        var v2 = await h.Db.Policies.AsOf(instants[1]).SingleAsync(Ct);
        Assert.Equal("Paris", v2.Address.City);
        Assert.Equal(("BY00", 5), (v2.Billing!.Iban, v2.Billing.Day));

        var v3 = await h.Db.Policies.AsOf(instants[2]).SingleAsync(Ct);
        Assert.Equal("Bob", v3.Holder!.Name);
        Assert.Null(v3.Holder.Contact);

        var v4 = await h.Db.Policies.AsOf(instants[3]).SingleAsync(Ct);
        Assert.Null(v4.Holder);
        Assert.Equal("Paris", v4.Address.City);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task AllVersions_FromTo_and_History_reconstruct_nested_members(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(AllVersions_FromTo_and_History_reconstruct_nested_members));
        await SeedFourVersionsAsync(h);
        var instants = await VersionStartsAsync(h);

        var all = await h.Db.Policies.AllVersions().ToListAsync(Ct);
        Assert.Equal(["Paris", "Paris", "Paris", "Minsk"], all.Select(p => p.Address.City));
        Assert.Equal([null, "Bob", "Ann", "Ann"], all.Select(p => p.Holder?.Name));

        var window = await h.Db.Policies.FromTo(instants[1], instants[2]).ToListAsync(Ct);
        Assert.Equal("BY00", Assert.Single(window).Billing!.Iban);

        var history = await h.Db.History<Policy>().ToListAsync(Ct);
        Assert.Equal(4, history.Count);
        Assert.Equal("+375", history[^1].Entity.Holder!.Contact!.Phone);
        Assert.Null(history[0].Entity.Holder);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Owned_reference_without_a_required_property_is_null_only_when_every_column_is_null(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Owned_reference_without_a_required_property_is_null_only_when_every_column_is_null));
        var policy = await AddAsync(h);
        policy.Marketing = new Marketing { Score = 7 };
        await h.Db.SaveChangesAsync(Ct);
        var instants = await VersionStartsAsync(h);

        Assert.Null((await h.Db.Policies.AsOf(instants[0]).SingleAsync(Ct)).Marketing);
        var present = (await h.Db.Policies.AsOf(instants[1]).SingleAsync(Ct)).Marketing;
        Assert.Equal((null, 7), (present!.Channel, present.Score));

        // Documented limitation (docs/configuration/nested-members.md): EF cannot translate a filter
        // through the all-columns presence check, and says so instead of returning a wrong answer.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Db.Policies.AllVersions().Where(p => p.Marketing!.Score == 7).ToListAsync(Ct));
    }

    [Fact]
    public async Task Where_and_OrderBy_on_nested_members_compose_into_one_history_query()
    {
        await using var h = await CreateAsync(HistoryWriter.Interceptor, nameof(Where_and_OrderBy_on_nested_members_compose_into_one_history_query));
        await SeedFourVersionsAsync(h);
        h.Sql.Commands.Clear();

        var cities = await h.Db.Policies
            .AllVersions()
            .Where(p => p.Holder!.Name == "Bob" || p.Address.City == "Minsk")
            .OrderBy(p => p.Address.Geo.Lat)
            .Select(p => p.Address.City)
            .ToListAsync(Ct);

        Assert.Equal(["Minsk", "Paris"], cities.Order());
        Assert.Single(h.Sql.Commands);
    }

    [Fact]
    public async Task AsOf_with_nested_members_generates_the_expected_sql()
    {
        await using var h = await CreateAsync(HistoryWriter.Interceptor, nameof(AsOf_with_nested_members_generates_the_expected_sql));

        var sql = h.Db.Policies.AsOf(DateTimeOffset.UnixEpoch).ToQueryString();

        await Verify(sql, extension: "sql");
    }

    [Fact]
    public async Task Saving_a_reattached_snapshot_after_changing_only_its_owned_member_throws()
    {
        await using var h = await CreateAsync(HistoryWriter.Interceptor, nameof(Saving_a_reattached_snapshot_after_changing_only_its_owned_member_throws));
        await SeedFourVersionsAsync(h);
        var instants = await VersionStartsAsync(h);
        h.Db.ChangeTracker.Clear();

        var snapshot = await h.Db.Policies.AsOf(instants[0]).SingleAsync(Ct);
        h.Db.Attach(snapshot);
        snapshot.Holder!.Name = "Mallory";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Db.SaveChangesAsync(Ct));
        Assert.Contains("read-only", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_reports_nested_members_by_path(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_reports_nested_members_by_path));
        await SeedFourVersionsAsync(h);

        var history = await h.Db.History<Policy>().ToListAsync(Ct); // newest first
        var first = h.Db.Diff(history[3], history[2]);
        var last = h.Db.Diff(history[1], history[0]);

        Assert.Equal(["Address.City", "Billing.Day", "Billing.Iban"], first.Select(c => c.Path));
        Assert.Equal(("Minsk", "Paris"), (first[0].OldValue, first[0].NewValue));
        Assert.Equal(["Holder.Name"], last.Select(c => c.Path));
        Assert.Equal(("Bob", null), (last[0].OldValue, last[0].NewValue));
    }

    // v1 insert; v2 Address.City + Billing set; v3 Holder.Name changed, Contact cleared; v4 Holder cleared.
    private static async Task SeedFourVersionsAsync(Harness h)
    {
        var policy = await AddAsync(h);

        policy.Address.City = "Paris";
        policy.Billing = new Billing { Iban = "BY00", Day = 5 };
        await h.Db.SaveChangesAsync(Ct);

        policy.Holder!.Name = "Bob";
        policy.Holder.Contact = null;
        await h.Db.SaveChangesAsync(Ct);

        policy.Holder = null;
        await h.Db.SaveChangesAsync(Ct);

        h.Time.Advance();
    }

    private static async Task<Policy> AddAsync(Harness h)
    {
        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);
        return policy;
    }

    private static async Task<List<DateTimeOffset>> VersionStartsAsync(Harness h)
        => (await h.Db.History<Policy>().Select(v => v.ValidFrom).ToListAsync(Ct)).Order().ToList();

    private static void AssertChain(List<HistoryRow> versions, params short[] operations)
    {
        Assert.Equal(operations, versions.Select(v => v.Operation));
        for (var i = 1; i < versions.Count; i++)
        {
            Assert.Equal(versions[i - 1].ValidTo, versions[i].ValidFrom); // contiguous
            Assert.True(versions[i - 1].ValidTo > versions[i - 1].ValidFrom); // strictly positive
        }

        Assert.True(versions[^1].IsOpen);
        Assert.Single(versions, v => v.IsOpen);
    }

    private static Policy NewPolicy() => new()
    {
        Number = "ACME-1",
        Address = new Address { Street = "Main St", City = "Minsk", Geo = new Geo { Lat = 53.9m } },
        Holder = new Holder { Name = "Ann", Contact = new Contact { Phone = "+375" } },
    };

    private async Task<Harness> CreateAsync(HistoryWriter writer, string dbName)
    {
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var trimmed = dbName.Length > 58 ? dbName[..58] : dbName;
        var cs = await postgres.CreateDatabaseAsync(trimmed + suffix, Ct);
        var time = new SteppingTimeProvider();
        var sql = new SqlCapture();
        var db = NewContext(cs, writer, time, sql);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(cs, db, time, sql);
    }

    private static PolicyContext NewContext(
        string connectionString, HistoryWriter writer, SteppingTimeProvider? time = null, SqlCapture? sql = null)
    {
        var builder = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(connectionString)
            .EnableServiceProviderCaching(false)
            .UseApplicationServiceProvider(new SingleServiceProvider(typeof(TimeProvider), time ?? new SteppingTimeProvider()))
            .UseHindsight(hb => hb.UseHistoryWriter(writer));
        if (sql is not null)
        {
            builder.AddInterceptors(sql);
        }

        return new PolicyContext(builder.Options);
    }

    private static async Task<List<HistoryRow>> ReadHistoryAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select number, address_street, address_city, address_geo_lat, billing_iban, billing_day,
                   holder_name, holder_contact_phone, valid_from, valid_to, operation
            from policies_history
            order by valid_from, history_id
            """;

        var rows = new List<HistoryRow>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new HistoryRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetFieldValue<DateTime>(8),
                reader.GetFieldValue<DateTime>(9),
                reader.GetInt16(10)));
        }

        return rows;
    }

    private static async Task<Dictionary<string, (string Type, bool Nullable)>> ReadColumnsAsync(string connectionString, string table)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select a.attname, format_type(a.atttypid, a.atttypmod), not a.attnotnull
            from pg_attribute a
            where a.attrelid = @t::regclass and a.attnum > 0 and not a.attisdropped
            """;
        cmd.Parameters.AddWithValue("t", table);

        var columns = new Dictionary<string, (string, bool)>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            columns[reader.GetString(0)] = (reader.GetString(1), reader.GetBoolean(2));
        }

        return columns;
    }

    private sealed record HistoryRow(
        string Number,
        string? Street,
        string? City,
        decimal? Lat,
        string? Iban,
        int? Day,
        string? HolderName,
        string? Phone,
        DateTime ValidFrom,
        DateTime ValidTo,
        short Operation)
    {
        public bool IsOpen => ValidTo == DateTime.MaxValue;
    }

    private sealed class Harness(string connectionString, PolicyContext db, SteppingTimeProvider time, SqlCapture sql)
        : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public PolicyContext Db { get; } = db;

        public SteppingTimeProvider Time { get; } = time;

        public SqlCapture Sql { get; } = sql;

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    // Interceptor mode reads one instant per SaveChanges from TimeProvider; every read moves it an hour
    // forward, so consecutive saves get distinct, increasing timestamps without waiting.
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            Advance();
            return _now;
        }

        public void Advance() => _now = _now.AddHours(1);
    }

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type type) => type == serviceType ? instance : null;
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public Address Address { get; set; } = new();
        public Billing? Billing { get; set; }
        public Holder? Holder { get; set; }
        public Marketing? Marketing { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class Address
    {
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
        public Geo Geo { get; set; } = new();
    }

    private sealed class Geo
    {
        public decimal Lat { get; set; }
    }

    private sealed class Billing
    {
        public string Iban { get; set; } = "";
        public int? Day { get; set; }
    }

    private sealed class Holder
    {
        public string Name { get; set; } = "";
        public Contact? Contact { get; set; }
    }

    private sealed class Contact
    {
        public string Phone { get; set; } = "";
    }

    // No required property: its presence can only be read from all of its columns together.
    private sealed class Marketing
    {
        public string? Channel { get; set; }
        public int? Score { get; set; }
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
            policy.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            policy.ComplexProperty(p => p.Address, a =>
            {
                a.Property(x => x.Street).HasColumnName("address_street");
                a.Property(x => x.City).HasColumnName("address_city");
                a.ComplexProperty(x => x.Geo, g => g.Property(x => x.Lat).HasColumnName("address_geo_lat").HasPrecision(9, 6));
            });
            policy.ComplexProperty(p => p.Billing, b =>
            {
                b.Property(x => x.Iban).HasColumnName("billing_iban");
                b.Property(x => x.Day).HasColumnName("billing_day");
            });
            policy.OwnsOne(p => p.Holder, o =>
            {
                o.Property(x => x.Name).HasColumnName("holder_name");
                o.OwnsOne(x => x.Contact, c => c.Property(x => x.Phone).HasColumnName("holder_contact_phone"));
            });
            policy.OwnsOne(p => p.Marketing, o =>
            {
                o.Property(x => x.Channel).HasColumnName("marketing_channel");
                o.Property(x => x.Score).HasColumnName("marketing_score");
            });
            policy.IsTemporal(t => t.Exclude(p => p.UpdatedAt));
        }
    }
}
