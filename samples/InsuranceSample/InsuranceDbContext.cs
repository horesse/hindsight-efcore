using Hindsight;
using Microsoft.EntityFrameworkCore;

namespace InsuranceSample;

public sealed class InsuranceDbContext(DbContextOptions<InsuranceDbContext> options) : DbContext(options)
{
    public DbSet<Policy> Policies => Set<Policy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Policy>(e =>
        {
            e.ToTable("policies");
            e.Property(p => p.Status).HasConversion<string>();
            e.IsTemporal(t => t.Exclude(p => p.UpdatedAt));
        });
    }
}
