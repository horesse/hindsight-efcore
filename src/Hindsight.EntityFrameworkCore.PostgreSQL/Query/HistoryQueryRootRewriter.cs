using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace Hindsight.Query;

/// <summary>
/// Rewrites every <c>AsOf(...)</c> / <c>AllVersions()</c> marker call in a query tree into a real
/// query over the entity's property-bag history entity type. Both markers share the same shape —
/// <c>historySource.Select(h =&gt; new TEntity { ... }).AsNoTracking()</c> — and differ only in how the
/// history source is filtered and ordered:
/// <list type="bullet">
/// <item><c>AsOf</c>: <c>history.Where(valid_from &lt;= @asOf &amp;&amp; valid_to &gt; @asOf)</c>.</item>
/// <item><c>AllVersions</c>: <c>history.Where(operation &lt;&gt; 3).OrderByDescending(valid_from)</c>
/// (no period predicate; the <c>delete</c> tombstone is not a state version).</item>
/// </list>
/// The operators the caller put after the marker sit on top of that and translate normally
/// (DESIGN.md D12). Uses only public EF Core API — no <c>Microsoft.EntityFrameworkCore.*.Internal</c>
/// (CLAUDE.md rule 1).
/// </summary>
internal sealed class HistoryQueryRootRewriter(IModel model) : ExpressionVisitor
{
    private static readonly MethodInfo _efProperty =
        typeof(EF).GetMethod(nameof(EF.Property))!;

    private static readonly MethodInfo _queryableWhere = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Where)
            && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static readonly MethodInfo _queryableOrderByDescending = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.OrderByDescending)
            && m.GetParameters().Length == 2);

    private static readonly MethodInfo _queryableSelect = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Select)
            && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static readonly MethodInfo _asNoTracking = typeof(EntityFrameworkQueryableExtensions).GetMethods()
        .Single(m => m.Name == nameof(EntityFrameworkQueryableExtensions.AsNoTracking)
            && m.GetParameters().Length == 1);

    // The delete tombstone (DESIGN.md D5): operation = 3, empty interval [ts, ts). Not a state version.
    // 'internal' rather than 'private' only to satisfy the repo's private-field naming rule, which
    // expects a leading underscore.
    internal const short DeleteOperation = 3;

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Method.IsGenericMethod)
        {
            var definition = node.Method.GetGenericMethodDefinition();

            if (definition == HindsightQueryableExtensions.AsOfMethod)
            {
                return RewriteMarker(node, HistoryReadKind.AsOf);
            }

            if (definition == HindsightQueryableExtensions.AllVersionsMethod)
            {
                return RewriteMarker(node, HistoryReadKind.AllVersions);
            }
        }

        return base.VisitMethodCall(node);
    }

    private MethodCallExpression RewriteMarker(MethodCallExpression node, HistoryReadKind kind)
    {
        var operatorName = kind == HistoryReadKind.AsOf ? "AsOf()" : "AllVersions()";
        var visitedSource = Visit(node.Arguments[0]);
        var entityClrType = node.Method.GetGenericArguments()[0];

        if (visitedSource is not EntityQueryRootExpression root)
        {
            throw new InvalidOperationException(
                $"{operatorName} must be the first operator on the query, applied directly to a DbSet<{entityClrType.Name}>. "
                + $"Move {operatorName} ahead of Where / OrderBy / Select and any other operator.");
        }

        var sourceEntityType = root.EntityType;

        if (sourceEntityType.BaseType is not null || sourceEntityType.GetDirectlyDerivedTypes().Any())
        {
            throw new NotSupportedException(
                $"{operatorName} is not supported for '{sourceEntityType.DisplayName()}': it takes part in an inheritance "
                + "hierarchy, which Hindsight does not support in v1 (DESIGN.md D9). Read the history table with FromSql.");
        }

        if (sourceEntityType.FindAnnotation(HindsightAnnotationNames.HistoryEntityType)?.Value is not string historyName)
        {
            throw new InvalidOperationException(
                $"{operatorName} requires '{sourceEntityType.DisplayName()}' to be temporal, but it is not. "
                + $"Configure it with IsTemporal() in OnModelCreating, or remove the {operatorName} call.");
        }

        var historyEntityType = model.FindEntityType(historyName)
            ?? throw new InvalidOperationException(
                $"{operatorName}: the history entity type '{historyName}' for '{sourceEntityType.DisplayName()}' is missing "
                + "from the model. This is a bug in Hindsight.");

        // Owned references and complex properties do not have columns on the history table, so a
        // reconstructed entity would carry silent nulls for them (CLAUDE.md rule 2).
        if (sourceEntityType.GetNavigations().Any(n => n.TargetEntityType.IsOwned())
            || sourceEntityType.GetComplexProperties().Any())
        {
            throw new NotSupportedException(
                $"{operatorName} is not supported for '{sourceEntityType.DisplayName()}': it has owned or complex members, "
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

        var historyRoot = new EntityQueryRootExpression(historyEntityType);
        var historySource = kind == HistoryReadKind.AsOf
            ? AsOfSource(historyRoot, bagType, periodStart, periodEnd, node.Arguments[1])
            : AllVersionsSource(historyRoot, bagType, periodStart);

        // h => new TEntity { Prop = EF.Property<TProp>(h, "column"), ... }
        var projectionParam = Expression.Parameter(bagType, "h");
        var projection = Expression.Lambda(
            Expression.MemberInit(Expression.New(entityClrType), BuildBindings(sourceEntityType, projectionParam, operatorName)),
            projectionParam);
        var projected = Expression.Call(
            _queryableSelect.MakeGenericMethod(bagType, entityClrType), historySource, Expression.Quote(projection));

        // D7: a Select that materialises a mapped entity is tracked unless this is forced.
        return Expression.Call(_asNoTracking.MakeGenericMethod(entityClrType), projected);
    }

    // history.Where(EF.Property<DateTime>(h, "valid_from") <= @asOf && EF.Property<DateTime>(h, "valid_to") > @asOf)
    private static MethodCallExpression AsOfSource(
        EntityQueryRootExpression historyRoot,
        Type bagType,
        string periodStart,
        string periodEnd,
        Expression asOfUtc) // DateTime, member access on a captured object -> EF query parameter
    {
        var param = Expression.Parameter(bagType, "h");
        var predicate = Expression.Lambda(
            Expression.AndAlso(
                Expression.LessThanOrEqual(Property(param, typeof(DateTime), periodStart), asOfUtc),
                Expression.GreaterThan(Property(param, typeof(DateTime), periodEnd), asOfUtc)),
            param);

        return Expression.Call(
            _queryableWhere.MakeGenericMethod(bagType), historyRoot, Expression.Quote(predicate));
    }

    // history.Where(EF.Property<short>(h, "operation") != 3).OrderByDescending(h => EF.Property<DateTime>(h, "valid_from"))
    private static MethodCallExpression AllVersionsSource(
        EntityQueryRootExpression historyRoot,
        Type bagType,
        string periodStart)
    {
        var filterParam = Expression.Parameter(bagType, "h");
        var notTombstone = Expression.Lambda(
            Expression.NotEqual(
                Property(filterParam, typeof(short), HindsightHistoryColumns.Operation),
                Expression.Constant(DeleteOperation)),
            filterParam);
        var filtered = Expression.Call(
            _queryableWhere.MakeGenericMethod(bagType), historyRoot, Expression.Quote(notTombstone));

        var orderParam = Expression.Parameter(bagType, "h");
        var validFrom = Expression.Lambda(Property(orderParam, typeof(DateTime), periodStart), orderParam);

        return Expression.Call(
            _queryableOrderByDescending.MakeGenericMethod(bagType, typeof(DateTime)),
            filtered,
            Expression.Quote(validFrom));
    }

    private static List<MemberBinding> BuildBindings(IEntityType sourceEntityType, ParameterExpression bag, string operatorName)
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
                    $"{operatorName} cannot reconstruct '{sourceEntityType.DisplayName()}': property '{property.Name}' has "
                    + $"no setter. v1 requires settable properties on entities read through {operatorName}.");
            }

            if (property.GetColumnName() is { } column)
            {
                bindings.Add(Expression.Bind(member, Property(bag, property.ClrType, column)));
            }
        }

        if (bindings.Count == 0)
        {
            throw new NotSupportedException(
                $"{operatorName} found no settable persisted properties on '{sourceEntityType.DisplayName()}'.");
        }

        return bindings;
    }

    private static string PeriodColumn(IReadOnlyEntityType source, string annotationName, string fallback)
        => source.FindAnnotation(annotationName)?.Value as string ?? fallback;

    private static MethodCallExpression Property(Expression bag, Type clrType, string name)
        => Expression.Call(_efProperty.MakeGenericMethod(clrType), bag, Expression.Constant(name));

    private enum HistoryReadKind
    {
        AsOf,
        AllVersions,
    }
}
