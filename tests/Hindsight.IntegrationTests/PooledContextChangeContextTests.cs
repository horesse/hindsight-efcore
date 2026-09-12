using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Hindsight.IntegrationTests;

/// <summary>
/// <c>AddDbContextPool&lt;T&gt;()</c> and <c>AddDbContextFactory&lt;T&gt;()</c> (pooled or not) build one
/// <c>DbContextOptions</c> instance once, so <c>CoreOptionsExtension.ApplicationServiceProvider</c> is
/// whichever provider was active at that moment — normally the application's root container, never a
/// request's scope (confirmed empirically against EF Core 10.0.12; see the "Pooled and factory-created
/// contexts" section of docs/articles/configuration.md). These tests reproduce exactly that condition —
/// a real root <see cref="ServiceProvider"/>, handed to every <see cref="DbContext"/> instance through
/// <c>UseApplicationServiceProvider</c>, exactly like the shared options template every pooled or
/// factory-created context carries — without needing a real ASP.NET Core host or reflection on
/// Microsoft.Extensions.DependencyInjection internals.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PooledContextChangeContextTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // PostgreSQL truncates identifiers at 63 bytes; see RetryingExecutionStrategyTests for the same
    // helper's rationale.
    private static string DbName(string label, HistoryWriter writer)
    {
        var suffix = writer == HistoryWriter.Trigger ? "_trg" : "_int";
        var trimmed = label.Length > 58 ? label[..58] : label;
        return trimmed + suffix;
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Scoped_provider_resolved_from_a_captured_root_provider_throws_a_Hindsight_specific_message(
        HistoryWriter writer)
    {
        // Simulates ServiceProviderOptions.ValidateScopes = true (ASP.NET Core's Development default):
        // the DbContextPool<T>/DbContextFactory<T> captured the ROOT container as
        // ApplicationServiceProvider, but ScopedProvider is registered Scoped — the exact trap the
        // "Pooled and factory-created contexts" section of docs/articles/configuration.md warns about.
        var services = new ServiceCollection();
        services.AddScoped<ScopedProvider>();
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var cs = await postgres.CreateDatabaseAsync(
            DbName(nameof(Scoped_provider_resolved_from_a_captured_root_provider_throws_a_Hindsight_specific_message), writer),
            Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs)
            .UseApplicationServiceProvider(root)
            .UseHindsight(h => h.WithChangeContext<ScopedProvider>().UseHistoryWriter(writer))
            .Options;

        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        await using var db = new WidgetContext(options);
        db.Widgets.Add(new Widget { Name = "a" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

        Assert.Contains(nameof(ScopedProvider), ex.Message);
        Assert.Contains("AddDbContextPool", ex.Message);
        Assert.Contains("singleton", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidOperationException>(ex.InnerException); // the original DI failure, preserved, not swallowed

        // Nothing was written: the resolution failure happens inside SavingChanges, before either
        // writer touches the database.
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from widgets";
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }

    [Theory]
    [InlineData(HistoryWriter.Interceptor)]
    [InlineData(HistoryWriter.Trigger)]
    public async Task Singleton_provider_reading_ambient_state_gets_the_correct_value_across_reused_options(
        HistoryWriter writer)
    {
        // The documented fix: a singleton provider that reads per-request ambient state fresh inside
        // GetChangeContext instead of capturing a scoped dependency at construction time. Two DbContext
        // instances built from the SAME DbContextOptions — exactly how a pool/factory reuses
        // ApplicationServiceProvider across requests — must each see the ambient value that was current
        // for their own SaveChanges, not whichever one constructed the singleton first.
        var ambient = new AmbientUserHolder();
        var services = new ServiceCollection();
        services.AddSingleton(ambient);
        services.AddSingleton<AmbientReadingProvider>();
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var cs = await postgres.CreateDatabaseAsync(
            DbName(nameof(Singleton_provider_reading_ambient_state_gets_the_correct_value_across_reused_options), writer),
            Ct);
        var options = new DbContextOptionsBuilder<WidgetContext>()
            .UseNpgsql(cs)
            .UseApplicationServiceProvider(root)
            .UseHindsight(h => h.WithChangeContext<AmbientReadingProvider>().UseHistoryWriter(writer))
            .Options;

        await using (var setup = new WidgetContext(options))
        {
            await setup.Database.EnsureCreatedAsync(Ct);
        }

        ambient.CurrentUser = "user-request-1";
        await using (var db1 = new WidgetContext(options))
        {
            db1.Widgets.Add(new Widget { Name = "a" });
            await db1.SaveChangesAsync(Ct);
        }

        ambient.CurrentUser = "user-request-2";
        await using (var db2 = new WidgetContext(options)) // same shared `options` — simulates pool/factory reuse
        {
            db2.Widgets.Add(new Widget { Name = "b" });
            await db2.SaveChangesAsync(Ct);
        }

        var changedBy = await ReadChangedByInOrderAsync(cs, Ct);
        Assert.Equal(["user-request-1", "user-request-2"], changedBy);
    }

    private static async Task<List<string?>> ReadChangedByInOrderAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select changed_by from widgets_history order by valid_from, history_id";

        var result = new List<string?>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return result;
    }

    private sealed class Widget
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class WidgetContext(DbContextOptions<WidgetContext> options) : DbContext(options)
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var widget = modelBuilder.Entity<Widget>();
            widget.ToTable("widgets");
            widget.IsTemporal();
        }
    }

    // Shaped like the trap the "Pooled and factory-created contexts" section warns about: a scoped
    // registration. Never actually constructed in these tests — ValidateScopes=true means the container
    // refuses to resolve it from the root provider before getting anywhere near its constructor.
    private sealed class ScopedProvider : IChangeContextProvider
    {
        public ChangeContext GetChangeContext(DbContext context) => ChangeContext.Empty with { UserId = "should never run" };
    }

    private sealed class AmbientUserHolder
    {
        public string? CurrentUser { get; set; }
    }

    // The documented safe pattern: singleton, reads ambient per-request state fresh on every call
    // instead of capturing a scoped dependency at construction time.
    private sealed class AmbientReadingProvider(AmbientUserHolder ambient) : IChangeContextProvider
    {
        public ChangeContext GetChangeContext(DbContext context) => ChangeContext.Empty with { UserId = ambient.CurrentUser };
    }
}
