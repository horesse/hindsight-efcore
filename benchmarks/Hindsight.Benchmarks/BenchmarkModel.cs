using Microsoft.EntityFrameworkCore;

namespace Hindsight.Benchmarks;

/// <summary>Which history writer (if any) the benchmarked <see cref="DbContext"/> runs with.</summary>
public enum HistoryMode
{
    /// <summary>Plain EF Core: <c>UseNpgsql</c> without <c>UseHindsight</c>. The baseline.</summary>
    None,

    /// <summary>History written by the <c>SaveChangesInterceptor</c> in the application.</summary>
    Interceptor,

    /// <summary>History written by the plpgsql trigger generated into the schema.</summary>
    Trigger,
}

/// <summary>Status of a <see cref="Policy"/>, stored as text like in the integration tests.</summary>
public enum PolicyStatus
{
    /// <summary>Not yet in force.</summary>
    Draft,

    /// <summary>In force.</summary>
    Active,

    /// <summary>Terminated.</summary>
    Cancelled,
}

/// <summary>The one entity the benchmarks write, mirroring the sample insurance policy.</summary>
public sealed class Policy
{
    /// <summary>Store-generated key.</summary>
    public int Id { get; set; }

    /// <summary>Policy number.</summary>
    public string Number { get; set; } = "";

    /// <summary>Lifecycle status.</summary>
    public PolicyStatus Status { get; set; }

    /// <summary>Annual premium.</summary>
    public decimal Premium { get; set; }

    /// <summary>Excluded from versioning; changing it alone writes no history row.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A minimal temporal-entity context: one <see cref="Policy"/> marked <c>IsTemporal</c>.</summary>
public sealed class BenchmarkContext(DbContextOptions<BenchmarkContext> options) : DbContext(options)
{
    /// <summary>The policies set.</summary>
    public DbSet<Policy> Policies => Set<Policy>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var policy = modelBuilder.Entity<Policy>();
        policy.ToTable("policies");
        policy.Property(p => p.Id).HasColumnName("id");
        policy.Property(p => p.Number).HasColumnName("number");
        policy.Property(p => p.Status).HasColumnName("status").HasConversion<string>();
        policy.Property(p => p.Premium).HasColumnName("premium").HasPrecision(18, 4);
        policy.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        policy.IsTemporal(t => t.Exclude(p => p.UpdatedAt));
    }
}
