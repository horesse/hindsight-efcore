using Microsoft.EntityFrameworkCore;

namespace Hindsight.Tests;

/// <summary>
/// The comparison logic of <c>Diff</c> over hand-built snapshots: which properties count, how values
/// compare, and every argument it rejects. What real history rows diff to is covered end to end in
/// <c>Hindsight.IntegrationTests.VersionDiffTests</c>.
/// </summary>
public sealed class VersionDiffTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Diff_against_null_lists_every_versioned_property_with_a_null_old_value()
    {
        using var db = new TestContext();

        var changes = db.Diff(null, Version(NewPolicy(), VersionOperation.Insert, _t0));

        Assert.Equal(["Id", "Number", "Payload", "Premium", "Status", "Tags"], changes.Select(c => c.Property.Name));
        Assert.All(changes, c => Assert.Null(c.OldValue));
        Assert.Equal("ACME-1", changes.Single(c => c.Property.Name == "Number").NewValue);
    }

    [Fact]
    public void Diff_reports_only_the_properties_whose_values_differ_with_old_and_new_values()
    {
        using var db = new TestContext();
        var older = NewPolicy();
        var newer = NewPolicy();
        newer.Status = PolicyStatus.Active;
        newer.Premium = 250.50m;

        var changes = db.Diff(Version(older, VersionOperation.Insert, _t0), Version(newer, VersionOperation.Update, _t0.AddHours(1)));

        Assert.Equal(["Premium", "Status"], changes.Select(c => c.Property.Name));
        Assert.Equal(100m, changes[0].OldValue);
        Assert.Equal(250.50m, changes[0].NewValue);
        Assert.Equal(PolicyStatus.Draft, changes[1].OldValue); // model (CLR) value, not the stored string
        Assert.Equal(PolicyStatus.Active, changes[1].NewValue);
    }

    [Fact]
    public void Diff_of_equal_snapshots_is_empty()
    {
        using var db = new TestContext();

        var changes = db.Diff(Version(NewPolicy(), VersionOperation.Insert, _t0), Version(NewPolicy(), VersionOperation.Update, _t0.AddHours(1)));

        Assert.Empty(changes);
    }

    [Fact]
    public void Diff_compares_arrays_by_content_not_by_reference()
    {
        using var db = new TestContext();
        var older = NewPolicy();
        var newer = NewPolicy();
        older.Tags = ["gold", "renewal"];
        newer.Tags = ["gold", "renewal"];
        older.Payload = [1, 2, 3];
        newer.Payload = [1, 2, 3];

        Assert.Empty(db.Diff(older, newer));

        newer.Tags = ["gold"];
        newer.Payload = [1, 2, 4];
        Assert.Equal(["Payload", "Tags"], db.Diff(older, newer).Select(c => c.Property.Name));
    }

    [Fact]
    public void Diff_ignores_properties_excluded_from_history()
    {
        using var db = new TestContext();
        var older = NewPolicy();
        var newer = NewPolicy();
        newer.UpdatedAt = older.UpdatedAt.AddDays(1);

        Assert.Empty(db.Diff(older, newer));
    }

    [Fact]
    public void Diff_of_two_different_entities_throws_ArgumentException_naming_the_key()
    {
        using var db = new TestContext();
        var other = NewPolicy();
        other.Id = 2;

        var ex = Assert.Throws<ArgumentException>(() =>
            db.Diff(Version(NewPolicy(), VersionOperation.Insert, _t0), Version(other, VersionOperation.Update, _t0.AddHours(1))));

        Assert.Contains("different entities", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Id'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_of_entities_differing_in_one_composite_key_part_throws_ArgumentException()
    {
        using var db = new TestContext();

        var ex = Assert.Throws<ArgumentException>(() =>
            db.Diff(new Reading { SensorId = 1, Sequence = 1 }, new Reading { SensorId = 1, Sequence = 2 }));

        Assert.Contains("'Sequence'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_with_versions_in_the_wrong_order_throws_ArgumentException()
    {
        using var db = new TestContext();

        var ex = Assert.Throws<ArgumentException>(() =>
            db.Diff(Version(NewPolicy(), VersionOperation.Update, _t0.AddHours(1)), Version(NewPolicy(), VersionOperation.Insert, _t0)));

        Assert.Equal("older", ex.ParamName);
    }

    [Fact]
    public void Diff_of_a_version_against_itself_throws_ArgumentException()
    {
        using var db = new TestContext();
        var version = Version(NewPolicy(), VersionOperation.Insert, _t0);

        Assert.Throws<ArgumentException>(() => db.Diff(version, version));
    }

    [Fact]
    public void Diff_with_a_delete_tombstone_as_the_newer_version_throws_ArgumentException()
    {
        using var db = new TestContext();

        var ex = Assert.Throws<ArgumentException>(() =>
            db.Diff(Version(NewPolicy(), VersionOperation.Insert, _t0), Version(NewPolicy(), VersionOperation.Delete, _t0.AddHours(1))));

        Assert.Equal("newer", ex.ParamName);
        Assert.Contains("tombstone", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_with_a_delete_tombstone_as_the_older_version_throws_ArgumentException()
    {
        using var db = new TestContext();

        var ex = Assert.Throws<ArgumentException>(() =>
            db.Diff(Version(NewPolicy(), VersionOperation.Delete, _t0), Version(NewPolicy(), VersionOperation.Insert, _t0.AddHours(1))));

        Assert.Equal("older", ex.ParamName);
    }

    [Fact]
    public void Diff_on_a_non_temporal_entity_throws_InvalidOperationException_naming_it()
    {
        using var db = new TestContext();

        var ex = Assert.Throws<InvalidOperationException>(() => db.Diff(new Widget(), new Widget()));

        Assert.Contains("Widget", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IsTemporal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_on_an_entity_with_a_versioned_shadow_property_throws_NotSupportedException_naming_it()
    {
        using var db = new TestContext();

        var ex = Assert.Throws<NotSupportedException>(() => db.Diff(new Contract(), new Contract()));

        Assert.Contains("'CustomerId'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IsExcluded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_on_an_entity_whose_shadow_property_is_excluded_compares_the_rest()
    {
        using var db = new TestContext();

        var changes = db.Diff(new Invoice { Id = 1, Amount = 10m }, new Invoice { Id = 1, Amount = 12m });

        Assert.Equal("Amount", Assert.Single(changes).Property.Name);
    }

    [Fact]
    public void Diff_when_UseHindsight_was_not_called_throws_InvalidOperationException_saying_so()
    {
        using var db = new NoHindsightContext();

        var ex = Assert.Throws<InvalidOperationException>(() => db.Diff(new Invoice { Id = 1 }, new Invoice { Id = 1 }));

        Assert.Contains("UseHindsight()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_with_a_null_newer_throws_ArgumentNullException()
    {
        using var db = new TestContext();

        Assert.Throws<ArgumentNullException>(() => db.Diff(NewPolicy(), (Policy)null!));
        Assert.Throws<ArgumentNullException>(() => db.Diff(null, (Version<Policy>)null!));
    }

    private static Policy NewPolicy()
        => new() { Id = 1, Number = "ACME-1", Status = PolicyStatus.Draft, Premium = 100m, UpdatedAt = _t0 };

    private static Version<Policy> Version(Policy entity, VersionOperation operation, DateTimeOffset validFrom)
        => new()
        {
            Entity = entity,
            Operation = operation,
            ValidFrom = validFrom,
            ValidTo = operation == VersionOperation.Delete ? validFrom : DateTimeOffset.MaxValue,
        };

    private enum PolicyStatus
    {
        Draft,
        Active,
    }

    private sealed class Policy
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public PolicyStatus Status { get; set; }
        public decimal Premium { get; set; }
        public string[] Tags { get; set; } = [];
        public byte[] Payload { get; set; } = [];
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class Reading
    {
        public int SensorId { get; set; }
        public int Sequence { get; set; }
        public double Value { get; set; }
    }

    private sealed class Customer
    {
        public int Id { get; set; }
    }

    private sealed class Contract
    {
        public int Id { get; set; }
        public Customer? Customer { get; set; }
    }

    private sealed class Invoice
    {
        public int Id { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class Widget
    {
        public int Id { get; set; }
    }

    private sealed class TestContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseNpgsql("Host=localhost;Database=unused").UseHindsight();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var policy = modelBuilder.Entity<Policy>();
            policy.Property(p => p.Status).HasConversion<string>();
            policy.IsTemporal(t => t.Exclude(p => p.UpdatedAt));

            modelBuilder.Entity<Reading>().HasKey(r => new { r.SensorId, r.Sequence });
            modelBuilder.Entity<Reading>().IsTemporal();

            modelBuilder.Entity<Customer>();

            // Shadow FK "CustomerId": mirrored into history, but no CLR member carries it.
            modelBuilder.Entity<Contract>().IsTemporal();

            var invoice = modelBuilder.Entity<Invoice>();
            invoice.Property<string>("Note").HasAnnotation(HindsightAnnotationNames.IsExcluded, true);
            invoice.IsTemporal();

            modelBuilder.Entity<Widget>();
        }
    }

    private sealed class NoHindsightContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseNpgsql("Host=localhost;Database=unused");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Invoice>().IsTemporal();
    }
}
