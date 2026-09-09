using Hindsight.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Hindsight;

/// <summary>
/// Configures Hindsight for a context. Obtained from
/// <see cref="HindsightDbContextOptionsExtensions.UseHindsight(DbContextOptionsBuilder, System.Action{HindsightOptionsBuilder})"/>:
/// <c>options.UseHindsight(h =&gt; h.UseHistoryWriter(HistoryWriter.Interceptor))</c>.
/// </summary>
public sealed class HindsightOptionsBuilder
{
    private readonly DbContextOptionsBuilder _optionsBuilder;

    internal HindsightOptionsBuilder(DbContextOptionsBuilder optionsBuilder)
    {
        _optionsBuilder = optionsBuilder;
    }

    /// <summary>
    /// Chooses the history writer. The default, if this is never called, is
    /// <see cref="HistoryWriter.Interceptor"/>.
    /// </summary>
    /// <param name="writer">The writer to use.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    /// <exception cref="System.NotSupportedException">
    /// <paramref name="writer"/> is <see cref="HistoryWriter.Trigger"/>. The trigger writer is not
    /// implemented yet; it arrives in a later release. Use <see cref="HistoryWriter.Interceptor"/>.
    /// </exception>
    public HindsightOptionsBuilder UseHistoryWriter(HistoryWriter writer)
    {
        if (writer == HistoryWriter.Trigger)
        {
            throw new NotSupportedException(
                "HistoryWriter.Trigger is not implemented yet; it arrives in a later release. "
                + "Use HistoryWriter.Interceptor (the default), which snapshots tracked entities in a "
                + "SaveChangesInterceptor and writes history rows in the same transaction.");
        }

        if (!Enum.IsDefined(writer))
        {
            throw new ArgumentOutOfRangeException(nameof(writer), writer, "Unknown history writer.");
        }

        var extension = _optionsBuilder.Options.FindExtension<HindsightOptionsExtension>()
            ?? new HindsightOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)_optionsBuilder)
            .AddOrUpdateExtension(extension.WithHistoryWriter(writer));

        return this;
    }
}
