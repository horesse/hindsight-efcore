using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight;

/// <summary>
/// One column a temporal entity's history table mirrors: a scalar property reached from the entity
/// through zero or more table-split complex properties or owned references (DESIGN.md D9).
/// </summary>
/// <param name="Column">The column name, identical on the main table and the history table.</param>
/// <param name="Property">The scalar property mapped to the column; declared on the entity, a complex type
/// or an owned entity type.</param>
/// <param name="Path">The complex properties (<see cref="IReadOnlyComplexProperty"/>) and owned reference
/// navigations (<see cref="IReadOnlyNavigation"/>) leading from the entity to the declaring type of
/// <paramref name="Property"/>, outermost first. Empty for the entity's own properties.</param>
internal sealed record VersionedColumn(string Column, IReadOnlyProperty Property, IReadOnlyList<IReadOnlyPropertyBase> Path)
{
    /// <summary>Whether the column belongs to a complex property or an owned reference.</summary>
    public bool IsNested => Path.Count > 0;

    /// <summary>The property's path from the entity for messages, e.g. <c>Address.City</c>.</summary>
    public string DisplayName => IsNested
        ? string.Join('.', Path.Select(member => member.Name)) + "." + Property.Name
        : Property.Name;
}

/// <summary>
/// The one place that decides which columns of a temporal entity are versioned, shared by the
/// history-table convention, both writers' helpers, the query rewriter and <c>Diff</c> (DESIGN.md D9).
/// Walks the entity's own properties, then its table-split complex properties (recursively) and owned
/// references (recursively), in model order. Public metadata API only.
/// </summary>
internal static class VersionedColumns
{
    /// <summary>
    /// Every column of <paramref name="entityType"/> that history mirrors: mapped to a column, not
    /// <c>Exclude(...)</c>-d, and — for an owned reference — not one of its key properties, which share the
    /// owner's key columns. A column name reached twice (table-splitting column sharing) is returned once.
    /// Call <see cref="FindUnsupportedMember"/> first: this method assumes every nested member is table-split.
    /// </summary>
    public static List<VersionedColumn> Collect(IReadOnlyEntityType entityType)
    {
        var columns = new List<VersionedColumn>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var table = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
        CollectEntity(entityType, [], isOwned: false, table, columns, seen);
        return columns;
    }

    /// <summary>
    /// Describes the first complex property or owned navigation whose mapping history cannot carry, or
    /// <see langword="null"/> when every nested member is supported. Supported: table-split complex
    /// properties (nested, optional) and table-split owned references (nested). Not supported: owned
    /// collections, complex collections, JSON-mapped members (<c>ToJson()</c>) and owned references
    /// mapped to a table of their own.
    /// </summary>
    public static string? FindUnsupportedMember(IReadOnlyEntityType entityType)
        => FindUnsupportedInEntity(entityType, entityType.GetTableName(), entityType.GetSchema(), entityType.DisplayName());

    /// <summary>Whether the entity reaches any owned reference, at any depth.</summary>
    public static bool HasOwnedReferences(IReadOnlyEntityType entityType)
        => OwnedReferences(entityType).Any();

    /// <summary>
    /// For an owned entity type, the non-owned entity type at the top of its ownership chain when that
    /// type is temporal; otherwise <see langword="null"/>.
    /// </summary>
    public static IReadOnlyEntityType? FindTemporalOwnerRoot(IReadOnlyEntityType entityType)
    {
        var current = entityType;
        while (current.FindOwnership() is { } ownership)
        {
            current = ownership.PrincipalEntityType;
        }

        return ReferenceEquals(current, entityType)
            || current.FindAnnotation(HindsightAnnotationNames.IsTemporal)?.Value is not true
                ? null
                : current;
    }

    /// <summary>The owned reference navigations declared directly on <paramref name="entityType"/>.</summary>
    public static IEnumerable<IReadOnlyNavigation> OwnedReferences(IReadOnlyEntityType entityType)
        => entityType.GetNavigations().Where(n => n.ForeignKey.IsOwnership && !n.IsOnDependent && !n.IsCollection);

    private static void CollectEntity(
        IReadOnlyEntityType entityType,
        IReadOnlyList<IReadOnlyPropertyBase> path,
        bool isOwned,
        StoreObjectIdentifier? table,
        List<VersionedColumn> columns,
        HashSet<string> seen)
    {
        foreach (var property in entityType.GetProperties())
        {
            // An owned reference's key is the ownership foreign key: the same column(s) as its owner's key.
            if (isOwned && property.IsPrimaryKey())
            {
                continue;
            }

            Add(property, path, table, columns, seen);
        }

        CollectComplex(entityType, path, table, columns, seen);

        foreach (var navigation in OwnedReferences(entityType))
        {
            CollectEntity(navigation.TargetEntityType, [.. path, navigation], isOwned: true, table, columns, seen);
        }
    }

    private static void CollectComplex(
        IReadOnlyTypeBase declaringType,
        IReadOnlyList<IReadOnlyPropertyBase> path,
        StoreObjectIdentifier? table,
        List<VersionedColumn> columns,
        HashSet<string> seen)
    {
        foreach (var complexProperty in declaringType.GetComplexProperties())
        {
            var complexPath = (IReadOnlyList<IReadOnlyPropertyBase>)[.. path, complexProperty];
            foreach (var property in complexProperty.ComplexType.GetProperties())
            {
                Add(property, complexPath, table, columns, seen);
            }

            CollectComplex(complexProperty.ComplexType, complexPath, table, columns, seen);
        }
    }

    private static void Add(
        IReadOnlyProperty property,
        IReadOnlyList<IReadOnlyPropertyBase> path,
        StoreObjectIdentifier? table,
        List<VersionedColumn> columns,
        HashSet<string> seen)
    {
        // A nested member's column name is only final per table: GetColumnName() is its base name ("City"),
        // the table overload adds the member prefix EF gives table-split members ("Address_City"). The
        // entity's own properties keep GetColumnName(), which every history table has used so far.
        var column = path.Count > 0 && table is { } storeObject
            ? property.GetColumnName(storeObject)
            : property.GetColumnName();
        if (property.FindAnnotation(HindsightAnnotationNames.IsExcluded)?.Value is true
            || column is null
            || !seen.Add(column))
        {
            return;
        }

        columns.Add(new VersionedColumn(column, property, path));
    }

    private static string? FindUnsupportedInEntity(IReadOnlyEntityType entityType, string? table, string? schema, string path)
    {
        if (FindUnsupportedComplex(entityType, path) is { } complexProblem)
        {
            return complexProblem;
        }

        foreach (var navigation in entityType.GetNavigations())
        {
            if (!navigation.ForeignKey.IsOwnership || navigation.IsOnDependent)
            {
                continue;
            }

            var memberPath = path + "." + navigation.Name;
            var target = navigation.TargetEntityType;
            if (navigation.IsCollection)
            {
                return $"'{memberPath}' is an owned collection (OwnsMany)";
            }

            if (target.IsMappedToJson())
            {
                return $"'{memberPath}' is an owned reference mapped to JSON (ToJson())";
            }

            if (!string.Equals(target.GetTableName(), table, StringComparison.Ordinal)
                || !string.Equals(target.GetSchema(), schema, StringComparison.Ordinal))
            {
                return $"'{memberPath}' is an owned reference mapped to a table of its own";
            }

            if (FindUnsupportedInEntity(target, table, schema, memberPath) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static string? FindUnsupportedComplex(IReadOnlyTypeBase declaringType, string path)
    {
        foreach (var complexProperty in declaringType.GetComplexProperties())
        {
            var memberPath = path + "." + complexProperty.Name;
            if (complexProperty.IsCollection)
            {
                return $"'{memberPath}' is a complex collection";
            }

            if (complexProperty.ComplexType.IsMappedToJson())
            {
                return $"'{memberPath}' is a complex property mapped to JSON (ToJson())";
            }

            if (FindUnsupportedComplex(complexProperty.ComplexType, memberPath) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
