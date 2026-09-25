using System.Diagnostics;
using Hindsight;
using InsuranceSample;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocsSnippets;

#region http-provider
public sealed class HttpChangeContextProvider(IHttpContextAccessor accessor) : IChangeContextProvider
{
    // Called once per SaveChanges that writes a temporal entity. Read per-request state here,
    // not in the constructor: the provider is registered as a singleton.
    public ChangeContext GetChangeContext(DbContext context)
    {
        var user = accessor.HttpContext?.User;
        return new ChangeContext
        {
            ChangedBy = user?.FindFirst("sub")?.Value,
            ChangedByName = user?.Identity?.Name,
            CorrelationId = Activity.Current?.TraceId.ToString(),
            Extra = """{"source":"web"}""",
        };
    }
}
#endregion http-provider

public static class ChangeContextSamples
{
    public static void Register(IServiceCollection services, string connectionString)
    {
        #region register-provider
        services.AddHttpContextAccessor();
        services.AddSingleton<HttpChangeContextProvider>();

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseHindsight(hindsight => hindsight
                .UseHistoryWriter(HistoryWriter.Trigger)
                .WithChangeContext<HttpChangeContextProvider>()));
        #endregion register-provider
    }

    public static void RegisterPooled(IServiceCollection services, string connectionString)
    {
        #region register-pooled
        services.AddHttpContextAccessor();
        services.AddSingleton<HttpChangeContextProvider>(); // singleton, never scoped

        services.AddDbContextPool<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseHindsight(hindsight => hindsight.WithChangeContext<HttpChangeContextProvider>()));
        #endregion register-pooled
    }

    public static async Task ReasonAsync(AppDbContext db, Policy policy, decimal corrected)
    {
        #region with-reason
        using (db.WithReason("Backdated correction after audit"))
        {
            policy.Premium = corrected;
            await db.SaveChangesAsync(); // reason = 'Backdated correction after audit'
        }
        #endregion with-reason
    }
}
