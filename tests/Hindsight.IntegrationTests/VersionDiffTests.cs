using Microsoft.EntityFrameworkCore;

namespace Hindsight.IntegrationTests;

/// <summary>
/// <c>Diff</c> over versions both history writers actually stored and <c>History&lt;T&gt;()</c> read back
/// from real PostgreSQL: values that round-trip through <c>text[]</c>, <c>jsonb</c>, <c>bytea</c> and an
/// enum-as-string conversion must compare equal when unchanged, and every edge case of the version chain
/// (first version, tombstone, re-insert, the Interceptor-only no-op version) behaves as documented in
/// <c>docs/querying/diff.md</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VersionDiffTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_of_consecutive_versions_lists_exactly_the_properties_that_changed(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_of_consecutive_versions_lists_exactly_the_properties_that_changed));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        policy.Status = PolicyStatus.Active;
        policy.Premium = 250.50m;
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await OldestFirstAsync(h.Db);
        var changes = h.Db.Diff(versions[0], versions[1]);

        Assert.Equal(["Premium", "Status"], changes.Select(c => c.Property.Name));
        Assert.Equal(100m, changes[0].OldValue);
        Assert.Equal(250.50m, changes[0].NewValue);
        Assert.Equal(PolicyStatus.Draft, changes[1].OldValue);
        Assert.Equal(PolicyStatus.Active, changes[1].NewValue);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_does_not_report_unchanged_array_jsonb_and_bytea_values_as_changed(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_does_not_report_unchanged_array_jsonb_and_bytea_values_as_changed));

        var policy = NewPolicy();
        policy.Tags = ["gold", "renewal"];
        policy.Metadata = """{"broker":"kb"}""";
        policy.Document = [0x25, 0x50, 0x44, 0x46];
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        policy.Number = "ACME-2";
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        // Each version is materialized separately: equal content, distinct array instances.
        var versions = await OldestFirstAsync(h.Db);

        Assert.Equal("Number", Assert.Single(h.Db.Diff(versions[0], versions[1])).Property.Name);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_reports_a_change_inside_an_array_column(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_reports_a_change_inside_an_array_column));

        var policy = NewPolicy();
        policy.Tags = ["gold"];
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        policy.Tags = ["gold", "renewal"];
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await OldestFirstAsync(h.Db);
        var change = Assert.Single(h.Db.Diff(versions[0], versions[1]));

        Assert.Equal("Tags", change.Property.Name);
        Assert.Equal(["gold"], (string[])change.OldValue!);
        Assert.Equal(["gold", "renewal"], (string[])change.NewValue!);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_of_the_insert_version_against_null_lists_every_versioned_property(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_of_the_insert_version_against_null_lists_every_versioned_property));

        h.Db.Policies.Add(NewPolicy());
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var insert = Assert.Single(await OldestFirstAsync(h.Db));
        var changes = h.Db.Diff(null, insert);

        Assert.Equal(VersionOperation.Insert, insert.Operation);
        Assert.Equal(["Id", "Document", "Metadata", "Number", "Premium", "Status", "Tags"], changes.Select(c => c.Property.Name));
        Assert.All(changes, c => Assert.Null(c.OldValue));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_ignores_a_property_excluded_from_history(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_ignores_a_property_excluded_from_history));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        policy.Premium = 120m;
        policy.UpdatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await OldestFirstAsync(h.Db);

        Assert.Equal("Premium", Assert.Single(h.Db.Diff(versions[0], versions[1])).Property.Name);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_with_the_delete_tombstone_throws_while_the_versions_before_it_still_diff(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_with_the_delete_tombstone_throws_while_the_versions_before_it_still_diff));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        policy.Status = PolicyStatus.Cancelled;
        await h.Db.SaveChangesAsync(Ct);

        h.Db.Policies.Remove(policy);
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await OldestFirstAsync(h.Db); // [insert, update, tombstone]

        Assert.Equal(VersionOperation.Delete, versions[2].Operation);
        var ex = Assert.Throws<ArgumentException>(() => h.Db.Diff(versions[1], versions[2]));
        Assert.Equal("newer", ex.ParamName);
        Assert.Equal("Status", Assert.Single(h.Db.Diff(versions[0], versions[1])).Property.Name);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_across_a_delete_compares_the_last_state_with_the_re_inserted_one(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_across_a_delete_compares_the_last_state_with_the_re_inserted_one));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);
        var id = policy.Id;

        h.Db.Policies.Remove(policy);
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var again = NewPolicy();
        again.Id = id;
        again.Premium = 90m;
        h.Db.Policies.Add(again);
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await OldestFirstAsync(h.Db); // [insert, tombstone, insert]

        Assert.Equal([VersionOperation.Insert, VersionOperation.Delete, VersionOperation.Insert], versions.Select(v => v.Operation));
        Assert.Equal("Premium", Assert.Single(h.Db.Diff(versions[0], versions[2])).Property.Name);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_of_versions_of_two_different_policies_throws(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_of_versions_of_two_different_policies_throws));

        h.Db.Policies.Add(NewPolicy("ACME-1"));
        await h.Db.SaveChangesAsync(Ct);
        h.Db.Policies.Add(NewPolicy("ACME-2"));
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await OldestFirstAsync(h.Db);

        Assert.Throws<ArgumentException>(() => h.Db.Diff(versions[0], versions[1]));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_with_History_order_passed_as_is_throws_because_it_is_newest_first(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_with_History_order_passed_as_is_throws_because_it_is_newest_first));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);
        policy.Premium = 120m;
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var newestFirst = await h.Db.History<Policy>().ToListAsync(Ct);

        var ex = Assert.Throws<ArgumentException>(() => h.Db.Diff(newestFirst[0], newestFirst[1]));
        Assert.Equal("older", ex.ParamName);
    }

    [Fact]
    public async Task Diff_of_the_no_op_version_the_Interceptor_writer_records_is_empty()
    {
        await using var h = await CreateAsync(HistoryWriter.Interceptor, nameof(Diff_of_the_no_op_version_the_Interceptor_writer_records_is_empty));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        h.Db.Entry(policy).Property(p => p.Status).IsModified = true; // same value, marked modified
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var versions = await OldestFirstAsync(h.Db);

        Assert.Equal([VersionOperation.Insert, VersionOperation.Update], versions.Select(v => v.Operation));
        Assert.Empty(h.Db.Diff(versions[0], versions[1]));
    }

    [Fact]
    public async Task The_Trigger_writer_records_no_version_for_a_no_op_update_so_there_is_nothing_to_diff()
    {
        await using var h = await CreateAsync(HistoryWriter.Trigger, nameof(The_Trigger_writer_records_no_version_for_a_no_op_update_so_there_is_nothing_to_diff));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);

        h.Db.Entry(policy).Property(p => p.Status).IsModified = true;
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        Assert.Equal(VersionOperation.Insert, Assert.Single(await OldestFirstAsync(h.Db)).Operation);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Diff_of_an_AllVersions_snapshot_against_the_current_entity_reports_what_changed_since(HistoryWriter writer)
    {
        await using var h = await CreateAsync(writer, nameof(Diff_of_an_AllVersions_snapshot_against_the_current_entity_reports_what_changed_since));

        var policy = NewPolicy();
        h.Db.Policies.Add(policy);
        await h.Db.SaveChangesAsync(Ct);
        policy.Premium = 120m;
        await h.Db.SaveChangesAsync(Ct);
        policy.Status = PolicyStatus.Active;
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ChangeTracker.Clear();

        var first = await h.Db.Policies.AllVersions().OrderBy(p => p.Premium).FirstAsync(Ct);
        var current = await h.Db.Policies.SingleAsync(Ct);

        Assert.Equal(["Premium", "Status"], h.Db.Diff(first, current).Select(c => c.Property.Name));
    }

    // ---------------------------------------------------------------------------------------------

    private static Policy NewPolicy(string number = "ACME-1")
        => new() { Number = number, Status = PolicyStatus.Draft, Premium = 100m };

    private static async Task<List<Version<Policy>>> OldestFirstAsync(PolicyContext db)
    {
        var newestFirst = await db.History<Policy>().ToListAsync(Ct);
        newestFirst.Reverse();
        return newestFirst;
    }

    private async Task<Harness> CreateAsync(HistoryWriter writer, string dbName)
    {
        // PostgreSQL truncates identifiers at 63 bytes; keep room for a per-writer suffix so the two
        // [Theory] runs do not collide on the database name.
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var trimmed = dbName.Length > 58 ? dbName[..58] : dbName;
        var cs = await postgres.CreateDatabaseAsync(trimmed + suffix, Ct);

        var options = new DbContextOptionsBuilder<PolicyContext>()
            .UseNpgsql(cs)
            .EnableServiceProviderCaching(false)
            .UseHindsight(hb => hb.UseHistoryWriter(writer))
            .Options;

        var db = new PolicyContext(options);
        await db.Database.EnsureCreatedAsync(Ct);
        return new Harness(db);
    }

    private sealed class Harness(PolicyContext db) : IAsyncDisposable
    {
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
        public byte[] Document { get; set; } = [];
        public DateTimeOffset UpdatedAt { get; set; }
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
            policy.Property(p => p.Tags).HasColumnName("tags");
            policy.Property(p => p.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            policy.Property(p => p.Document).HasColumnName("document");
            policy.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            policy.IsTemporal(t => t.Exclude(p => p.UpdatedAt));
        }
    }
}
