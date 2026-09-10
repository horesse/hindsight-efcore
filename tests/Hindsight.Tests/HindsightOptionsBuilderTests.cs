using Hindsight.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.Tests;

public sealed class HindsightOptionsBuilderTests
{
    [Fact]
    public void WithChangeContext_records_the_provider_type()
    {
        var options = new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseHindsight(h => h.WithChangeContext<StubProvider>())
            .Options;

        Assert.Equal(
            typeof(StubProvider),
            options.FindExtension<HindsightOptionsExtension>()!.ChangeContextProviderType);
    }

    [Fact]
    public void WithChangeContext_on_an_abstract_provider_throws_InvalidOperationException()
    {
        var builder = new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=unused");

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.UseHindsight(h => h.WithChangeContext<AbstractProvider>()));

        Assert.Contains(nameof(AbstractProvider), ex.Message);
    }

    private sealed class StubProvider : IChangeContextProvider
    {
        public ChangeContext GetChangeContext(DbContext context) => ChangeContext.Empty;
    }

    private abstract class AbstractProvider : IChangeContextProvider
    {
        public abstract ChangeContext GetChangeContext(DbContext context);
    }

    [Fact]
    public void UseHindsight_without_configuration_selects_the_Interceptor_writer()
    {
        var options = new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseHindsight()
            .Options;

        Assert.Equal(
            HistoryWriter.Interceptor,
            options.FindExtension<HindsightOptionsExtension>()!.HistoryWriter);
    }

    [Fact]
    public void UseHistoryWriter_records_the_choice()
    {
        var options = new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Interceptor))
            .Options;

        Assert.Equal(
            HistoryWriter.Interceptor,
            options.FindExtension<HindsightOptionsExtension>()!.HistoryWriter);
    }

    [Fact]
    public void UseHistoryWriter_records_the_Trigger_choice()
    {
        var options = new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger))
            .Options;

        Assert.Equal(
            HistoryWriter.Trigger,
            options.FindExtension<HindsightOptionsExtension>()!.HistoryWriter);
    }

    [Fact]
    public void UseHistoryWriter_with_an_undefined_value_throws_ArgumentOutOfRangeException()
    {
        var builder = new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=unused");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.UseHindsight(h => h.UseHistoryWriter((HistoryWriter)42)));
    }
}
