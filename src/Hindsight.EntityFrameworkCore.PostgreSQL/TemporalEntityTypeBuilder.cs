using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hindsight;

/// <summary>
/// Configures the temporal aspects of an entity type: where history is stored, how the period
/// columns are named, and which properties are excluded from versioning.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
public sealed class TemporalEntityTypeBuilder<TEntity>
    where TEntity : class
{
    private readonly EntityTypeBuilder<TEntity> _builder;

    internal TemporalEntityTypeBuilder(EntityTypeBuilder<TEntity> builder)
    {
        _builder = builder;
    }

    /// <summary>Sets the name (and optionally schema) of the history table.</summary>
    /// <param name="name">History table name.</param>
    /// <param name="schema">History table schema, or <see langword="null"/> for the model default.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public TemporalEntityTypeBuilder<TEntity> UseHistoryTable(string name, string? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _builder.HasAnnotation(HindsightAnnotationNames.HistoryTableName, name);
        _builder.HasAnnotation(HindsightAnnotationNames.HistoryTableSchema, schema);
        return this;
    }

    /// <summary>Sets the column name for the start of the system-time period.</summary>
    /// <param name="columnName">Column name.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public TemporalEntityTypeBuilder<TEntity> HasPeriodStart(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        _builder.HasAnnotation(HindsightAnnotationNames.PeriodStartColumnName, columnName);
        return this;
    }

    /// <summary>Sets the column name for the end of the system-time period.</summary>
    /// <param name="columnName">Column name.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public TemporalEntityTypeBuilder<TEntity> HasPeriodEnd(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        _builder.HasAnnotation(HindsightAnnotationNames.PeriodEndColumnName, columnName);
        return this;
    }

    /// <summary>
    /// Excludes a property from versioning. The column is not copied to the history table and a
    /// change to this property alone does not produce a history row.
    /// </summary>
    /// <typeparam name="TProperty">Property type.</typeparam>
    /// <param name="property">Property selector.</param>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public TemporalEntityTypeBuilder<TEntity> Exclude<TProperty>(Expression<Func<TEntity, TProperty>> property)
    {
        ArgumentNullException.ThrowIfNull(property);

        _builder.Property(property).HasAnnotation(HindsightAnnotationNames.IsExcluded, true);
        return this;
    }

    /// <summary>
    /// Adds an additional, always-populated history column, <c>db_session_user text not null default
    /// session_user</c>, holding the PostgreSQL role that actually executed the write. Unlike the
    /// application-supplied change-context columns (<c>changed_by</c> and friends), this value is
    /// guaranteed by PostgreSQL's own connection authentication and cannot be forged with
    /// <c>set_config</c> — see DESIGN.md D16 for the threat model this defends against, and what it does
    /// not. Opt-in: not part of the base history column set (DESIGN.md D5), since a pooled application
    /// connection's <c>session_user</c> is typically one shared service role rather than a human
    /// identity, so the column is not universally useful.
    /// </summary>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public TemporalEntityTypeBuilder<TEntity> WithDbSessionUser()
    {
        _builder.HasAnnotation(HindsightAnnotationNames.HasDbSessionUser, true);
        return this;
    }

    /// <summary>
    /// Allows this entity's history to be pruned with
    /// <see cref="HindsightDbContextExtensions.PruneHistoryAsync{TEntity}"/>, and makes <c>AsOf</c>,
    /// <c>FromTo</c> and <c>ContainedIn</c> fail with a <c>PostgresException</c> (SQLSTATE <c>HS001</c>) for an instant
    /// before the pruned history's horizon instead of answering from incomplete history.
    /// </summary>
    /// <remarks>
    /// The next migration creates the <c>hindsight_retention_horizon</c> table and the
    /// <c>hindsight_history_retained</c> function the check calls; nothing is removed by a migration.
    /// Once a migration has recorded this option, removing it throws at model build: history that may
    /// already be pruned would then answer <c>AsOf</c> for an instant it no longer covers.
    /// </remarks>
    /// <returns>The same builder instance so that calls can be chained.</returns>
    public TemporalEntityTypeBuilder<TEntity> WithRetention()
    {
        _builder.HasAnnotation(HindsightAnnotationNames.HasRetention, true);
        return this;
    }
}
