using Hindsight;
using InsuranceSample;
using Microsoft.EntityFrameworkCore;

namespace DocsSnippets;

/// <summary>The context most samples use: the sample <see cref="Policy"/> entity, made temporal.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Policy> Policies => Set<Policy>();

    #region mark-temporal
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Policy>().IsTemporal();
    }
    #endregion mark-temporal
}
