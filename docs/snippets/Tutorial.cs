using Hindsight;
using InsuranceSample;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocsSnippets;

public static class Tutorial
{
    public static void Configure(WebApplicationBuilder builder)
    {
        #region tutorial-register
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<HttpChangeContextProvider>();

        builder.Services.AddDbContext<InsuranceDbContext>(options => options
            .UseNpgsql(builder.Configuration.GetConnectionString("Insurance"))
            .UseHindsight(hindsight => hindsight
                .UseHistoryWriter(HistoryWriter.Trigger)
                .WithChangeContext<HttpChangeContextProvider>()));
        #endregion tutorial-register
    }

    public static void MapWrites(WebApplication app)
    {
        #region tutorial-write
        app.MapPost("/policies", async (Policy policy, InsuranceDbContext db) =>
        {
            db.Policies.Add(policy);
            await db.SaveChangesAsync();
            return Results.Created($"/policies/{policy.Id}", policy);
        });

        app.MapPost("/policies/{id:int}/activate", async (int id, string reason, InsuranceDbContext db) =>
        {
            var policy = await db.Policies.SingleAsync(p => p.Id == id);
            policy.Status = PolicyStatus.Active;

            using (db.WithReason(reason))
            {
                await db.SaveChangesAsync();
            }

            return Results.NoContent();
        });

        app.MapPut("/policies/{id:int}/premium", async (int id, decimal premium, string reason, InsuranceDbContext db) =>
        {
            var policy = await db.Policies.SingleAsync(p => p.Id == id);
            policy.Premium = premium;

            using (db.WithReason(reason))
            {
                await db.SaveChangesAsync();
            }

            return Results.NoContent();
        });

        app.MapDelete("/policies/{id:int}", async (int id, string reason, InsuranceDbContext db) =>
        {
            var policy = await db.Policies.SingleAsync(p => p.Id == id);
            db.Policies.Remove(policy);

            using (db.WithReason(reason))
            {
                await db.SaveChangesAsync();
            }

            return Results.NoContent();
        });
        #endregion tutorial-write
    }

    public static void MapReads(WebApplication app)
    {
        #region tutorial-as-of
        // GET /policies/1/as-of?at=2026-09-01T10:00:00Z
        app.MapGet("/policies/{id:int}/as-of", async (int id, DateTimeOffset at, InsuranceDbContext db) =>
            await db.Policies.AsOf(at).SingleOrDefaultAsync(p => p.Id == id) is { } policy
                ? Results.Ok(policy)
                : Results.NotFound());
        #endregion tutorial-as-of

        #region tutorial-history
        // GET /policies/1/history
        app.MapGet("/policies/{id:int}/history", async (int id, InsuranceDbContext db) =>
            await db.History<Policy>()
                .Where(v => v.Entity.Id == id)
                .Select(v => new
                {
                    v.ValidFrom,
                    v.ValidTo,
                    v.Operation,
                    v.ChangedByName,
                    v.Reason,
                    v.Entity.Status,
                    v.Entity.Premium,
                })
                .ToListAsync());
        #endregion tutorial-history

        #region tutorial-deleted
        // GET /policies/deleted
        app.MapGet("/policies/deleted", async (InsuranceDbContext db) =>
            await db.History<Policy>()
                .Where(v => v.Operation == VersionOperation.Delete)
                .Select(v => new { v.Entity.Id, v.Entity.Number, DeletedAt = v.ValidFrom, v.ChangedByName, v.Reason })
                .ToListAsync());
        #endregion tutorial-deleted
    }
}
