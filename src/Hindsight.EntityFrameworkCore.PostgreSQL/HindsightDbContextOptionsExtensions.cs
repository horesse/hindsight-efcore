using Hindsight.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Hindsight;

/// <summary>
/// <c>DbContextOptionsBuilder</c> extensions that turn Hindsight on for a context.
/// </summary>
public static class HindsightDbContextOptionsExtensions
{
    /// <summary>
    /// Enables Hindsight for the context. Every entity type configured with
    /// <see cref="TemporalEntityTypeBuilderExtensions.IsTemporal{TEntity}"/> gets a history table
    /// generated into the model, so <c>dotnet ef migrations add</c> creates it alongside the main table,
    /// and every insert, update and delete of a temporal entity writes a history row via the default
    /// <see cref="HistoryWriter.Interceptor"/> writer. Call this after <c>UseNpgsql(...)</c>.
    /// </summary>
    /// <param name="optionsBuilder">The builder being configured.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public static DbContextOptionsBuilder UseHindsight(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        EnsureExtension(optionsBuilder);
        return optionsBuilder;
    }

    /// <summary>
    /// Enables Hindsight for the context and configures it, for example
    /// <c>UseHindsight(h =&gt; h.UseHistoryWriter(HistoryWriter.Interceptor))</c>. See
    /// <see cref="UseHindsight(DbContextOptionsBuilder)"/> for what enabling does.
    /// </summary>
    /// <param name="optionsBuilder">The builder being configured.</param>
    /// <param name="configure">Configures the Hindsight options.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public static DbContextOptionsBuilder UseHindsight(
        this DbContextOptionsBuilder optionsBuilder,
        Action<HindsightOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configure);

        EnsureExtension(optionsBuilder);
        configure(new HindsightOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseHindsight(DbContextOptionsBuilder)"/>
    /// <typeparam name="TContext">The context type being configured.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseHindsight<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseHindsight((DbContextOptionsBuilder)optionsBuilder);

    /// <inheritdoc cref="UseHindsight(DbContextOptionsBuilder, Action{HindsightOptionsBuilder})"/>
    /// <typeparam name="TContext">The context type being configured.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseHindsight<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<HindsightOptionsBuilder> configure)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseHindsight((DbContextOptionsBuilder)optionsBuilder, configure);

    private static void EnsureExtension(DbContextOptionsBuilder optionsBuilder)
    {
        var extension = optionsBuilder.Options.FindExtension<HindsightOptionsExtension>()
            ?? new HindsightOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
    }
}
