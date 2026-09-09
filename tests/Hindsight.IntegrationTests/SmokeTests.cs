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

        var options = new DbContextOptionsBuilder<SmokeContext>().UseNpgsql(cs).Options;
        await using var db = new SmokeContext(options);

        Assert.True(await db.Database.CanConnectAsync(ct));
    }

    private sealed class SmokeContext(DbContextOptions<SmokeContext> options) : DbContext(options);
}
