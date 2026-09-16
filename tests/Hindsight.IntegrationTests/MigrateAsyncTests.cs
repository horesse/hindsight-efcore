using InsuranceSample;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// <c>Database.MigrateAsync()</c> against a brand-new database — the one
/// migration path no other suite in this project exercises, since every other test uses
/// <c>EnsureCreatedAsync</c> (a single <c>Generate()</c> call that never reaches
/// <c>Migrator</c>'s own history-table bootstrap). Regression coverage for the bug where
/// <c>HistoryEntityTypeConvention</c> mistook that bootstrap's throwaway single-entity model for every
/// temporal entity having just been de-temporalized (DESIGN.md D6) and cloned each history table's
/// <c>CreateTableOperation</c> into it, racing the real migration for the same table.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MigrateAsyncTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task MigrateAsync_against_a_fresh_database_creates_the_main_and_history_tables(HistoryWriter writer)
    {
        var cs = await CreateFreshDatabaseAsync(
            nameof(MigrateAsync_against_a_fresh_database_creates_the_main_and_history_tables), writer);

        await using (var db = NewContext(cs, writer))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);

        Assert.Equal("policies", await ScalarAsync(conn, "select to_regclass('policies')::text"));
        Assert.Equal("policies_history", await ScalarAsync(conn, "select to_regclass('policies_history')::text"));
        Assert.Equal("1", await ScalarAsync(conn, "select count(*)::text from \"__EFMigrationsHistory\""));
        Assert.Equal(
            "20260916204232_Initial",
            await ScalarAsync(conn, "select \"MigrationId\" from \"__EFMigrationsHistory\""));
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task MigrateAsync_run_again_against_an_already_migrated_database_stays_a_no_op(HistoryWriter writer)
    {
        var cs = await CreateFreshDatabaseAsync(
            nameof(MigrateAsync_run_again_against_an_already_migrated_database_stays_a_no_op), writer);

        await using (var db = NewContext(cs, writer))
        {
            await db.Database.MigrateAsync(Ct);
        }

        // The scenario the history-table bootstrap exists to speed up: a second process (or a restarted
        // host) calling MigrateAsync() against a database that is already fully migrated. Must stay a
        // genuine no-op — not re-attempt "Initial" and fail the same way against the still-live tables.
        await using (var db = NewContext(cs, writer))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        Assert.Equal("1", await ScalarAsync(conn, "select count(*)::text from \"__EFMigrationsHistory\""));
    }

    private async Task<string> CreateFreshDatabaseAsync(string name, HistoryWriter writer)
    {
        // PostgreSQL truncates identifiers at 63 bytes; keep room for a per-writer suffix so the two
        // [Theory] runs do not collide on the database name (same trick as TriggerHistoryWriterTests).
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var trimmed = name.Length > 58 ? name[..58] : name;
        return await postgres.CreateDatabaseAsync(trimmed + suffix, Ct);
    }

    private static InsuranceDbContext NewContext(string connectionString, HistoryWriter writer)
    {
        var options = new DbContextOptionsBuilder<InsuranceDbContext>()
            .UseNpgsql(connectionString)
            .EnableServiceProviderCaching(false)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;
        return new InsuranceDbContext(options);
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? string.Empty : value.ToString()!;
    }
}
