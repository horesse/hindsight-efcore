using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace Hindsight.Query;

/// <summary>
/// Rewrites every <c>AsOf(...)</c> marker call in a query tree into a real query over the entity's
/// property-bag history entity type: <c>history.Where(valid_from &lt;= @asOf &amp;&amp; valid_to &gt; @asOf)
/// .Select(h =&gt; new TEntity { ... }).AsNoTracking()</c>. The operators the caller put after
/// <c>AsOf</c> sit on top of that and translate normally (DESIGN.md D12). Uses only public EF Core
/// API — no <c>Microsoft.EntityFrameworkCore.*.Internal</c> (CLAUDE.md rule 1).
/// </summary>
internal sealed class AsOfQueryRootRewriter(IModel model) : ExpressionVisitor
{
    private static readonly MethodInfo _efProperty =
        typeof(EF).GetMethod(nameof(EF.Property))!;

    private static readonly MethodInfo _queryableWhere = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Where)
            && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static readonly MethodInfo _queryableSelect = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Select)
            && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static readonly MethodInfo _asNoTracking = typeof(EntityFrameworkQueryableExtensions).GetMethods()
        .Single(m => m.Name == nameof(EntityFrameworkQueryableExtensions.AsNoTracking)
            && m.GetParameters().Length == 1);

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Method.IsGenericMethod
            && node.Method.GetGenericMethodDefinition() == HindsightQueryableExtensions.AsOfMethod)
        {
            return RewriteAsOf(node);
        }

        return base.VisitMethodCall(node);
    }

    private MethodCallExpression RewriteAsOf(MethodCallExpression node)
    {
        var visitedSource = Visit(node.Arguments[0]);
        var asOfUtc = node.Arguments[1]; // DateTime, member access on a captured object -> EF query parameter
        var entityClrType = node.Method.GetGenericArguments()[0];

        if (visitedSource is not EntityQueryRootExpression root)
        {
            throw new InvalidOperationException(
                $"AsOf() must be the first operator on the query, applied directly to a DbSet<{entityClrType.Name}>. "
                + "Move AsOf() ahead of Where / OrderBy / Select and any other operator.");
        }

        var sourceEntityType = root.EntityType;

        if (sourceEntityType.BaseType is not null || sourceEntityType.GetDirectlyDerivedTypes().Any())
        {
            throw new NotSupportedException(
                $"AsOf() is not supported for '{sourceEntityType.DisplayName()}': it takes part in an inheritance "
                + "hierarchy, which Hindsight does not support in v1 (DESIGN.md D9). Read the history table with FromSql.");
        }

        if (sourceEntityType.FindAnnotation(HindsightAnnotationNames.HistoryEntityType)?.Value is not string historyName)
        {
            throw new InvalidOperationException(
                $"AsOf() requires '{sourceEntityType.DisplayName()}' to be temporal, but it is not. "
                + "Configure it with IsTemporal() in OnModelCreating, or remove the AsOf() call.");
        }

        var historyEntityType = model.FindEntityType(historyName)
            ?? throw new InvalidOperationException(
                $"AsOf(): the history entity type '{historyName}' for '{sourceEntityType.DisplayName()}' is missing "
                + "from the model. This is a bug in Hindsight.");

        // Owned references and complex properties do not have columns on the history table, so a
        // reconstructed entity would carry silent nulls for them (CLAUDE.md rule 2).
        if (sourceEntityType.GetNavigations().Any(n => n.TargetEntityType.IsOwned())
            || sourceEntityType.GetComplexProperties().Any())
        {
            throw new NotSupportedException(
                $"AsOf() is not supported for '{sourceEntityType.DisplayName()}': it has owned or complex members, "
                + "which the history table does not carry. Read the history table with FromSql.");
        }

        var bagType = typeof(Dictionary<string, object>);
        var periodStart = PeriodColumn(
            sourceEntityType,
            HindsightAnnotationNames.PeriodStartColumnName,
            TemporalEntityTypeBuilderExtensions.DefaultPeriodStartColumnName);
        var periodEnd = PeriodColumn(
            sourceEntityType,
            HindsightAnnotationNames.PeriodEndColumnName,
            TemporalEntityTypeBuilderExtensions.DefaultPeriodEndColumnName);

        // h => EF.Property<DateTime>(h, "valid_from") <= @asOf && EF.Property<DateTime>(h, "valid_to") > @asOf
        var predicateParam = Expression.Parameter(bagType, "h");
        var predicate = Expression.Lambda(
            Expression.AndAlso(
                Expression.LessThanOrEqual(Property(predicateParam, typeof(DateTime), periodStart), asOfUtc),
                Expression.GreaterThan(Property(predicateParam, typeof(DateTime), periodEnd), asOfUtc)),
            predicateParam);

        var historyRoot = new EntityQueryRootExpression(historyEntityType);
        var filtered = Expression.Call(
            _queryableWhere.MakeGenericMethod(bagType), historyRoot, Expression.Quote(predicate));

        // h => new TEntity { Prop = EF.Property<TProp>(h, "column"), ... }
        var projectionParam = Expression.Parameter(bagType, "h");
        var projection = Expression.Lambda(
            Expression.MemberInit(Expression.New(entityClrType), BuildBindings(sourceEntityType, projectionParam)),
            projectionParam);
        var projected = Expression.Call(
            _queryableSelect.MakeGenericMethod(bagType, entityClrType), filtered, Expression.Quote(projection));

        // D7: a Select that materialises a mapped entity is tracked unless this is forced.
        return Expression.Call(_asNoTracking.MakeGenericMethod(entityClrType), projected);
    }

    private static List<MemberBinding> BuildBindings(IEntityType sourceEntityType, ParameterExpression bag)
    {
        var bindings = new List<MemberBinding>();

        foreach (var property in sourceEntityType.GetProperties())
        {
            if (property.FindAnnotation(HindsightAnnotationNames.IsExcluded)?.Value is true
                || property.IsShadowProperty())
            {
                continue;
            }

            if (property.PropertyInfo is not { SetMethod: not null } member)
            {
                throw new NotSupportedException(
                    $"AsOf() cannot reconstruct '{sourceEntityType.DisplayName()}': property '{property.Name}' has "
                    + "no setter. v1 requires settable properties on entities read through AsOf().");
            }

            if (property.GetColumnName() is { } column)
            {
                bindings.Add(Expression.Bind(member, Property(bag, property.ClrType, column)));
            }
        }

        if (bindings.Count == 0)
        {
            throw new NotSupportedException(
                $"AsOf() found no settable persisted properties on '{sourceEntityType.DisplayName()}'.");
        }

        return bindings;
    }

    private static string PeriodColumn(IReadOnlyEntityType source, string annotationName, string fallback)
        => source.FindAnnotation(annotationName)?.Value as string ?? fallback;

    private static MethodCallExpression Property(Expression bag, Type clrType, string name)
        => Expression.Call(_efProperty.MakeGenericMethod(clrType), bag, Expression.Constant(name));
}
