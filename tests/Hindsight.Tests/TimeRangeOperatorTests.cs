using Microsoft.EntityFrameworkCore;

namespace Hindsight.Tests;

/// <summary>
/// What <c>FromTo</c> / <c>ContainedIn</c> do before a database is involved (DESIGN.md D17): argument
/// validation at the call, the <c>tstzrange</c> function mapping on the model, and the translated SQL
/// (<c>ToQueryString</c> needs no connection). Behaviour against real rows is in
/// <c>Hindsight.IntegrationTests.TimeRangeQueryTests</c>.
/// </summary>
public sealed class TimeRangeOperatorTests
{
    private static readonly DateTimeOffset _from = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _to = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromTo_with_from_after_to_throws_ArgumentOutOfRange_at_the_call()
    {
        using var db = new TestContext();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => db.Policies.FromTo(_to, _from));

        Assert.Equal("from", ex.ParamName);
    }

    [Fact]
    public void ContainedIn_with_from_after_to_throws_ArgumentOutOfRange_at_the_call()
    {
        using var db = new TestContext();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => db.Policies.ContainedIn(_to, _from));

        Assert.Equal("from", ex.ParamName);
    }

    [Fact]
    public void FromTo_and_ContainedIn_accept_an_empty_window()
    {
        using var db = new TestContext();

        Assert.NotNull(db.Policies.FromTo(_from, _from));
        Assert.NotNull(db.Policies.ContainedIn(_from, _from));
    }

    [Fact]
    public void FromTo_and_ContainedIn_reject_a_null_source()
    {
        IQueryable<Policy> source = null!;

        Assert.Throws<ArgumentNullException>(() => source.FromTo(_from, _to));
        Assert.Throws<ArgumentNullException>(() => source.ContainedIn(_from, _to));
    }

    [Fact]
    public void UseHindsight_maps_the_builtin_tstzrange_function()
    {
        using var db = new TestContext();

        var function = Assert.Single(db.Model.GetDbFunctions(), f => f.Name == "tstzrange");

        Assert.True(function.IsBuiltIn);
    }

    [Fact]
    public void The_tstzrange_mapping_adds_no_ddl_beyond_the_period_range_index()
    {
        using var db = new TestContext();

        var line = Assert.Single(
            db.Database.GenerateCreateScript().Split('\n'),
            line => line.Contains("tstzrange", StringComparison.Ordinal));

        Assert.StartsWith("CREATE INDEX ix_policies_history_period", line.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void FromTo_translates_to_a_range_overlap_with_both_bounds_as_parameters()
    {
        using var db = new TestContext();

        var sql = db.Policies.FromTo(_from, _to).ToQueryString();

        Assert.Contains(
            "p.operation <> 3 AND tstzrange(p.valid_from, p.valid_to) && tstzrange(@FromUtc, @ToUtc)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("ORDER BY p.valid_from DESC", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainedIn_translates_to_range_containment_with_both_bounds_as_parameters()
    {
        using var db = new TestContext();

        var sql = db.Policies.ContainedIn(_from, _to).ToQueryString();

        Assert.Contains(
            "p.operation <> 3 AND tstzrange(p.valid_from, p.valid_to) <@ tstzrange(@FromUtc, @ToUtc)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("ORDER BY p.valid_from DESC", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_bounds_are_passed_to_PostgreSQL_in_UTC()
    {
        using var db = new TestContext();
        var plusThree = TimeSpan.FromHours(3);

        var sql = db.Policies.FromTo(_from.ToOffset(plusThree), _to.ToOffset(plusThree)).ToQueryString();

        Assert.Contains("@FromUtc='2026-07-01T00:00:00.0000000Z'", sql, StringComparison.Ordinal);
        Assert.Contains("@ToUtc='2026-10-01T00:00:00.0000000Z'", sql, StringComparison.Ordinal);
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private sealed class TestContext : DbContext
    {
        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options
                .UseNpgsql("Host=localhost;Database=unused")
                .EnableServiceProviderCaching(false)
                .UseHindsight();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");
            policy.IsTemporal();
        }
    }
}
