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
    /// generated into the model, so <c>dotnet ef migrations add</c> creates it alongside the main table.
    /// Call this after <c>UseNpgsql(...)</c>.
    /// </summary>
    /// <param name="optionsBuilder">The builder being configured.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public static DbContextOptionsBuilder UseHindsight(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var extension = optionsBuilder.Options.FindExtension<HindsightOptionsExtension>()
            ?? new HindsightOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseHindsight(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder)"/>
    /// <typeparam name="TContext">The context type being configured.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseHindsight<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseHindsight((DbContextOptionsBuilder)optionsBuilder);
}
