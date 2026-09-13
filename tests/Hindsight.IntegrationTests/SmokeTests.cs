using Microsoft.EntityFrameworkCore;

namespace Hindsight.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SmokeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Container_Starts_And_Accepts_Connections()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await postgres.CreateDatabaseAsync("smoke", ct);

        // EnableServiceProviderCaching(false): this context is built once, used briefly and disposed —
        // never reused — so it should never have been cached in the first place. Every integration test
        // file builds its own one-shot context the same way; left caching-enabled, the process-wide
        // cumulative count eventually crosses EF's built-in "more than twenty service providers" cap.
        var options = new DbContextOptionsBuilder<SmokeContext>()
            .UseNpgsql(cs)
            .EnableServiceProviderCaching(false)
            .Options;
        await using var db = new SmokeContext(options);

        Assert.True(await db.Database.CanConnectAsync(ct));
    }

    private sealed class SmokeContext(DbContextOptions<SmokeContext> options) : DbContext(options);
}
