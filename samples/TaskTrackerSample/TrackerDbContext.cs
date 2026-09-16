using Hindsight;
using Microsoft.EntityFrameworkCore;

namespace TaskTrackerSample;

public sealed class TrackerDbContext(DbContextOptions<TrackerDbContext> options) : DbContext(options)
{
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItem>(e =>
        {
            e.ToTable("work_items");
            e.Property(w => w.Status).HasConversion<string>();
            e.IsTemporal(t => t.Exclude(w => w.UpdatedAt));
        });
    }
}
