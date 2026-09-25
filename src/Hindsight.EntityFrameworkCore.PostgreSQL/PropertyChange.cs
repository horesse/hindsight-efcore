using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight;

/// <summary>
/// One versioned property whose value differs between two versions of a temporal entity, as returned
/// by <see cref="HindsightDbContextExtensions.Diff{TEntity}(Microsoft.EntityFrameworkCore.DbContext, Version{TEntity}?, Version{TEntity})"/>.
/// Use it to render an audit screen ("Status: Draft → Active") without comparing snapshots by hand.
/// </summary>
/// <remarks>
/// Values are the entity's CLR (model) values, not provider values: an enum stored as text comes back as
/// the enum, a <c>jsonb</c> string as the string. Who made the change and when is not repeated here; read
/// it from the <see cref="Version{TEntity}"/> you passed in. A member of a complex property or an owned
/// reference is reported property by property, with <see cref="Path"/> naming where it sits.
/// </remarks>
public sealed class PropertyChange
{
    internal PropertyChange(IProperty property, string path, object? oldValue, object? newValue)
    {
        Property = property;
        Path = path;
        OldValue = oldValue;
        NewValue = newValue;
    }

    /// <summary>
    /// The EF Core property that changed. <see cref="IReadOnlyPropertyBase.Name"/> is the CLR property
    /// name; the metadata also gives its type, column name and any annotations an audit UI keys labels on.
    /// For a member of a complex property or an owned reference, its declaring type is that complex or
    /// owned type.
    /// </summary>
    public IProperty Property { get; }

    /// <summary>
    /// The property's path from the entity, dot-separated: <c>"Status"</c> for the entity's own property,
    /// <c>"Address.City"</c> for a member of the <c>Address</c> complex property or owned reference. Use it
    /// as the label or key of a change when the entity has nested members, since two of them can declare
    /// properties with the same <see cref="IReadOnlyPropertyBase.Name"/>.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// The value in the older version; <see langword="null"/> when the older version was
    /// <see langword="null"/> (the diff of an insert against nothing), the value itself was null, or the
    /// optional complex property / owned reference holding it was null.
    /// </summary>
    public object? OldValue { get; }

    /// <summary>The value in the newer version; <see langword="null"/> in the same cases as <see cref="OldValue"/>.</summary>
    public object? NewValue { get; }
}
