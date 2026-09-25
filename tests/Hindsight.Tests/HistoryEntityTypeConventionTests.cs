using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight.Tests;

public sealed class HistoryEntityTypeConventionTests
{
    [Fact]
    public void UseHindsight_adds_a_history_entity_type_mapped_to_the_history_table()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var history = model.HistoryEntityType(typeof(Policy));

        Assert.Equal("policies_history", history.GetTableName());
        Assert.True(history.IsPropertyBag);
        Assert.True(history[HindsightAnnotationNames.IsHistoryTable] is true);
    }

    [Fact]
    public void History_entity_type_mirrors_entity_columns_and_skips_excluded_ones()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.Exclude(p => p.UpdatedAt)));

        var columns = model.HistoryEntityType(typeof(Policy)).GetProperties()
            .Select(p => p.GetColumnName())
            .ToList();

        Assert.Contains("Number", columns);
        Assert.Contains("Status", columns);
        Assert.DoesNotContain("UpdatedAt", columns);
    }

    [Fact]
    public void History_entity_type_has_the_period_and_context_columns()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var columns = model.HistoryEntityType(typeof(Policy)).GetProperties()
            .ToDictionary(p => p.GetColumnName()!, p => p);

        Assert.False(columns["valid_from"].IsNullable);
        Assert.False(columns["valid_to"].IsNullable);
        Assert.False(columns["operation"].IsNullable);
        Assert.True(columns["changed_by"].IsNullable);
        Assert.True(columns["changed_by_name"].IsNullable);
        Assert.True(columns["correlation_id"].IsNullable);
        Assert.True(columns["reason"].IsNullable);
        Assert.Equal("jsonb", columns["extra"].GetColumnType());
        Assert.DoesNotContain("db_session_user", columns.Keys); // opt-in only (DESIGN.md D16)
    }

    [Fact]
    public void WithDbSessionUser_adds_a_not_null_column_defaulting_to_session_user()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.WithDbSessionUser()));

        var dbSessionUser = model.HistoryEntityType(typeof(Policy)).GetProperty("db_session_user");

        Assert.False(dbSessionUser.IsNullable);
        Assert.Equal("session_user", dbSessionUser.GetDefaultValueSql());
    }

    [Fact]
    public void Entity_type_annotation_is_set_only_when_WithDbSessionUser_is_called()
    {
        var withoutOptIn = BuildModel(b => b.Entity<Policy>().IsTemporal());
        var withOptIn = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.WithDbSessionUser()));

        Assert.Null(withoutOptIn.FindEntityType(typeof(Policy))![HindsightAnnotationNames.HasDbSessionUser]);
        Assert.True(withOptIn.FindEntityType(typeof(Policy))![HindsightAnnotationNames.HasDbSessionUser] is true);
    }

    [Fact]
    public void History_entity_type_has_a_surrogate_key_on_history_id()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var history = model.HistoryEntityType(typeof(Policy));
        var key = history.FindPrimaryKey();

        Assert.NotNull(key);
        Assert.Equal("history_id", Assert.Single(key.Properties).GetColumnName());
        Assert.Equal(ValueGenerated.OnAdd, history.GetProperty("history_id").ValueGenerated);
    }

    [Fact]
    public void History_entity_type_has_a_version_index_on_key_columns_and_period_start()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal());

        var index = Assert.Single(model.HistoryEntityType(typeof(Policy)).GetIndexes());

        Assert.Equal(["Id", "valid_from"], index.Properties.Select(p => p.GetColumnName()));
        Assert.Equal([false, true], index.IsDescending!);
    }

    [Fact]
    public void Custom_history_table_name_and_schema_are_honored()
    {
        var model = BuildModel(b => b.Entity<Policy>().IsTemporal(t => t
            .UseHistoryTable("policy_versions", schema: "audit")));

        var history = model.HistoryEntityType(typeof(Policy));

        Assert.Equal("policy_versions", history.GetTableName());
        Assert.Equal("audit", history.GetSchema());
    }

    [Fact]
    public void Temporal_entity_without_a_primary_key_throws_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildModel(b => b.Entity<Keyless>().HasNoKey().ToTable("keyless").IsTemporal()));

        Assert.Contains("has no primary key", ex.Message);
    }

    [Fact]
    public void Temporal_tph_hierarchy_is_rejected()
    {
        Assert.Throws<NotSupportedException>(() => BuildModel(b =>
        {
            b.Entity<Policy>().IsTemporal();
            b.Entity<MotorPolicy>();
        }));
    }

    [Fact]
    public void History_entity_type_mirrors_the_columns_of_complex_properties_including_nested_ones()
    {
        var model = BuildModel(b => b.Entity<PolicyWithMembers>(e =>
        {
            e.ComplexProperty(p => p.Address, a => a.ComplexProperty(x => x.Geo));
            e.ComplexProperty(p => p.Premium);
            e.Ignore(p => p.Holder);
            e.IsTemporal();
        }));

        var history = model.HistoryEntityType(typeof(PolicyWithMembers));
        var columns = history.GetProperties().Select(p => p.GetColumnName()).ToList();

        Assert.Contains("Address_Street", columns);
        Assert.Contains("Address_City", columns);
        Assert.Contains("Address_Geo_Lat", columns);
        Assert.Contains("Premium_Amount", columns);
        Assert.Contains("Premium_Currency", columns);
        Assert.Equal(typeof(decimal?), history.GetProperty("Address_Geo_Lat").ClrType);
        Assert.Equal("numeric(9,6)", history.GetProperty("Address_Geo_Lat").GetColumnType());
        Assert.True(history.GetProperty("Address_City").IsNullable);
    }

    [Fact]
    public void History_entity_type_mirrors_owned_reference_columns_without_repeating_the_shared_key()
    {
        var model = BuildModel(b => b.Entity<PolicyWithMembers>(e =>
        {
            e.Ignore(p => p.Address);
            e.Ignore(p => p.Premium);
            e.OwnsOne(p => p.Holder, o => o.OwnsOne(h => h.Contact));
            e.IsTemporal();
        }));

        var columns = model.HistoryEntityType(typeof(PolicyWithMembers)).GetProperties()
            .Select(p => p.GetColumnName())
            .ToList();

        Assert.Contains("Holder_Name", columns);
        Assert.Contains("Holder_Contact_Phone", columns);
        Assert.Single(columns, c => c == "Id");
    }

    [Fact]
    public void Temporal_entity_with_an_owned_collection_is_rejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => BuildModel(b => b.Entity<PolicyWithOwnedCollection>(e =>
        {
            e.OwnsMany(p => p.Addresses);
            e.IsTemporal();
        })));

        Assert.Contains("'PolicyWithOwnedCollection.Addresses' is an owned collection", ex.Message);
    }

    [Fact]
    public void Temporal_entity_with_a_json_mapped_owned_reference_is_rejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => BuildModel(b => b.Entity<PolicyWithMembers>(e =>
        {
            e.Ignore(p => p.Address);
            e.Ignore(p => p.Premium);
            e.OwnsOne(p => p.Holder, o => o.ToJson());
            e.IsTemporal();
        })));

        Assert.Contains("'PolicyWithMembers.Holder' is an owned reference mapped to JSON", ex.Message);
    }

    [Fact]
    public void Temporal_entity_with_a_json_mapped_complex_property_is_rejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => BuildModel(b => b.Entity<PolicyWithMembers>(e =>
        {
            e.ComplexProperty(p => p.Address, a => a.ToJson());
            e.Ignore(p => p.Premium);
            e.Ignore(p => p.Holder);
            e.IsTemporal();
        })));

        Assert.Contains("'PolicyWithMembers.Address' is a complex property mapped to JSON", ex.Message);
    }

    [Fact]
    public void Temporal_entity_with_a_complex_collection_is_rejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => BuildModel(b => b.Entity<PolicyWithOwnedCollection>(e =>
        {
            e.ComplexCollection(p => p.Addresses, a => a.ToJson());
            e.IsTemporal();
        })));

        Assert.Contains("'PolicyWithOwnedCollection.Addresses' is a complex collection", ex.Message);
    }

    [Fact]
    public void Temporal_entity_with_an_owned_reference_in_a_table_of_its_own_is_rejected()
    {
        var ex = Assert.Throws<NotSupportedException>(() => BuildModel(b => b.Entity<PolicyWithMembers>(e =>
        {
            e.Ignore(p => p.Address);
            e.Ignore(p => p.Premium);
            e.OwnsOne(p => p.Holder, o =>
            {
                o.ToTable("holders");
                o.Ignore(h => h.Contact);
            });
            e.IsTemporal();
        })));

        Assert.Contains("'PolicyWithMembers.Holder' is an owned reference mapped to a table of its own", ex.Message);
    }

    [Fact]
    public void Nested_member_mapped_to_a_reserved_history_column_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b => b.Entity<PolicyWithMembers>(e =>
        {
            e.ComplexProperty(p => p.Address, a => a.Property(x => x.City).HasColumnName("reason"));
            e.Ignore(p => p.Premium);
            e.Ignore(p => p.Holder);
            e.IsTemporal();
        })));

        Assert.Contains("'Address.City'", ex.Message);
        Assert.Contains("'reason'", ex.Message);
    }
    [Theory]
    [InlineData("history_id")]
    [InlineData("operation")]
    [InlineData("changed_by")]
    [InlineData("changed_by_name")]
    [InlineData("correlation_id")]
    [InlineData("reason")]
    [InlineData("extra")]
    [InlineData("db_session_user")]
    public void Property_column_colliding_with_a_fixed_history_column_throws_with_a_clear_message(string column)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b =>
        {
            var entity = b.Entity<ReservedColumnEntity>();
            entity.Property(p => p.Value).HasColumnName(column);
            entity.IsTemporal();
        }));

        Assert.Contains($"'{nameof(ReservedColumnEntity)}'", ex.Message);
        Assert.Contains(nameof(ReservedColumnEntity.Value), ex.Message);
        Assert.Contains($"'{column}'", ex.Message);
    }

    [Theory]
    [InlineData("valid_from")]
    [InlineData("valid_to")]
    public void Property_column_colliding_with_the_default_period_column_throws_with_a_clear_message(string column)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b =>
        {
            var entity = b.Entity<ReservedColumnEntity>();
            entity.Property(p => p.Value).HasColumnName(column);
            entity.IsTemporal();
        }));

        Assert.Contains("period", ex.Message);
    }

    [Fact]
    public void Property_column_colliding_with_a_custom_period_column_throws_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b =>
        {
            var entity = b.Entity<ReservedColumnEntity>();
            entity.Property(p => p.Value).HasColumnName("effective_at");
            entity.IsTemporal(t => t.HasPeriodStart("effective_at"));
        }));

        Assert.Contains("period", ex.Message);
    }

    [Fact]
    public void Temporal_entity_with_its_entire_primary_key_excluded_throws_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BuildModel(b => b.Entity<Policy>().IsTemporal(t => t.Exclude(p => p.Id))));

        Assert.Contains($"'{nameof(Policy)}'", ex.Message);
        Assert.Contains("primary key", ex.Message);
        Assert.Contains("Exclude(...)", ex.Message);
    }

    [Fact]
    public void Temporal_entity_with_a_composite_key_partially_excluded_does_not_throw()
    {
        var model = BuildModel(b => b.Entity<CompositeKeyPolicy>(e =>
        {
            e.HasKey(p => new { p.TenantId, p.Id });
            e.IsTemporal(t => t.Exclude(p => p.TenantId));
        }));

        var columns = model.HistoryEntityType(typeof(CompositeKeyPolicy)).GetProperties()
            .Select(p => p.GetColumnName())
            .ToList();

        // TenantId is excluded but Id still identifies the previous version, so this is allowed
        // (HistoryRowPlan's keyColumns already handles "some key columns present" correctly).
        Assert.Contains("Id", columns);
        Assert.DoesNotContain("TenantId", columns);
    }

    [Fact]
    public void Equal_period_start_and_end_column_names_throw_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b => b.Entity<Policy>().IsTemporal(t => t
            .HasPeriodStart("same")
            .HasPeriodEnd("same"))));

        Assert.Contains("period start and end columns", ex.Message);
    }

    [Fact]
    public void Excluded_property_colliding_with_a_fixed_history_column_does_not_throw()
    {
        var model = BuildModel(b =>
        {
            var entity = b.Entity<ReservedColumnEntity>();
            entity.Property(p => p.Value).HasColumnName("reason");
            entity.IsTemporal(t => t.Exclude(p => p.Value));
        });

        var columns = model.HistoryEntityType(typeof(ReservedColumnEntity)).GetProperties()
            .Select(p => p.GetColumnName())
            .ToList();

        // Only the fixed 'reason' context column exists; the excluded property never reached the
        // history table (MirrorEntityColumns skips excluded properties), so there was no real collision.
        Assert.Single(columns, c => c == "reason");
    }

    // PostgreSQL's NAMEDATALEN limit truncates any identifier over 63 bytes silently. The tightest of
    // Hindsight's generated identifiers is the version index name, "ix_<history_table>_version" — with
    // the default "_history" suffix that is "ix_" (3) + table (n) + "_history_version" (16) = n + 19
    // bytes, so a main table name of 44 ASCII bytes lands exactly on the limit and 45 is one byte over.
    [Fact]
    public void Main_table_name_at_the_63_byte_identifier_limit_does_not_throw()
    {
        var mainTable = new string('a', 44);

        var model = BuildModel(b => b.Entity<LongNameEntity>(e =>
        {
            e.ToTable(mainTable);
            e.IsTemporal();
        }));

        Assert.NotNull(model.HistoryEntityType(typeof(LongNameEntity)));
    }

    [Fact]
    public void Main_table_name_one_byte_over_the_63_byte_identifier_limit_throws_with_a_clear_message()
    {
        var mainTable = new string('a', 45);

        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b => b.Entity<LongNameEntity>(e =>
        {
            e.ToTable(mainTable);
            e.IsTemporal();
        })));

        Assert.Contains($"'{nameof(LongNameEntity)}'", ex.Message);
        Assert.Contains("version index name", ex.Message);
        Assert.Contains("64 bytes", ex.Message);
        Assert.Contains("UseHistoryTable(", ex.Message);
    }

    [Fact]
    public void UseHistoryTable_with_a_short_name_bypasses_a_main_table_name_over_the_limit()
    {
        // The main table name alone is 200 bytes — every identifier Hindsight would derive from it by
        // default is far over the limit — but UseHistoryTable gives the history table (and everything
        // derived from it) a short name instead, so validation must check the actual resolved history
        // table name, not the main table name.
        var mainTable = new string('a', 200);

        var model = BuildModel(b => b.Entity<LongNameEntity>(e =>
        {
            e.ToTable(mainTable);
            e.IsTemporal(t => t.UseHistoryTable("short_history"));
        }));

        Assert.Equal("short_history", model.HistoryEntityType(typeof(LongNameEntity)).GetTableName());
    }

    [Fact]
    public void NonAscii_table_name_under_63_characters_but_over_63_bytes_throws()
    {
        // 23 Cyrillic characters are 23 UTF-16 chars but 46 UTF-8 bytes (2 bytes each): the version
        // index name is then 19 + 23 = 42 characters (under 63) but 19 + 46 = 65 bytes (over 63) —
        // proof the check counts UTF-8 bytes and not System.String.Length.
        var mainTable = new string('а', 23);
        Assert.True(mainTable.Length < 63);

        var ex = Assert.Throws<InvalidOperationException>(() => BuildModel(b => b.Entity<LongNameEntity>(e =>
        {
            e.ToTable(mainTable);
            e.IsTemporal();
        })));

        Assert.Contains("65 bytes", ex.Message);
    }

    private static IModel BuildModel(Action<ModelBuilder> configure)
    {
        using var db = new TestContext(configure);
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Table("policies")]
    private class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public PolicyStatus Status { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    [Table("composite_key_policies")]
    private sealed class CompositeKeyPolicy
    {
        public int TenantId { get; set; }
        public int Id { get; set; }
        public string Number { get; set; } = "";
    }

    private sealed class MotorPolicy : Policy
    {
        public string PlateNumber { get; set; } = "";
    }

    private sealed class Keyless
    {
        public string Name { get; set; } = "";
    }

    [Table("reserved_column_entities")]
    private sealed class ReservedColumnEntity
    {
        public int Id { get; set; }
        public string Value { get; set; } = "";
    }

    // Table name set per-test via ToTable(...) — the 63-byte identifier limit tests need exact lengths.
    private sealed class LongNameEntity
    {
        public int Id { get; set; }
    }

    private enum PolicyStatus
    {
        Draft,
        Active,
    }

    [Table("policies")]
    private sealed class PolicyWithMembers
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public MemberAddress Address { get; set; } = new();
        public Money Premium { get; set; }
        public Holder? Holder { get; set; }
    }

    private sealed class MemberAddress
    {
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
        public Geo Geo { get; set; } = new();
    }

    private sealed class Geo
    {
        [Column(TypeName = "numeric(9,6)")]
        public decimal Lat { get; set; }
    }

    private sealed class Holder
    {
        public string Name { get; set; } = "";
        public Contact? Contact { get; set; }
    }

    private sealed class Contact
    {
        public string Phone { get; set; } = "";
    }

    [Table("policies")]
    private sealed class PolicyWithOwnedCollection
    {
        public int Id { get; set; }
        public List<Address> Addresses { get; } = [];
    }

    private sealed class Address
    {
        public string Street { get; set; } = "";
        public string City { get; set; } = "";
    }

    private readonly record struct Money(decimal Amount, string Currency);

    private sealed class TestContext(Action<ModelBuilder> configure) : DbContext
    {
        private readonly Action<ModelBuilder> _configure = configure;

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options
                .UseNpgsql("Host=localhost;Database=unused")
                .UseHindsight()
                .ReplaceService<IModelCacheKeyFactory, UniquePerContextModelCacheKeyFactory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => _configure(modelBuilder);
    }

    // Each context instance carries a different model configuration; never share a cached model.
    private sealed class UniquePerContextModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => Guid.NewGuid();
    }
}
