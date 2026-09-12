using Microsoft.EntityFrameworkCore;

namespace Hindsight.Tests;

/// <summary>
/// Covers <see cref="Infrastructure.HindsightOptionsExtension.Validate"/>: Hindsight is Npgsql-only
/// (README.md, Non-goals), so a context configured with any other relational provider must fail early
/// and clearly instead of hitting a confusing DI or SQL-generation error later.
/// </summary>
public sealed class HindsightOptionsExtensionTests
{
    [Fact]
    public void Model_access_on_a_Sqlite_context_throws_InvalidOperationException_naming_Npgsql()
    {
        using var db = new SqliteContext();

        var ex = Assert.Throws<InvalidOperationException>(() => db.Model);

        Assert.Contains("Npgsql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Microsoft.EntityFrameworkCore.Sqlite", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_access_on_an_Npgsql_context_does_not_throw()
    {
        using var db = new NpgsqlContext();

        // Model access is what triggers Validate() (it builds the context's internal service
        // provider) - this must not throw for the one provider Hindsight actually supports.
        var model = db.Model;

        Assert.NotNull(model);
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private sealed class SqliteContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite("Data Source=:memory:").UseHindsight();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Policy>().IsTemporal();
    }

    private sealed class NpgsqlContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseNpgsql("Host=localhost;Database=unused").UseHindsight();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Policy>().IsTemporal();
    }
}
