using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hindsight.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class HistoryTableCreationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task History_table_columns_have_the_same_store_type_as_the_main_table()
    {
        var (cs, _) = await CreateSchemaAsync(nameof(History_table_columns_have_the_same_store_type_as_the_main_table));

        var main = await ReadColumnsAsync(cs, "policies");
        var history = await ReadColumnsAsync(cs, "policies_history");

        foreach (var (column, main_) in main)
        {
            if (column == "updated_at")
            {
                continue; // excluded from versioning
            }

            Assert.True(history.TryGetValue(column, out var history_), $"history is missing column '{column}'");
            Assert.Equal(main_.Type, history_.Type);
            // History drops NOT NULL (DESIGN.md D5): every mirrored column is nullable regardless of the source.
            Assert.True(history_.Nullable, $"history column '{column}' should be nullable");
        }
    }

    [Fact]
    public async Task History_table_has_the_period_and_change_context_columns()
    {
        var (cs, _) = await CreateSchemaAsync(nameof(History_table_has_the_period_and_change_context_columns));

        var history = await ReadColumnsAsync(cs, "policies_history");

        Assert.Equal(("timestamp with time zone", false), (history["valid_from"].Type, history["valid_from"].Nullable));
        Assert.Equal(("timestamp with time zone", false), (history["valid_to"].Type, history["valid_to"].Nullable));
        Assert.Equal(("smallint", false), (history["operation"].Type, history["operation"].Nullable));
        Assert.Equal(("text", true), (history["changed_by"].Type, history["changed_by"].Nullable));
        Assert.Equal(("text", true), (history["changed_by_name"].Type, history["changed_by_name"].Nullable));
        Assert.Equal(("text", true), (history["correlation_id"].Type, history["correlation_id"].Nullable));
        Assert.Equal(("text", true), (history["reason"].Type, history["reason"].Nullable));
        Assert.Equal(("jsonb", true), (history["extra"].Type, history["extra"].Nullable));
    }

    [Fact]
    public async Task History_id_is_an_always_generated_identity_primary_key()
    {
        var (cs, _) = await CreateSchemaAsync(nameof(History_id_is_an_always_generated_identity_primary_key));

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var identity = await ScalarAsync(conn,
            """
            select is_identity || '/' || coalesce(identity_generation, '')
            from information_schema.columns
            where table_name = 'policies_history' and column_name = 'history_id'
            """);
        Assert.Equal("YES/ALWAYS", identity);

        var pkColumns = await ScalarAsync(conn,
            """
            select string_agg(a.attname, ',' order by a.attnum)
            from pg_index i
            join pg_attribute a on a.attrelid = i.indrelid and a.attnum = any(i.indkey)
            where i.indrelid = 'policies_history'::regclass and i.indisprimary
            """);
        Assert.Equal("history_id", pkColumns);
    }

    [Fact]
    public async Task History_table_has_a_version_index_on_key_columns_and_period_start()
    {
        var (cs, _) = await CreateSchemaAsync(nameof(History_table_has_a_version_index_on_key_columns_and_period_start));

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var indexDef = await ScalarAsync(conn,
            """
            select indexdef from pg_indexes
            where tablename = 'policies_history' and indexname = 'ix_policies_history_version'
            """);

        Assert.Contains("(id, valid_from DESC)", indexDef);
    }

    [Fact]
    public async Task History_table_is_not_created_without_UseHindsight()
    {
        var cs = await postgres.CreateDatabaseAsync(
            nameof(History_table_is_not_created_without_UseHindsight), TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<PolicyContext>().UseNpgsql(cs).Options;
        await using var db = new PolicyContext(options, useHindsight: false);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var exists = await ScalarAsync(conn, "select to_regclass('policies_history')::text");

        Assert.Equal(string.Empty, exists);
    }

    private async Task<(string ConnectionString, PolicyContext Context)> CreateSchemaAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await postgres.CreateDatabaseAsync(name, ct);
        var options = new DbContextOptionsBuilder<PolicyContext>().UseNpgsql(cs).Options;
        var db = new PolicyContext(options, useHindsight: true);
        await db.Database.EnsureCreatedAsync(ct);
        return (cs, db);
    }

    private static async Task<Dictionary<string, (string Type, bool Nullable)>> ReadColumnsAsync(
        string connectionString, string table)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select column_name,
                   case
                       when data_type = 'ARRAY' then udt_name
                       when data_type = 'numeric' then 'numeric(' || numeric_precision || ',' || numeric_scale || ')'
                       when character_maximum_length is not null then data_type || '(' || character_maximum_length || ')'
                       else data_type
                   end,
                   is_nullable
            from information_schema.columns
            where table_schema = 'public' and table_name = @t
            """;
        cmd.Parameters.AddWithValue("t", table);

        var result = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            result[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2) == "YES");
        }

        return result;
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? string.Empty : value.ToString()!;
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

    private sealed class PolicyContext(DbContextOptions<PolicyContext> options, bool useHindsight) : DbContext(options)
    {
        private readonly bool _useHindsight = useHindsight;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (_useHindsight)
            {
                optionsBuilder.UseHindsight();
            }
        }

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
        }
    }
}
