using Microsoft.EntityFrameworkCore;

namespace Hindsight.IntegrationTests;

/// <summary>
/// Columns whose model CLR type reaches PostgreSQL through the provider's own type mapping rather than a
/// converter the user configured: an Npgsql <see cref="LTree"/> (<c>ltree</c>, from the <c>ltree</c>
/// extension) and an enum with no <c>HasConversion</c> (<c>integer</c>). The history column mirrors them
/// with the same store type, both writers record them, and <c>AllVersions</c> reads them back.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProviderMappedColumnTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Ltree_and_plain_enum_columns_are_versioned(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync($"provider_mapped_columns_{writer}".ToLowerInvariant(), Ct);
        var options = new DbContextOptionsBuilder<EntryContext>()
            .UseNpgsql(cs)
            .EnableServiceProviderCaching(false)
            .UseHindsight(h => h.UseHistoryWriter(writer))
            .Options;

        await using (var setup = new EntryContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using (var db = new EntryContext(options))
        {
            var entry = new Entry { Id = 1, Path = new LTree("root.a"), Kind = EntryKind.Leaf };
            db.Entries.Add(entry);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = new EntryContext(options))
        {
            var entry = await db.Entries.SingleAsync(Ct);
            entry.Path = new LTree("root.b.a");
            entry.Kind = EntryKind.Branch;
            await db.SaveChangesAsync(Ct);
        }

        await using var read = new EntryContext(options);
        var versions = await read.Entries.AllVersions()
            .OrderBy(e => e.Kind)
            .Select(e => new { Path = (string)e.Path, e.Kind })
            .ToListAsync(Ct);

        Assert.Equal(
            [new { Path = "root.a", Kind = EntryKind.Leaf }, new { Path = "root.b.a", Kind = EntryKind.Branch }],
            versions);
    }

    private enum EntryKind
    {
        Leaf = 1,
        Branch = 2,
    }

    private sealed class Entry
    {
        public int Id { get; set; }

        public LTree Path { get; set; }

        public EntryKind Kind { get; set; }
    }

    private sealed class EntryContext(DbContextOptions<EntryContext> options) : DbContext(options)
    {
        public DbSet<Entry> Entries => Set<Entry>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasPostgresExtension("ltree");

            var entry = modelBuilder.Entity<Entry>();
            entry.ToTable("entries");
            entry.Property(e => e.Id).ValueGeneratedNever();
            entry.IsTemporal();
        }
    }
}
