using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight;

/// <summary>
/// What <c>Diff</c> compares for one temporal entity type: every property the history table mirrors
/// (not <c>Exclude(...)</c>-d, and with a history column), in model order, plus the primary key used to
/// check that two snapshots belong to the same entity. Built once per model and cached as a runtime
/// annotation (<see cref="HindsightAnnotationNames.DiffPlan"/>).
/// </summary>
internal sealed class VersionDiffPlan
{
    private VersionDiffPlan(IEntityType entityType, IProperty[] properties, IProperty[] keyProperties, string[] shadowProperties)
    {
        EntityType = entityType;
        Properties = properties;
        KeyProperties = keyProperties;
        ShadowProperties = shadowProperties;
    }

    public IEntityType EntityType { get; }

    /// <summary>The versioned properties, in the order <see cref="IEntityType.GetProperties"/> returns them.</summary>
    public IReadOnlyList<IProperty> Properties { get; }

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

    private static VersionDiffPlan Build(IEntityType entityType, IEntityType historyType)
    {
        var properties = new List<IProperty>();
        var shadow = new List<string>();

        foreach (var property in entityType.GetProperties())
        {
            // Same selection as the Interceptor writer (HistoryRowPlan): a property is versioned when it
            // is not excluded and its column is mirrored onto the history table.
            if (property.FindAnnotation(HindsightAnnotationNames.IsExcluded)?.Value is true
                || property.GetColumnName() is not { } column
                || historyType.FindProperty(column) is null)
            {
                continue;
            }

            if (property.IsShadowProperty())
            {
                shadow.Add(property.Name);
                continue;
            }

            properties.Add(property);
        }

        // An Exclude()-d key part is never reconstructed on a snapshot (always its CLR default), so it
        // cannot tell two entities apart; compare the key on the parts history actually records.
        // IsTemporal() guarantees at least one.
        var keyProperties = entityType.FindPrimaryKey()!.Properties.Where(properties.Contains).ToArray();

        return new VersionDiffPlan(entityType, [.. properties], keyProperties, [.. shadow]);
    }
}
