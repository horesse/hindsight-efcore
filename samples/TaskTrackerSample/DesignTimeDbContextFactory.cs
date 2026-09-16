using Hindsight;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TaskTrackerSample;

/// <summary>
/// Used by <c>dotnet ef</c> at design time. The connection string is never opened for
/// <c>migrations add</c>; it only needs to be syntactically valid for Npgsql.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TrackerDbContext>
{
    public TrackerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseNpgsql("Host=localhost;Database=hindsight_sample;Username=postgres;Password=postgres")
            .UseHindsight(h => h
                .UseHistoryWriter(HistoryWriter.Trigger)
                .WithChangeContext<DemoChangeContextProvider>())
            .Options;

        return new TrackerDbContext(options);
    }
}
