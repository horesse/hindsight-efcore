using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Hindsight.IntegrationTests.Spikes;

// SPIKE — DESIGN.md → Open questions:
//   "Property-bag + HasConversion<string>() enums: does the history column get the same store type?"
//   plus D2 "Revisit if": jsonb, arrays, custom ValueConverters.
//
// Question: when the history table is a property-bag entity type
// (SharedTypeEntity<Dictionary<string, object>>), what does it take for each history column to end
// up with the SAME store type and value converter as the corresponding main-table column?
//
// Two strategies, both building the history entity from OnModelCreating (stand-in for the real
// model-finalizing convention):
//   Naive  -> IndexerProperty(sourceProperty.ClrType, columnName) and nothing else.
//   Mirror -> also copy the source property's GetColumnType() / GetValueConverter() /
//             GetProviderClrType() / GetMaxLength() / IsUnicode() / GetPrecision() / GetScale().
//
// Finding (see DESIGN.md D2):
//   * Naive loses fidelity: enum HasConversion<string>() -> integer, jsonb -> text,
//     numeric(18,4) -> numeric, varchar(8) -> text. Arrays (text[]) survive because Npgsql infers
//     them from the CLR type.
//   * Mirror reproduces every case exactly, in both the EF model and the PostgreSQL catalog,
//     using only public property facets.
//
// NOTE on timing: the copied getters only return the configured values once the source property is
// fully configured. Reading them mid-configuration (before the source entity's own fluent calls have
// all run) yields nulls. The real convention must therefore run at/after model finalization.
//
// Ugly by design. Promote the Mirror copy into the real convention, then delete this file.
[Collection(PostgresCollection.Name)]
public sealed class PropertyBagTypeFidelitySpike(PostgresFixture postgres)
{
    public enum SpikeStatus
    {
        Draft,
        Active,
        Cancelled,
    }

    public enum HistoryStrategy
    {
        Naive,
        Mirror,
    }

    internal const string MainTable = "spike_policies";
    internal const string HistoryTable = "spike_policies_history";

    // Columns whose store type the naive strategy is expected to get wrong.
    internal static readonly string[] FragileColumns = ["Status", "Payload", "Premium", "Code"];

    [Theory]
    [InlineData(HistoryStrategy.Naive)]
    [InlineData(HistoryStrategy.Mirror)]
    public async Task Property_bag_history_columns_versus_main_table(HistoryStrategy strategy)
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await postgres.CreateDatabaseAsync($"pb_fidelity_{strategy}", ct);
        var options = new DbContextOptionsBuilder().UseNpgsql(cs).Options;

        // Distinct context type per strategy: EF caches the built model keyed by context CLR type,
        // so a shared context type would hand every strategy whichever model was built first.
        await using DbContext db = strategy == HistoryStrategy.Naive
            ? new NaiveContext(options)
            : new MirrorContext(options);
        await db.Database.EnsureCreatedAsync(ct);

        var mainEntity = db.Model.FindEntityType(typeof(SpikePolicy))!;
        var historyEntity = db.Model.FindEntityType(HistoryTable)!;

        var modelByColumn = new Dictionary<string, (string Main, string History)>(StringComparer.Ordinal);
        foreach (var source in mainEntity.GetProperties())
        {
            var column = source.GetColumnName()!;
            var target = historyEntity.GetProperties().Single(p => p.GetColumnName() == column);
            modelByColumn[column] = (DescribeModel(source), DescribeModel(target));
        }

        var mainCatalog = await ReadColumnsAsync(cs, MainTable, ct);
        var historyCatalog = await ReadColumnsAsync(cs, HistoryTable, ct);

        var report = BuildReport(strategy, modelByColumn, mainCatalog, historyCatalog);

        var modelDiverging = modelByColumn
            .Where(kv => kv.Value.Main != kv.Value.History)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        var catalogDiverging = mainCatalog
            .Where(kv => historyCatalog.TryGetValue(kv.Key, out var h) && h != kv.Value)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (strategy == HistoryStrategy.Mirror)
        {
            Assert.True(modelDiverging.Count == 0 && catalogDiverging.Count == 0,
                "mirroring source facets should give full store-type parity\n" + report);
        }
        else
        {
            // The naive strategy is expected to get exactly the fragile columns wrong (both in the
            // model and the catalog) and everything else right.
            Assert.True(
                modelDiverging.SetEquals(FragileColumns) && catalogDiverging.SetEquals(FragileColumns),
                "naive strategy divergence did not match the documented finding\n" + report);
        }
    }

    private static string DescribeModel(IReadOnlyProperty property)
    {
        var mapping = property.GetRelationalTypeMapping();
        var converter = property.GetValueConverter() ?? mapping.Converter;
        return $"{mapping.StoreType}|{(converter is null ? "none" : converter.ProviderClrType.Name)}";
    }

    private static string BuildReport(
        HistoryStrategy strategy,
        Dictionary<string, (string Main, string History)> model,
        Dictionary<string, string> mainCatalog,
        Dictionary<string, string> historyCatalog)
    {
        var lines = new List<string> { $"strategy={strategy}", "MODEL (storeType|converter->providerClrType):" };
        lines.AddRange(model.Select(kv =>
            $"  {kv.Key,-12} main=[{kv.Value.Main}] history=[{kv.Value.History}]" +
            (kv.Value.Main == kv.Value.History ? "" : "   <-- DIVERGES")));
        lines.Add("CATALOG (data_type|udt_name|char_len|num_precision|num_scale):");
        lines.AddRange(mainCatalog
            .Where(kv => historyCatalog.ContainsKey(kv.Key))
            .Select(kv =>
                $"  {kv.Key,-12} main=[{kv.Value}] history=[{historyCatalog[kv.Key]}]" +
                (kv.Value == historyCatalog[kv.Key] ? "" : "   <-- DIVERGES")));
        return string.Join('\n', lines);
    }

    private static async Task<Dictionary<string, string>> ReadColumnsAsync(
        string connectionString, string table, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            select column_name, data_type, udt_name,
                   coalesce(character_maximum_length, -1) as len,
                   coalesce(numeric_precision, -1)        as prec,
                   coalesce(numeric_scale, -1)            as scale
            from information_schema.columns
            where table_schema = 'public' and table_name = @t
            order by column_name
            """;
        cmd.Parameters.AddWithValue("t", table);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = string.Join(
                '|',
                reader.GetString(1),
                reader.GetString(2),
                Convert.ToInt32(reader.GetValue(3)),
                Convert.ToInt32(reader.GetValue(4)),
                Convert.ToInt32(reader.GetValue(5)));
        }

        return result;
    }

    private sealed class SpikePolicy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public SpikeStatus Status { get; set; }
        public string[] Tags { get; set; } = [];
        public string Payload { get; set; } = "{}";
        public decimal Premium { get; set; }
        public string Code { get; set; } = "";
        public Uri? Website { get; set; }
    }

    private sealed class NaiveContext(DbContextOptions options) : SpikeContext(options, HistoryStrategy.Naive);

    private sealed class MirrorContext(DbContextOptions options) : SpikeContext(options, HistoryStrategy.Mirror);

    private abstract class SpikeContext(DbContextOptions options, HistoryStrategy strategy) : DbContext(options)
    {
        private readonly HistoryStrategy _strategy = strategy;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var entity = modelBuilder.Entity<SpikePolicy>();
            entity.ToTable(MainTable);
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Status).HasConversion<string>();          // enum -> text
            entity.Property(p => p.Tags);                                    // string[] -> text[] (Npgsql, by CLR type)
            entity.Property(p => p.Payload).HasColumnType("jsonb");          // explicit store type
            entity.Property(p => p.Premium).HasPrecision(18, 4);            // numeric(18,4)
            entity.Property(p => p.Code).HasMaxLength(8);                    // varchar(8)
            entity.Property(p => p.Website).HasConversion(                   // custom ValueConverter
                v => v == null ? null : v.ToString(),
                v => v == null ? null : new Uri(v));

            BuildHistory(modelBuilder, entity.Metadata, mirror: _strategy == HistoryStrategy.Mirror);
        }

        // Stand-in for the real model-finalizing convention (D2): build the property-bag history entity.
        private static void BuildHistory(ModelBuilder modelBuilder, IMutableEntityType source, bool mirror)
        {
            var history = modelBuilder.SharedTypeEntity<Dictionary<string, object>>(HistoryTable);
            history.ToTable(HistoryTable);
            history.HasNoKey();

            foreach (var property in source.GetProperties())
            {
                var column = property.GetColumnName()!;
                var builder = history.IndexerProperty(property.ClrType, column);
                builder.HasColumnName(column);

                if (!mirror)
                {
                    continue;
                }

                if (property.GetColumnType() is { } storeType)
                {
                    builder.HasColumnType(storeType);
                }

                if (property.GetValueConverter() is { } converter)
                {
                    builder.HasConversion(converter);
                }
                else if (property.GetProviderClrType() is { } providerClrType)
                {
                    builder.HasConversion(providerClrType);
                }

                if (property.GetMaxLength() is { } maxLength)
                {
                    builder.HasMaxLength(maxLength);
                }

                if (property.IsUnicode() is { } unicode)
                {
                    builder.IsUnicode(unicode);
                }

                if (property.GetPrecision() is { } precision)
                {
                    builder.HasPrecision(precision, property.GetScale() ?? 0);
                }
            }
        }
    }
}
