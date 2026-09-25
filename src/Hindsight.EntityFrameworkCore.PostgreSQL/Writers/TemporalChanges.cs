using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight.Writers;

/// <summary>
/// Finds the temporal entities a <c>SaveChanges</c> versions, from the change tracker's public API.
/// Besides the temporal entries themselves this covers the two nested shapes of DESIGN.md D9: a change to
/// a complex property is tracked on the owner's own entry (and marks it <c>Modified</c>), but a change to
/// an owned reference is tracked on the owned type's own entry while the owner stays <c>Unchanged</c> — so
/// the owner is found through the owned entry's key, which for a table-split owned reference is the
/// owner's key.
/// </summary>
internal static class TemporalChanges
{
    /// <summary>
    /// Every temporal entry that gets a history row, with the state it is recorded as: <c>Added</c>,
    /// <c>Deleted</c>, or <c>Modified</c> when a versioned property, a versioned member of a complex
    /// property, or an owned reference changed. Callers disable <c>AutoDetectChangesEnabled</c> around it.
    /// </summary>
    public static List<(EntityEntry Entry, EntityState State)> Collect(ChangeTracker tracker)
    {
        var changes = new List<(EntityEntry Entry, EntityState State)>();
        HashSet<EntityEntry>? versioned = null;
        Dictionary<OwnerKey, EntityEntry>? owners = null;
        List<(IEntityType Root, EntityEntry Entry)>? ownedChanges = null;

        foreach (var entry in tracker.Entries())
        {
            var entityType = entry.Metadata;
            if (entityType.FindAnnotation(HindsightAnnotationNames.IsTemporal)?.Value is true)
            {
                if (TemporalWritePlan.For(entityType) is { HasOwnedReferences: true })
                {
                    (owners ??= []).TryAdd(OwnerKey.For(entityType, entry, entityType), entry);
                }

                if (entry.State is EntityState.Added or EntityState.Deleted
                    || (entry.State == EntityState.Modified && HasVersionedModification(entry)))
                {
                    changes.Add((entry, entry.State));
                    (versioned ??= new HashSet<EntityEntry>(ReferenceEqualityComparer.Instance)).Add(entry);
                }

                continue;
            }

            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && entityType.IsOwned()
                && VersionedColumns.FindTemporalOwnerRoot(entityType) is IEntityType root)
            {
                (ownedChanges ??= []).Add((root, entry));
            }
        }

        if (ownedChanges is null)
        {
            return changes;
        }

        foreach (var (root, ownedEntry) in ownedChanges)
        {
            if (owners is null || !owners.TryGetValue(OwnerKey.For(root, ownedEntry, ownedEntry.Metadata), out var owner))
            {
                // A table-split owned entity cannot be tracked without its owner; if it ever is, writing no
                // history row for the change would be the silent-wrong outcome golden rule 2 forbids.
                throw new InvalidOperationException(
                    $"Hindsight found a change to owned '{ownedEntry.Metadata.DisplayName()}' but its temporal owner "
                    + $"'{root.DisplayName()}' is not tracked by this context, so it cannot record the new version "
                    + "(DESIGN.md D9). Load or attach the owner before saving the owned entity.");
            }

            if ((versioned ??= new HashSet<EntityEntry>(ReferenceEqualityComparer.Instance)).Add(owner))
            {
                // The owner itself is Unchanged (or Modified only in excluded properties): its owned
                // reference is part of its versioned state, so the change is an update of the owner.
                changes.Add((owner, EntityState.Modified));
            }
        }

        return changes;
    }

    /// <summary>
    /// Whether any temporal entity would get a history row: a temporal entry being added, modified or
    /// deleted, or an owned entry of a temporal owner being so. Cheaper than <see cref="Collect"/> and
    /// deliberately conservative (a <c>Modified</c> entry counts even if only excluded properties changed).
    /// </summary>
    public static bool Any(ChangeTracker tracker)
    {
        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var entityType = entry.Metadata;
            if (entityType.FindAnnotation(HindsightAnnotationNames.IsTemporal)?.Value is true
                || (entityType.IsOwned() && VersionedColumns.FindTemporalOwnerRoot(entityType) is not null))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="entry"/> is an owned entry, added, modified or deleted, of a temporal owner.</summary>
    public static bool IsChangedOwnedOfTemporal(EntityEntry entry)
        => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && entry.Metadata.IsOwned()
            && VersionedColumns.FindTemporalOwnerRoot(entry.Metadata) is not null;

    // DESIGN.md D5 / configuration.md: a SaveChanges that touched only excluded properties writes no
    // history row. A complex property's members are tracked on the owner's entry, one level at a time.
    private static bool HasVersionedModification(EntityEntry entry)
    {
        foreach (var property in entry.Properties)
        {
            if (property.IsModified && !IsExcluded(property.Metadata))
            {
                return true;
            }
        }

        foreach (var complex in entry.ComplexProperties)
        {
            if (HasVersionedModification(complex))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasVersionedModification(ComplexPropertyEntry complex)
    {
        if (!complex.IsModified)
        {
            return false;
        }

        foreach (var property in complex.Properties)
        {
            if (property.IsModified && !IsExcluded(property.Metadata))
            {
                return true;
            }
        }

        foreach (var nested in complex.ComplexProperties)
        {
            if (HasVersionedModification(nested))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExcluded(IReadOnlyProperty property)
        => property.FindAnnotation(HindsightAnnotationNames.IsExcluded)?.Value is true;

    // The root entity type plus its primary-key values. A table-split owned reference's key is the
    // ownership foreign key, which maps position by position onto its owner's key — at every level of
    // nesting — so an owned entry's own key values are its root owner's key values.
    private readonly struct OwnerKey(IEntityType root, object?[] values) : IEquatable<OwnerKey>
    {
        public static OwnerKey For(IEntityType root, EntityEntry entry, IEntityType keyType)
        {
            var keyProperties = keyType.FindPrimaryKey()!.Properties;
            var values = new object?[keyProperties.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = entry.CurrentValues[keyProperties[i]];
            }

            return new OwnerKey(root, values);
        }

        public bool Equals(OwnerKey other)
        {
            if (!ReferenceEquals(root, other.Root) || values.Length != other.Values.Length)
            {
                return false;
            }

            for (var i = 0; i < values.Length; i++)
            {
                if (!Equals(values[i], other.Values[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is OwnerKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(root);
            foreach (var value in values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }

        private IEntityType Root => root;

        private object?[] Values => values;
    }
}
