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
/// it from the <see cref="Version{TEntity}"/> you passed in.
/// </remarks>
public sealed class PropertyChange
{
    internal PropertyChange(IProperty property, object? oldValue, object? newValue)
    {
        Property = property;
        OldValue = oldValue;
        NewValue = newValue;
    }

    /// <summary>
    /// The EF Core property that changed. <see cref="IReadOnlyPropertyBase.Name"/> is the CLR property
    /// name; the metadata also gives its type, column name and any annotations an audit UI keys labels on.
    /// </summary>
    public IProperty Property { get; }

    /// <summary>
    /// The value in the older version; <see langword="null"/> when the older version was
    /// <see langword="null"/> (the diff of an insert against nothing) or the value itself was null.
    /// </summary>
    public object? OldValue { get; }

    /// <summary>The value in the newer version.</summary>
    public object? NewValue { get; }
}
