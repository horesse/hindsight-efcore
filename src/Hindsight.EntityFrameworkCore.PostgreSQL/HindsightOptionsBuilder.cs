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
    /// <see cref="HistoryWriter.Interceptor"/>; <see cref="HistoryWriter.Trigger"/> is recommended in
    /// production. Both produce the same history schema and the same half-open intervals — see
    /// <see cref="HistoryWriter"/> for the trade-offs.
    /// </summary>
    /// <param name="writer">The writer to use.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// <paramref name="writer"/> is not a defined <see cref="HistoryWriter"/> value.
    /// </exception>
    public HindsightOptionsBuilder UseHistoryWriter(HistoryWriter writer)
    {
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

    /// <summary>
    /// Registers the <see cref="IChangeContextProvider"/> that supplies the "who / why" of each
    /// <c>SaveChanges</c>. With <see cref="HistoryWriter.Interceptor"/> the provider is called once per
    /// <c>SaveChanges</c> and its <see cref="ChangeContext"/> is written into the <c>changed_by</c>,
    /// <c>changed_by_name</c>, <c>correlation_id</c>, <c>reason</c> and <c>extra</c> columns of that
    /// call's history rows. Without this call those columns are written <see langword="null"/>.
    /// </summary>
    /// <typeparam name="TProvider">
    /// A concrete <see cref="IChangeContextProvider"/>. It is resolved per <c>SaveChanges</c> from the
    /// application service provider (register it there if it has dependencies); a type with a
    /// parameterless constructor is created directly.
    /// </typeparam>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// <typeparamref name="TProvider"/> is abstract, so it can never be instantiated. Pass a concrete
    /// implementation.
    /// </exception>
    public HindsightOptionsBuilder WithChangeContext<TProvider>()
        where TProvider : class, IChangeContextProvider
    {
        var providerType = typeof(TProvider);
        if (providerType.IsAbstract)
        {
            throw new InvalidOperationException(
                $"Change context provider '{providerType.FullName}' is abstract and cannot be "
                + "instantiated. Pass a concrete IChangeContextProvider implementation to "
                + "WithChangeContext<TProvider>().");
        }

        var extension = _optionsBuilder.Options.FindExtension<HindsightOptionsExtension>()
            ?? new HindsightOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)_optionsBuilder)
            .AddOrUpdateExtension(extension.WithChangeContextProvider(providerType));

        return this;
    }
}
