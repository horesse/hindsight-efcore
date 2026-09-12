using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hindsight.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SmokeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Container_Starts_And_Accepts_Connections()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = await postgres.CreateDatabaseAsync("smoke", ct);

        // Suppressed: across the whole test process, a handful of files each construct a small number
        // of deliberately distinct, short-lived DbContext service-provider configurations for diffing
        // or mocking purposes — legitimate here, but it is exactly the "unique service provider per
        // context" pattern this EF Core diagnostic exists to catch in long-lived production code. Once
        // the process-wide cumulative count crosses EF's built-in threshold, whichever context happens
        // to be constructed next throws — not necessarily one of the contexts actually responsible.
        var options = new DbContextOptionsBuilder<SmokeContext>()
            .UseNpgsql(cs)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        await using var db = new SmokeContext(options);

        Assert.True(await db.Database.CanConnectAsync(ct));
    }

    private sealed class SmokeContext(DbContextOptions<SmokeContext> options) : DbContext(options);
}
