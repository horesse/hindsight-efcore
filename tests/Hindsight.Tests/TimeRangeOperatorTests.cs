using Microsoft.EntityFrameworkCore;

namespace Hindsight.Tests;

/// <summary>
/// What <c>FromTo</c> / <c>ContainedIn</c> do before a database is involved (DESIGN.md D18): argument
/// validation at the call and the <c>tstzrange</c> function mapping on the model. The translated SQL is
/// pinned by the Verify snapshots, and behaviour against real rows, in
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
