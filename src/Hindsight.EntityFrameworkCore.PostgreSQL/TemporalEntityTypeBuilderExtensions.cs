using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hindsight;

/// <summary>
/// Fluent configuration entry point: <c>modelBuilder.Entity&lt;Policy&gt;().IsTemporal()</c>.
/// </summary>
public static class TemporalEntityTypeBuilderExtensions
{
    /// <summary>Default suffix appended to the entity's table name to form the history table name.</summary>
    public const string DefaultHistoryTableSuffix = "_history";

    /// <summary>Default column name for the start of the system-time period.</summary>
    public const string DefaultPeriodStartColumnName = "valid_from";

    /// <summary>Default column name for the end of the system-time period.</summary>
    public const string DefaultPeriodEndColumnName = "valid_to";

    /// <summary>
    /// Configures the entity type as system-versioned. A history table is generated alongside the
    /// main table and every insert, update and delete produces a history row.
    /// </summary>
    /// <typeparam name="TEntity">The entity type being configured.</typeparam>
    /// <param name="builder">The builder for the entity type.</param>
    /// <param name="configure">Optional temporal configuration.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public static EntityTypeBuilder<TEntity> IsTemporal<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Action<TemporalEntityTypeBuilder<TEntity>>? configure = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.HasAnnotation(HindsightAnnotationNames.IsTemporal, true);
        configure?.Invoke(new TemporalEntityTypeBuilder<TEntity>(builder));
        return builder;
    }
}
