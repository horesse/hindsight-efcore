using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// PostgreSQL truncates any identifier over 63 bytes (<c>NAMEDATALEN - 1</c>) silently, with no error.
/// Before <c>HistoryEntityTypeConvention.ValidateIdentifierLengths</c> existed, two temporal entities
/// whose generated history table/function/trigger/index names differed only after that many bytes
/// collided on the same physical PostgreSQL object once their migrations were applied: <c>CREATE
/// TABLE</c>/<c>CREATE INDEX</c> failed loudly ("relation ... already exists"), but <c>CREATE OR
/// REPLACE FUNCTION</c> did not — it silently replaced the first entity's trigger function body with
/// the second's, so the first entity's own trigger (unaffected, since triggers are scoped per table)
/// went on calling the wrong function on every future write. These tests pin down that the collision
/// is now rejected while the model is still being built — the same point <c>dotnet ef migrations add</c>
/// builds it at, and well before any table or function reaches PostgreSQL — for both writer modes
/// (docs/articles/configuration.md → Model validation).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class IdentifierLengthValidationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public void Building_the_design_time_model_throws_before_any_database_is_touched(HistoryWriter writer)
    {
        // No CreateDatabaseAsync, no connection ever opened: accessing DbContext.Model triggers the
        // model-finalizing conventions (the same ones `dotnet ef migrations add` runs), so a throw here
        // proves the check runs at that point rather than later against a real database.
        var options = new DbContextOptionsBuilder<CollidingEntitiesContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;
        using var db = new CollidingEntitiesContext(options);

        void BuildModel()
        {
            _ = db.Model;
        }

        var ex = Assert.Throws<InvalidOperationException>(BuildModel);

        Assert.Contains("63-byte identifier limit", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task EnsureCreated_throws_the_same_way_and_creates_neither_entitys_table(HistoryWriter writer)
    {
        // A short literal label, not nameof(this test method): PostgreSQL's 63-byte identifier limit —
        // the very thing under test — applies to database names too, and the method name plus a writer
        // suffix would collide with itself across the two [Theory] cases (CLAUDE.md: integration tests
        // never share a database).
        var cs = await postgres.CreateDatabaseAsync("identifier_length_collision_" + writer, Ct);
        var options = new DbContextOptionsBuilder<CollidingEntitiesContext>()
            .UseNpgsql(cs)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;
        await using var db = new CollidingEntitiesContext(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.EnsureCreatedAsync(Ct));

        // Before this validation existed, this is exactly the point where the two entities' migrations
        // would have reached PostgreSQL and collided (CREATE OR REPLACE FUNCTION silently overwriting
        // one entity's trigger body with the other's — confirmed against real PostgreSQL while
        // investigating this bug). Now neither table exists at all: the whole model build failed first.
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select to_regclass('{CollidingEntitiesContext.TableA}')::text, "
            + $"to_regclass('{CollidingEntitiesContext.TableB}')::text";
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.True(await reader.IsDBNullAsync(0, Ct), "entity A's table must not be created");
        Assert.True(await reader.IsDBNullAsync(1, Ct), "entity B's table must not be created either");
    }

    private sealed class EntityA
    {
        public int Id { get; set; }
    }

    private sealed class EntityB
    {
        public int Id { get; set; }
    }

    private sealed class CollidingEntitiesContext(DbContextOptions<CollidingEntitiesContext> options) : DbContext(options)
    {
        // Identical for the first 60 characters, diverging only at character 61 ('b' vs 'c'). Each
        // name is 61 bytes on its own, so each independently exceeds the 63-byte limit once Hindsight
        // adds "ix_" + "_history_version" (19 bytes) for the version index — the tightest of the
        // generated identifiers. Before the fix, this divergence point (byte 60) fell *before* the
        // truncation cutoff for the plain table/function/trigger names (so those stayed distinct) but
        // *after* it once the version index's 3-byte "ix_" prefix shifted the cutoff — so the two
        // entities' version indexes truncated to the exact same 63-byte name.
        public const string TableA = "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqb";
        public const string TableB = "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqc";

        public DbSet<EntityA> As => Set<EntityA>();

        public DbSet<EntityB> Bs => Set<EntityB>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EntityA>(e =>
            {
                e.ToTable(TableA);
                e.IsTemporal();
            });
            modelBuilder.Entity<EntityB>(e =>
            {
                e.ToTable(TableB);
                e.IsTemporal();
            });
        }
    }
}
