using Hindsight.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.Tests;

public sealed class HindsightOptionsBuilderTests
{
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
    public void UseHistoryWriter_Trigger_throws_NotSupportedException_pointing_at_Interceptor()
    {
        var builder = new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=unused");

        var ex = Assert.Throws<NotSupportedException>(
            () => builder.UseHindsight(h => h.UseHistoryWriter(HistoryWriter.Trigger)));

        Assert.Contains("HistoryWriter.Interceptor", ex.Message);
    }
}
