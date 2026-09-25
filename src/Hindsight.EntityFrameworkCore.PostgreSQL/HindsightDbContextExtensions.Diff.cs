using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Hindsight;

public static partial class HindsightDbContextExtensions
{
    /// <summary>
    /// Lists the versioned properties whose values differ between two versions of the same entity read
    /// with <see cref="HindsightQueryableExtensions.History{TEntity}"/> — the "what changed" line of an
    /// audit trail. Pass <see langword="null"/> as <paramref name="older"/> to diff an insert against
    /// nothing: every versioned property comes back, with a <see langword="null"/> old value.
    /// </summary>
    /// <param name="context">The context whose model defines <typeparamref name="TEntity"/>.</param>
    /// <param name="older">The earlier version, or <see langword="null"/> for "nothing before".</param>
    /// <param name="newer">The later version.</param>
    /// <typeparam name="TEntity">The temporal entity type.</typeparam>
    /// <returns>
    /// The changed properties in model order (key first); empty when every versioned value is equal.
    /// Values are compared with each property's EF Core <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer"/>,
    /// so arrays, collections and converted values compare by content. Properties excluded from history
    /// with <c>Exclude(...)</c> are never compared: history does not record them.
    /// </returns>
    /// <remarks>
    /// The Interceptor writer records a version whenever EF Core marks a versioned property as modified,
    /// even if the value is the same, so under that writer an update version can legitimately diff empty
    /// against its predecessor. The Trigger writer skips such no-op updates.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The versions belong to different entities (different keys); <paramref name="older"/> does not
    /// start before <paramref name="newer"/>; or either one is a delete tombstone
    /// (<see cref="VersionOperation.Delete"/>), which records when an entity was deleted, not a state to
    /// compare — read <see cref="Version{TEntity}.Operation"/> for that.
    /// </exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEntity"/> is not temporal in this model.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TEntity"/> has versioned shadow properties. History records them, but the
    /// entity snapshot has no member that carries them, so a change to one would be invisible.
    /// </exception>
    public static IReadOnlyList<PropertyChange> Diff<TEntity>(this DbContext context, Version<TEntity>? older, Version<TEntity> newer)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(newer);

        var plan = GetDiffPlan(context, typeof(TEntity));

        ThrowIfTombstone(newer, nameof(newer));
        if (older is not null)
        {
            ThrowIfTombstone(older, nameof(older));
            if (older.ValidFrom >= newer.ValidFrom)
            {
                throw new ArgumentException(
                    $"The older version of '{plan.EntityType.DisplayName()}' starts at {older.ValidFrom:O}, which is "
                    + $"not before the newer version's start at {newer.ValidFrom:O}. Pass the earlier version as "
                    + "'older' and the later one as 'newer'; History<T>() returns newest first.",
                    nameof(older));
            }
        }

        return Compare(plan, older?.Entity, newer.Entity);
    }

    /// <summary>
    /// Lists the versioned properties whose values differ between two snapshots of the same entity —
    /// for example <c>AsOf(t1)</c> against <c>AsOf(t2)</c>, two <c>AllVersions()</c> rows, or a
    /// historical snapshot against the entity as loaded now. Pass <see langword="null"/> as
    /// <paramref name="older"/> to diff against nothing.
    /// </summary>
    /// <param name="context">The context whose model defines <typeparamref name="TEntity"/>.</param>
    /// <param name="older">The earlier snapshot, or <see langword="null"/> for "nothing before".</param>
    /// <param name="newer">The later snapshot.</param>
    /// <typeparam name="TEntity">The temporal entity type.</typeparam>
    /// <returns>
    /// The changed properties in model order; empty when every versioned value is equal. Compared as
    /// in <see cref="Diff{TEntity}(DbContext, Version{TEntity}?, Version{TEntity})"/>.
    /// </returns>
    /// <remarks>
    /// A bare entity carries no period, so this overload cannot check which snapshot is older: the
    /// result simply reports <paramref name="older"/>'s values as old. Prefer the
    /// <see cref="Version{TEntity}"/> overload when you have versions.
    /// </remarks>
    /// <exception cref="ArgumentException">The snapshots belong to different entities (different keys).</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="TEntity"/> is not temporal in this model.</exception>
    /// <exception cref="NotSupportedException"><typeparamref name="TEntity"/> has versioned shadow properties.</exception>
    public static IReadOnlyList<PropertyChange> Diff<TEntity>(this DbContext context, TEntity? older, TEntity newer)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(newer);

        return Compare(GetDiffPlan(context, typeof(TEntity)), older, newer);
    }

    private static VersionDiffPlan GetDiffPlan(DbContext context, Type clrType)
    {
        var plan = VersionDiffPlan.For(context.Model, clrType);
        if (plan.ShadowProperties.Count > 0)
        {
            throw new NotSupportedException(
                $"Entity '{plan.EntityType.DisplayName()}' has versioned shadow properties "
                + $"({string.Join(", ", plan.ShadowProperties.Select(p => $"'{p}'"))}). History records them, but the "
                + "entity snapshots Hindsight reads back have no CLR member to hold them, so a diff could not see "
                + "a change to one. Map each as a CLR property on the entity, or, if it need not be versioned, "
                + "exclude it from history with Property(\"<name>\").HasAnnotation(HindsightAnnotationNames.IsExcluded, true).");
        }

        return plan;
    }

    private static void ThrowIfTombstone<TEntity>(Version<TEntity> version, string parameterName)
        where TEntity : class
    {
        if (version.Operation == VersionOperation.Delete)
        {
            throw new ArgumentException(
                $"The '{parameterName}' version of '{typeof(TEntity).Name}' is a delete tombstone "
                + "(Operation == VersionOperation.Delete). It records when the entity was deleted and by whom, not "
                + "a state to compare: its values repeat the last version before the delete. Diff the versions "
                + "around it and read the deletion from Operation.",
                parameterName);
        }
    }

    private static PropertyChange[] Compare(VersionDiffPlan plan, object? older, object newer)
    {
        if (older is not null)
        {
            foreach (var key in plan.KeyProperties)
            {
                var getter = key.GetGetter();
                if (!key.GetKeyValueComparer().Equals(
                        getter.GetClrValueUsingContainingEntity(older), getter.GetClrValueUsingContainingEntity(newer)))
                {
                    throw new ArgumentException(
                        $"The two snapshots of '{plan.EntityType.DisplayName()}' belong to different entities: key "
                        + $"property '{key.Name}' is '{getter.GetClrValueUsingContainingEntity(older)}' in the older one "
                        + $"and '{getter.GetClrValueUsingContainingEntity(newer)}' in the newer one. Diff compares two "
                        + "versions of the same entity; group versions by key first.",
                        nameof(older));
                }
            }
        }

        List<PropertyChange>? changes = null;
        foreach (var column in plan.Columns)
        {
            var property = (IProperty)column.Property;
            var newValue = VersionDiffPlan.Read(newer, column);

            if (older is null)
            {
                (changes ??= []).Add(new PropertyChange(property, column.DisplayName, oldValue: null, newValue));
                continue;
            }

            var oldValue = VersionDiffPlan.Read(older, column);
            if (!property.GetValueComparer().Equals(oldValue, newValue))
            {
                (changes ??= []).Add(new PropertyChange(property, column.DisplayName, oldValue, newValue));
            }
        }

        return changes is null ? [] : [.. changes];
    }
}
