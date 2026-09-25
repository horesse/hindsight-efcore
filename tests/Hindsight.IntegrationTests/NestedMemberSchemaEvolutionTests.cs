using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// DESIGN.md D6 for the members of complex properties and owned references (D9), on real PostgreSQL: a
/// member added to a complex type, and an owned reference added to an existing temporal entity, become
/// new history columns; a member removed from a complex type stays on the history table as a nullable
/// orphan with its old values. No generated operation drops or alters a history column, and both writers
/// keep writing the new column set afterwards.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NestedMemberSchemaEvolutionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Adding_and_removing_nested_members_adds_history_columns_and_orphans_the_removed_one(HistoryWriter writer)
    {
        var cs = await postgres.CreateDatabaseAsync(
            "nested_evolution" + (writer == HistoryWriter.Trigger ? "_trg" : "_int"), Ct);

        // v1: Address { Street, Legacy }, no Holder.
        await using var v1 = new EvolutionContext(cs, writer, version: 1);
        await v1.Database.EnsureCreatedAsync(Ct);
        v1.Policies.Add(new Policy { Number = "ACME-1", Address = new Address { Street = "Main St", Legacy = "old" } });
        await v1.SaveChangesAsync(Ct);
        var v1Model = v1.GetService<IDesignTimeModel>().Model;

        // v2: Address.Legacy removed, Address.Zip added, owned Holder added.
        await using var v2 = new EvolutionContext(cs, writer, version: 2, snapshotModel: v1Model);
        var v2Model = v2.GetService<IDesignTimeModel>().Model;

        var operations = v2.GetService<IMigrationsModelDiffer>().GetDifferences(
            v1Model.GetRelationalModel(), v2Model.GetRelationalModel());

        // The main table's own operations are EF's business (its differ may even pair address_legacy with
        // holder_name as a rename); the history table only ever gains columns.
        foreach (var added in new[] { "address_zip", "holder_name" })
        {
            Assert.Contains(operations.OfType<AddColumnOperation>(), op => op.Table == "policies_history" && op.Name == added);
        }

        Assert.DoesNotContain(operations.OfType<DropColumnOperation>(), op => op.Table == "policies_history");
        Assert.DoesNotContain(operations.OfType<RenameColumnOperation>(), op => op.Table == "policies_history");
        Assert.DoesNotContain(operations, op => op is DropTableOperation or AlterColumnOperation);

        foreach (var command in v2.GetService<IMigrationsSqlGenerator>().Generate(operations, v2Model))
        {
            await v2.Database.ExecuteSqlRawAsync(command.CommandText, Ct);
        }

        Assert.Equal("YES", await ScalarAsync(cs,
            "select is_nullable from information_schema.columns where table_name = 'policies_history' and column_name = 'address_legacy'"));
        Assert.Equal("old", await ScalarAsync(cs, "select address_legacy from policies_history"));

        // Both writers pick up the new columns (Trigger mode: the function was recreated after AddColumn).
        var policy = await v2.Policies.SingleAsync(Ct);
        policy.Address.Zip = "220000";
        policy.Holder = new Holder { Name = "Ann" };
        await v2.SaveChangesAsync(Ct);

        Assert.Equal("220000|Ann|", await ScalarAsync(cs,
            "select concat_ws('|', address_zip, holder_name, coalesce(address_legacy, '')) from policies_history where valid_to = 'infinity'"));
        Assert.Equal("2", await ScalarAsync(cs, "select count(*) from policies_history"));
    }

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? null : value.ToString();
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public Address Address { get; set; } = new();
        public Holder? Holder { get; set; }
    }

    private sealed class Address
    {
        public string Street { get; set; } = "";
        public string? Legacy { get; set; }
        public string? Zip { get; set; }
    }

    private sealed class Holder
    {
        public string Name { get; set; } = "";
    }

    private sealed class EvolutionContext(string connectionString, HistoryWriter writer, int version, IModel? snapshotModel = null)
        : DbContext
    {
        public DbSet<Policy> Policies => Set<Policy>();

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseNpgsql(connectionString)
                .UseHindsight(h => h.UseHistoryWriter(writer))
                .EnableServiceProviderCaching(false);
            if (snapshotModel is not null)
            {
                options.ReplaceService<IMigrationsAssembly, StubMigrationsAssembly>();
                StubMigrationsAssembly.Snapshot.Value = new StubModelSnapshot(snapshotModel);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.ToTable("policies");
            policy.Property(p => p.Id).HasColumnName("id");
            policy.Property(p => p.Number).HasColumnName("number");
            policy.ComplexProperty(p => p.Address, a =>
            {
                a.Property(x => x.Street).HasColumnName("address_street");
                if (version == 1)
                {
                    a.Property(x => x.Legacy).HasColumnName("address_legacy");
                    a.Ignore(x => x.Zip);
                }
                else
                {
                    a.Ignore(x => x.Legacy);
                    a.Property(x => x.Zip).HasColumnName("address_zip");
                }
            });

            if (version == 1)
            {
                policy.Ignore(p => p.Holder);
            }
            else
            {
                policy.OwnsOne(p => p.Holder, o => o.Property(x => x.Name).HasColumnName("holder_name"));
            }

            policy.IsTemporal();
        }
    }

    private sealed class StubMigrationsAssembly : IMigrationsAssembly
    {
        public static readonly AsyncLocal<StubModelSnapshot?> Snapshot = new();

        public IReadOnlyDictionary<string, TypeInfo> Migrations { get; } = new Dictionary<string, TypeInfo>();

        public ModelSnapshot? ModelSnapshot => Snapshot.Value;

        public Assembly Assembly => typeof(StubMigrationsAssembly).Assembly;

        public string? FindMigrationId(string nameOrId) => null;

        public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
            => throw new NotSupportedException();
    }

    private sealed class StubModelSnapshot(IModel model) : ModelSnapshot
    {
        public override IModel Model { get; } = model;

        protected override void BuildModel(ModelBuilder modelBuilder)
        {
        }
    }
}
