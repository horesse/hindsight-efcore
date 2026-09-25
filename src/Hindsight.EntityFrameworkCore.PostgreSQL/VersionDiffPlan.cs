using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight;

/// <summary>
/// What <c>Diff</c> compares for one temporal entity type: every column the history table mirrors
/// (not <c>Exclude(...)</c>-d, and with a history column), nested complex and owned members included
/// (DESIGN.md D9), in model order, plus the primary key used to check that two snapshots belong to the
/// same entity. Built once per model and cached as a runtime annotation (<see cref="HindsightAnnotationNames.DiffPlan"/>).
/// </summary>
internal sealed class VersionDiffPlan
{
    private VersionDiffPlan(IEntityType entityType, VersionedColumn[] columns, IProperty[] keyProperties, string[] shadowProperties)
    {
        EntityType = entityType;
        Columns = columns;
        KeyProperties = keyProperties;
        ShadowProperties = shadowProperties;
    }

    public IEntityType EntityType { get; }

    /// <summary>The versioned columns, in <see cref="VersionedColumns.Collect"/> order.</summary>
    public IReadOnlyList<VersionedColumn> Columns { get; }

    public IReadOnlyList<IProperty> KeyProperties { get; }

    /// <summary>
    /// Versioned shadow properties. The history table records them, but the entity snapshot a history
    /// query reconstructs has no CLR member to carry them (DESIGN.md D12), so a diff over the snapshot
    /// could not see a change to one. Non-empty means <c>Diff</c> throws rather than return a partial answer.
    /// </summary>
    public IReadOnlyList<string> ShadowProperties { get; }

    public static VersionDiffPlan For(IModel model, Type clrType)
    {
        var entityType = model.FindEntityType(clrType);
        if (entityType is null || entityType.FindAnnotation(HindsightAnnotationNames.IsTemporal)?.Value is not true)
        {
            throw new InvalidOperationException(
                $"'{clrType.Name}' is not a temporal entity type in this context's model, so it has no history "
                + "versions to compare. Call IsTemporal() on it in OnModelCreating, or pass instances of a "
                + "temporal entity type.");
        }

        if (entityType[HindsightAnnotationNames.HistoryEntityType] is not string historyTypeName
            || model.FindEntityType(historyTypeName) is not { } historyType)
        {
            throw new InvalidOperationException(
                $"Entity '{entityType.DisplayName()}' is marked IsTemporal() but this context's model has no history "
                + "entity type for it. Call UseHindsight() on the DbContextOptionsBuilder.");
        }

        return entityType.GetOrAddRuntimeAnnotationValue(
            HindsightAnnotationNames.DiffPlan,
            static state => Build(state.EntityType, state.HistoryType),
            (EntityType: entityType, HistoryType: historyType));
    }

    /// <summary>
    /// Reads a column's value off an entity snapshot: through each complex property / owned reference on
    /// its path, null when one of them is null (an unset optional member).
    /// </summary>
    public static object? Read(object entity, VersionedColumn column)
    {
        var holder = entity;
        foreach (var member in column.Path)
        {
            holder = ((IPropertyBase)member).GetGetter().GetClrValue(holder);
            if (holder is null)
            {
                return null;
            }
        }

        return ((IProperty)column.Property).GetGetter().GetClrValue(holder);
    }

    private static VersionDiffPlan Build(IEntityType entityType, IEntityType historyType)
    {
        var columns = new List<VersionedColumn>();
        var shadow = new List<string>();

        // Same selection as the Interceptor writer (TemporalWritePlan): a column is versioned when it is
        // not excluded and mirrored onto the history table.
        foreach (var column in VersionedColumns.Collect(entityType))
        {
            if (historyType.FindProperty(column.Column) is null)
            {
                continue;
            }

            if (column.Property.IsShadowProperty())
            {
                shadow.Add(column.DisplayName);
                continue;
            }

            columns.Add(column);
        }

        // An Exclude()-d key part is never reconstructed on a snapshot (always its CLR default), so it
        // cannot tell two entities apart; compare the key on the parts history actually records.
        // IsTemporal() guarantees at least one.
        var keyProperties = entityType.FindPrimaryKey()!.Properties
            .Where(key => columns.Any(column => ReferenceEquals(column.Property, key)))
            .ToArray();

        return new VersionDiffPlan(entityType, [.. columns], keyProperties, [.. shadow]);
    }
}
