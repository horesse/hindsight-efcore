using Hindsight;
using InsuranceSample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocsSnippets;

public sealed class CustomizedTemporalContext(DbContextOptions<CustomizedTemporalContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        #region configure-temporal
        modelBuilder.Entity<Policy>(policy =>
        {
            policy.ToTable("policies");

            policy.IsTemporal(temporal => temporal
                .UseHistoryTable("policy_versions", schema: "audit") // default: policies_history, same schema
                .HasPeriodStart("sys_from")                          // default: valid_from
                .HasPeriodEnd("sys_to")                              // default: valid_to
                .Exclude(p => p.UpdatedAt));                         // never copied to history
        });
        #endregion configure-temporal
    }
}

public sealed class DbSessionUserContext(DbContextOptions<DbSessionUserContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        #region with-db-session-user
        modelBuilder.Entity<Policy>(policy =>
        {
            policy.ToTable("policies");

            // db_session_user is populated with PostgreSQL's own session_user on every write, in both
            // writer modes; unlike changed_by it cannot be forged with set_config (DESIGN.md D16).
            policy.IsTemporal(temporal => temporal.WithDbSessionUser());
        });
        #endregion with-db-session-user
    }
}

public static class ContextOptions
{
    public static void Defaults(IServiceCollection services, string connectionString)
    {
        #region use-hindsight
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseHindsight());
        #endregion use-hindsight
    }

    public static void Configured(IServiceCollection services, string connectionString)
    {
        #region use-hindsight-configured
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseHindsight(hindsight => hindsight
                .UseHistoryWriter(HistoryWriter.Trigger)
                .WithChangeContext<HttpChangeContextProvider>()));
        #endregion use-hindsight-configured
    }

    public static AppDbContext WithoutDependencyInjection(string connectionString)
    {
        #region options-builder
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseHindsight(hindsight => hindsight.UseHistoryWriter(HistoryWriter.Trigger))
            .Options;

        var db = new AppDbContext(options);
        #endregion options-builder
        return db;
    }

    public static void DeterministicTime(IServiceCollection services, string connectionString, TimeProvider clock)
    {
        #region time-provider
        // HistoryWriter.Interceptor stamps history with the TimeProvider registered here,
        // for example a FakeTimeProvider in tests; without one it uses TimeProvider.System.
        services.AddSingleton(clock);

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseHindsight(hindsight => hindsight.UseHistoryWriter(HistoryWriter.Interceptor)));
        #endregion time-provider
    }
}
