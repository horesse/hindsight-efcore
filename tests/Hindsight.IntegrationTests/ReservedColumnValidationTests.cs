using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// A source property mapped to a column name Hindsight reserves for a fixed history column
/// (<c>HistoryEntityTypeConvention.ValidateReservedColumnNames</c>) must be rejected while the model is
/// still being built — the same point <c>dotnet ef migrations add</c> builds it at — in both writer
/// modes, rather than surfacing later as a raw PostgreSQL error the first time a row is written
/// (docs/articles/configuration.md → Model validation).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReservedColumnValidationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public void Building_the_design_time_model_throws_before_any_database_is_touched(HistoryWriter writer)
    {
        // No CreateDatabaseAsync, no connection ever opened: accessing DbContext.Model is what triggers
        // the model-finalizing conventions (the same ones `dotnet ef migrations add` runs to build its
        // design-time model), so a throw here proves the collision is caught at that point rather than
        // later against a real database.
        var options = new DbContextOptionsBuilder<ReasonCollisionContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;
        using var db = new ReasonCollisionContext(options);

        void BuildModel()
        {
            _ = db.Model;
        }

        var ex = Assert.Throws<InvalidOperationException>(BuildModel);

        Assert.Contains("reason", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task EnsureCreated_throws_the_same_way_and_creates_no_table(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(EnsureCreated_throws_the_same_way_and_creates_no_table) + "_" + writer, Ct);
        var options = new DbContextOptionsBuilder<ReasonCollisionContext>()
            .UseNpgsql(cs)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;
        await using var db = new ReasonCollisionContext(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.EnsureCreatedAsync(Ct));

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select to_regclass('reason_collision_entities')::text";
        var mainTable = await cmd.ExecuteScalarAsync(Ct);
        Assert.True(mainTable is null or DBNull, "the main table must not be created either: the whole model build failed");
    }

    private sealed class ReasonCollisionEntity
    {
        public int Id { get; set; }
        public string Reason { get; set; } = "";
    }

    private sealed class ReasonCollisionContext(DbContextOptions<ReasonCollisionContext> options) : DbContext(options)
    {
        public DbSet<ReasonCollisionEntity> Entities => Set<ReasonCollisionEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<ReasonCollisionEntity>();
            entity.ToTable("reason_collision_entities");
            entity.Property(p => p.Id).HasColumnName("id");
            // Realistic collision: a domain property that happens to be named/mapped like Hindsight's
            // own fixed change-context column (CLAUDE.md task example: SupportTicket.Reason).
            entity.Property(p => p.Reason).HasColumnName("reason");
            entity.IsTemporal();
        }
    }
}
