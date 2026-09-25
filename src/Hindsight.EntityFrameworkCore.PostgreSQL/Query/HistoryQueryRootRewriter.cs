using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using NpgsqlTypes;

namespace Hindsight.Query;

/// <summary>
/// Rewrites every <c>AsOf(...)</c> / <c>AllVersions()</c> / <c>FromTo(...)</c> / <c>ContainedIn(...)</c> /
/// <c>History&lt;T&gt;()</c> marker call in a query tree into a real query over the entity's property-bag
/// history entity type. All of them end in
/// <c>historySource.Select(h =&gt; …).AsNoTracking()</c> and differ in how the history source is
/// filtered and ordered and in what the projection produces:
/// <list type="bullet">
/// <item><c>AsOf</c>: <c>history.Where(valid_from &lt;= @asOf &amp;&amp; valid_to &gt; @asOf)</c>,
/// projected to <c>new TEntity { … }</c>.</item>
/// <item><c>AllVersions</c>: <c>history.Where(operation &lt;&gt; 3).OrderByDescending(valid_from)</c>
/// (no period predicate; the <c>delete</c> tombstone is not a state version), projected to
/// <c>new TEntity { … }</c>.</item>
/// <item><c>FromTo</c> / <c>ContainedIn</c>: the <c>AllVersions</c> source with
/// <c>tstzrange(valid_from, valid_to) &amp;&amp; tstzrange(@from, @to)</c> (resp. <c>&lt;@</c>) added to its
/// filter (DESIGN.md D18), projected to <c>new TEntity { … }</c>.</item>
/// <item><c>History&lt;T&gt;</c>: <c>history.OrderByDescending(valid_from).ThenByDescending(history_id)</c>
/// (no filter at all — the tombstone is the delete audit), projected to
/// <c>new Version&lt;TEntity&gt; { Entity = new TEntity { … }, ValidFrom = …, Operation = …, … }</c>.</item>
/// </list>
/// The operators the caller put after the marker sit on top of that and translate normally
/// (DESIGN.md D12). Uses only public EF Core API — no <c>Microsoft.EntityFrameworkCore.*.Internal</c> —
/// so this stays working across EF Core minor releases instead of breaking on an internal API change.
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

    private static readonly MethodInfo _queryableThenByDescending = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.ThenByDescending)
            && m.GetParameters().Length == 2);

    private static readonly MethodInfo _queryableSelect = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Select)
            && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static readonly MethodInfo _asNoTracking = typeof(EntityFrameworkQueryableExtensions).GetMethods()
        .Single(m => m.Name == nameof(EntityFrameworkQueryableExtensions.AsNoTracking)
            && m.GetParameters().Length == 1);

    // Public Npgsql range operators (NpgsqlRangeDbFunctionsExtensions), closed over DateTime: && and <@.
    private static readonly MethodInfo _rangeOverlaps = RangeOperator(nameof(NpgsqlRangeDbFunctionsExtensions.Overlaps));

    private static readonly MethodInfo _rangeContainedBy = RangeOperator(nameof(NpgsqlRangeDbFunctionsExtensions.ContainedBy));

    // The delete tombstone (DESIGN.md D5): operation = 3, empty interval [ts, ts). Not a state version.
    private const short DeleteOperation = 3;

    // The AsNoTracking(...) node produced for each rewritten marker (by reference) -> the CLR type it
    // yields (TEntity for AsOf / AllVersions, Version<TEntity> for History<T>). Lets the caller
    // (HindsightQueryExpressionInterceptor) tell a query whose result is a reconstructed history
    // entity from one that merely uses a history query in a subquery, so it can tag the former for
    // the save-back guard (DESIGN.md D7) without a wrapper inside the projection.
    private readonly Dictionary<Expression, Type> _rewrittenMarkers = new(ReferenceEqualityComparer.Instance);

    /// <summary>The rewritten <c>AsOf</c> / <c>AllVersions</c> / <c>History&lt;T&gt;</c> nodes in the visited
    /// tree, mapped to the CLR type each yields.</summary>
    public IReadOnlyDictionary<Expression, Type> RewrittenMarkers => _rewrittenMarkers;

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

            if (definition == HindsightQueryableExtensions.FromToMethod)
            {
                return RewriteMarker(node, HistoryReadKind.FromTo);
            }

            if (definition == HindsightQueryableExtensions.ContainedInMethod)
            {
                return RewriteMarker(node, HistoryReadKind.ContainedIn);
            }

            if (definition == HindsightQueryableExtensions.HistoryMethod)
            {
                return RewriteMarker(node, HistoryReadKind.History);
            }
        }

        return base.VisitMethodCall(node);
    }

    private MethodCallExpression RewriteMarker(MethodCallExpression node, HistoryReadKind kind)
    {
        var operatorName = kind switch
        {
            HistoryReadKind.AsOf => "AsOf()",
            HistoryReadKind.AllVersions => "AllVersions()",
            HistoryReadKind.FromTo => "FromTo()",
            HistoryReadKind.ContainedIn => "ContainedIn()",
            _ => "History<T>()",
        };
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
                + "hierarchy, which Hindsight does not support (DESIGN.md D9). Read the history table with FromSql.");
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

        // Only table-split complex properties and owned references have columns on the history table
        // (DESIGN.md D9); for any other nested shape a reconstructed entity would carry silent nulls
        // instead of its real values. IsTemporal() already rejects these shapes at model finalization.
        if (VersionedColumns.FindUnsupportedMember(sourceEntityType) is { } unsupported)
        {
            throw new NotSupportedException(
                $"{operatorName} is not supported for '{sourceEntityType.DisplayName()}': {unsupported}, which the "
                + "history table does not carry (DESIGN.md D9). Read the history table with FromSql.");
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
        var historySource = kind switch
        {
            HistoryReadKind.AsOf => AsOfSource(historyRoot, bagType, periodStart, periodEnd, node.Arguments[1]),
            HistoryReadKind.AllVersions => AllVersionsSource(historyRoot, bagType, periodStart, periodEnd, range: null),
            HistoryReadKind.FromTo => AllVersionsSource(
                historyRoot, bagType, periodStart, periodEnd, (_rangeOverlaps, node.Arguments[1], node.Arguments[2])),
            HistoryReadKind.ContainedIn => AllVersionsSource(
                historyRoot, bagType, periodStart, periodEnd, (_rangeContainedBy, node.Arguments[1], node.Arguments[2])),
            _ => HistorySource(historyRoot, bagType, periodStart),
        };

        // h => new TEntity { Prop = EF.Property<TProp>(h, "column"), ... }
        var projectionParam = Expression.Parameter(bagType, "h");
        var entityInit = Expression.MemberInit(
            Expression.New(entityClrType), BuildBindings(sourceEntityType, projectionParam, operatorName));

        // AsOf / AllVersions project the entity itself; History<T> wraps it in Version<TEntity> and
        // adds the period + change-context columns as top-level members.
        var (resultClrType, projectionBody) = kind == HistoryReadKind.History
            ? WrapInVersion(entityClrType, entityInit, projectionParam, periodStart, periodEnd, historyEntityType)
            : (entityClrType, (Expression)entityInit);

        var projection = Expression.Lambda(projectionBody, projectionParam);
        var projected = Expression.Call(
            _queryableSelect.MakeGenericMethod(bagType, resultClrType), historySource, Expression.Quote(projection));

        // D7: a Select that materialises a mapped entity (or one nested in Version<TEntity>) is tracked
        // unless this is forced.
        var rewritten = Expression.Call(_asNoTracking.MakeGenericMethod(resultClrType), projected);
        _rewrittenMarkers[rewritten] = resultClrType;
        return rewritten;
    }

    // h => new Version<TEntity>
    // {
    //     Entity = new TEntity { ... },
    //     ValidFrom = (DateTimeOffset)EF.Property<DateTime>(h, "valid_from"),
    //     ValidTo = (DateTimeOffset)EF.Property<DateTime>(h, "valid_to"),
    //     Operation = (VersionOperation)EF.Property<short>(h, "operation"),
    //     ChangedBy = EF.Property<string>(h, "changed_by"), ... Extra = EF.Property<string>(h, "extra"),
    //     DbSessionUser = EF.Property<string>(h, "db_session_user"), // only when the history entity type has the column
    // }
    private static (Type ResultClrType, Expression Body) WrapInVersion(
        Type entityClrType,
        Expression entityInit,
        ParameterExpression bag,
        string periodStart,
        string periodEnd,
        IEntityType historyEntityType)
    {
        var versionClrType = typeof(Version<>).MakeGenericType(entityClrType);

        MemberBinding Bind(string member, Expression value)
            => Expression.Bind(versionClrType.GetProperty(member)!, value);

        Expression ToDateTimeOffset(string column)
            => Expression.Convert(Property(bag, typeof(DateTime), column), typeof(DateTimeOffset));

        var bindings = new List<MemberBinding>
        {
            Bind(nameof(Version<object>.Entity), entityInit),
            Bind(nameof(Version<object>.ValidFrom), ToDateTimeOffset(periodStart)),
            Bind(nameof(Version<object>.ValidTo), ToDateTimeOffset(periodEnd)),
            Bind(
                nameof(Version<object>.Operation),
                Expression.Convert(
                    Property(bag, typeof(short), HindsightHistoryColumns.Operation), typeof(VersionOperation))),
            Bind(nameof(Version<object>.ChangedBy), Property(bag, typeof(string), HindsightHistoryColumns.ChangedBy)),
            Bind(nameof(Version<object>.ChangedByName), Property(bag, typeof(string), HindsightHistoryColumns.ChangedByName)),
            Bind(nameof(Version<object>.CorrelationId), Property(bag, typeof(string), HindsightHistoryColumns.CorrelationId)),
            Bind(nameof(Version<object>.Reason), Property(bag, typeof(string), HindsightHistoryColumns.Reason)),
            Bind(nameof(Version<object>.Extra), Property(bag, typeof(string), HindsightHistoryColumns.Extra)),
            Bind(
                nameof(Version<object>.DbSessionUser),
                historyEntityType.FindProperty(HindsightHistoryColumns.DbSessionUser) is not null
                    ? Property(bag, typeof(string), HindsightHistoryColumns.DbSessionUser)
                    : Expression.Constant(null, typeof(string))),
        };

        return (versionClrType, Expression.MemberInit(Expression.New(versionClrType), bindings));
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
    //
    // FromTo / ContainedIn (DESIGN.md D18) are the same source with a period-range predicate added to the
    // Where: TstzRange(valid_from, valid_to).Overlaps / .ContainedBy(TstzRange(@from, @to)), which EF
    // translates to tstzrange(valid_from, valid_to) && / <@ tstzrange(@from, @to) — the expression the
    // D14 GiST index is built on. The tombstone filter matters for ContainedIn: its empty range
    // [ts, ts) is contained by every range, so without it every delete in the window would come back.
    private static MethodCallExpression AllVersionsSource(
        EntityQueryRootExpression historyRoot,
        Type bagType,
        string periodStart,
        string periodEnd,
        (MethodInfo Operator, Expression FromUtc, Expression ToUtc)? range)
    {
        var filterParam = Expression.Parameter(bagType, "h");
        Expression filter = Expression.NotEqual(
            Property(filterParam, typeof(short), HindsightHistoryColumns.Operation),
            Expression.Constant(DeleteOperation));

        if (range is var (rangeOperator, fromUtc, toUtc))
        {
            var versionPeriod = Expression.Call(
                PeriodRangeFunction.Method,
                Property(filterParam, typeof(DateTime), periodStart),
                Property(filterParam, typeof(DateTime), periodEnd));
            var window = Expression.Call(PeriodRangeFunction.Method, fromUtc, toUtc);
            filter = Expression.AndAlso(filter, Expression.Call(rangeOperator, versionPeriod, window));
        }

        var filtered = Expression.Call(
            _queryableWhere.MakeGenericMethod(bagType),
            historyRoot,
            Expression.Quote(Expression.Lambda(filter, filterParam)));

        var orderParam = Expression.Parameter(bagType, "h");
        var validFrom = Expression.Lambda(Property(orderParam, typeof(DateTime), periodStart), orderParam);

        return Expression.Call(
            _queryableOrderByDescending.MakeGenericMethod(bagType, typeof(DateTime)),
            filtered,
            Expression.Quote(validFrom));
    }

    // history.OrderByDescending(h => EF.Property<DateTime>(h, "valid_from"))
    //        .ThenByDescending(h => EF.Property<long>(h, "history_id"))
    // No filter: History<T>() returns every stored row, tombstone included — it is the delete audit
    // (DESIGN.md D5). history_id is the identity surrogate key, so the secondary sort is a stable
    // total order (rows written in one transaction share nothing else that separates them).
    private static MethodCallExpression HistorySource(
        EntityQueryRootExpression historyRoot,
        Type bagType,
        string periodStart)
    {
        var orderParam = Expression.Parameter(bagType, "h");
        var validFrom = Expression.Lambda(Property(orderParam, typeof(DateTime), periodStart), orderParam);
        var ordered = Expression.Call(
            _queryableOrderByDescending.MakeGenericMethod(bagType, typeof(DateTime)),
            historyRoot,
            Expression.Quote(validFrom));

        var thenParam = Expression.Parameter(bagType, "h");
        var historyId = Expression.Lambda(
            Property(thenParam, typeof(long), HindsightHistoryColumns.HistoryId), thenParam);

        return Expression.Call(
            _queryableThenByDescending.MakeGenericMethod(bagType, typeof(long)),
            ordered,
            Expression.Quote(historyId));
    }

    // h => new TEntity { Prop = EF.Property<TProp>(h, "column"), Address = new Address { … }, … }. Table-split
    // complex properties and owned references (DESIGN.md D9) become nested member-inits over their own
    // mirrored columns; an optional one is null when every one of its columns is NULL, the same rule EF
    // applies when it reads the main table.
    private static List<MemberBinding> BuildBindings(IEntityType sourceEntityType, ParameterExpression bag, string operatorName)
    {
        var columns = VersionedColumns.Collect(sourceEntityType);
        var bindings = BindMembers(sourceEntityType, [], columns, bag, operatorName, sourceEntityType);

        if (bindings.Count == 0)
        {
            throw new NotSupportedException(
                $"{operatorName} found no settable persisted properties on '{sourceEntityType.DisplayName()}'.");
        }

        return bindings;
    }

    private static List<MemberBinding> BindMembers(
        IReadOnlyTypeBase declaringType,
        IReadOnlyList<IReadOnlyPropertyBase> path,
        List<VersionedColumn> columns,
        ParameterExpression bag,
        string operatorName,
        IEntityType sourceEntityType)
    {
        var bindings = new List<MemberBinding>();

        foreach (var column in columns)
        {
            if (!column.Path.SequenceEqual(path) || column.Property.IsShadowProperty())
            {
                continue;
            }

            if (column.Property.PropertyInfo is not { SetMethod: not null } member)
            {
                throw new NotSupportedException(
                    $"{operatorName} cannot reconstruct '{sourceEntityType.DisplayName()}': property '{column.DisplayName}' has "
                    + $"no setter. Entities read through {operatorName} require settable properties.");
            }

            bindings.Add(Expression.Bind(member, Property(bag, column.Property.ClrType, column.Column)));
        }

        foreach (var complexProperty in declaringType.GetComplexProperties())
        {
            BindNested(complexProperty, complexProperty.ComplexType, complexProperty.IsNullable);
        }

        if (declaringType is IReadOnlyEntityType entityType)
        {
            foreach (var navigation in VersionedColumns.OwnedReferences(entityType))
            {
                BindNested(navigation, navigation.TargetEntityType, !navigation.ForeignKey.IsRequiredDependent);
            }
        }

        return bindings;

        void BindNested(IReadOnlyPropertyBase nestedMember, IReadOnlyTypeBase nestedType, bool nullable)
        {
            var nestedPath = (IReadOnlyList<IReadOnlyPropertyBase>)[.. path, nestedMember];
            var nestedColumns = columns.Where(c => c.Path.Count >= nestedPath.Count && c.Path.Take(nestedPath.Count).SequenceEqual(nestedPath)).ToList();
            if (nestedColumns.Count == 0)
            {
                return;
            }

            var memberDisplayName = string.Join('.', nestedPath.Select(m => m.Name));
            if (nestedMember.PropertyInfo is not { SetMethod: not null } member)
            {
                throw new NotSupportedException(
                    $"{operatorName} cannot reconstruct '{sourceEntityType.DisplayName()}': member '{memberDisplayName}' has "
                    + $"no setter. Entities read through {operatorName} require settable properties.");
            }

            var clrType = nestedType.ClrType;
            if (!clrType.IsValueType && clrType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new NotSupportedException(
                    $"{operatorName} cannot reconstruct '{sourceEntityType.DisplayName()}': the type of member "
                    + $"'{memberDisplayName}', '{clrType.Name}', has no parameterless constructor. Entities read "
                    + $"through {operatorName} build complex and owned members with one and settable properties.");
            }

            Expression value = Expression.MemberInit(
                Expression.New(clrType),
                BindMembers(nestedType, nestedPath, columns, bag, operatorName, sourceEntityType));
            if (value.Type != member.PropertyType)
            {
                value = Expression.Convert(value, member.PropertyType);
            }

            if (nullable)
            {
                value = NullWhenAbsent(value, member.PropertyType, nestedPath, nestedColumns);
            }

            bindings.Add(Expression.Bind(member, value));
        }

        // An optional member is absent when its columns say so, by the rule EF itself applies to an optional
        // dependent sharing its owner's table: when it has a required property of its own, that property's
        // column is NULL; otherwise every column is NULL. EF translates a member access through
        // `column == null ? null : new T { … }` (a Where / OrderBy on p.Address.City composes into SQL) only
        // when the test is a null check of one column — `a == null && b == null` does not translate — so
        // the all-columns case is a chain of one-column checks. That chain materializes, but EF cannot
        // translate a filter on a member reached through it; the docs say so (docs/configuration/nested-members.md).
        Expression NullWhenAbsent(
            Expression value, Type memberType, IReadOnlyList<IReadOnlyPropertyBase> nestedPath, List<VersionedColumn> nestedColumns)
        {
            var absent = Expression.Constant(null, memberType);
            var required = nestedColumns.FirstOrDefault(
                c => c.Path.SequenceEqual(nestedPath) && !c.Property.IsNullable && !c.Property.IsShadowProperty());
            if (required is not null)
            {
                return Expression.Condition(IsNull(required), absent, value);
            }

            Expression result = absent;
            for (var i = nestedColumns.Count - 1; i >= 0; i--)
            {
                result = Expression.Condition(IsNull(nestedColumns[i]), result, value);
            }

            return result;
        }

        Expression IsNull(VersionedColumn column)
        {
            var nullableType = AsNullable(column.Property.ClrType);
            return Expression.Equal(Property(bag, nullableType, column.Column), Expression.Constant(null, nullableType));
        }
    }

    private static Type AsNullable(Type clrType)
        => clrType.IsValueType && Nullable.GetUnderlyingType(clrType) is null
            ? typeof(Nullable<>).MakeGenericType(clrType)
            : clrType;

    private static string PeriodColumn(IReadOnlyEntityType source, string annotationName, string fallback)
        => source.FindAnnotation(annotationName)?.Value as string ?? fallback;

    private static MethodCallExpression Property(Expression bag, Type clrType, string name)
        => Expression.Call(_efProperty.MakeGenericMethod(clrType), bag, Expression.Constant(name));

    // Overlaps<T>(NpgsqlRange<T>, NpgsqlRange<T>) / ContainedBy<T>(NpgsqlRange<T>, NpgsqlRange<T>): the
    // range-with-range overloads, not the ones taking a multirange or a bare element.
    private static MethodInfo RangeOperator(string name)
        => typeof(NpgsqlRangeDbFunctionsExtensions).GetMethods()
            .Single(m => m.Name == name
                && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 2
                && m.GetParameters().All(p => p.ParameterType.IsGenericType
                    && p.ParameterType.GetGenericTypeDefinition() == typeof(NpgsqlRange<>)))
            .MakeGenericMethod(typeof(DateTime));

    private enum HistoryReadKind
    {
        AsOf,
        AllVersions,
        FromTo,
        ContainedIn,
        History,
    }
}
