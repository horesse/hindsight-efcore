using System.Linq.Expressions;
using System.Reflection;

namespace Hindsight.Query;

/// <summary>
/// After <see cref="HistoryQueryRootRewriter"/> has replaced the marker roots, appends a trailing
/// client-evaluated <c>Select(<see cref="HistoryOrigin.Tag{T}(T)"/>)</c> to a query whose result the
/// caller receives as the reconstructed entity (or a <see cref="Version{T}"/> of it), so every such
/// instance is marked for <see cref="Writers.HistorySnapshotGuardInterceptor"/> (DESIGN.md D7).
/// </summary>
/// <remarks>
/// The tag sits <b>outside</b> everything the caller composed, so it never stops a <c>Where</c> /
/// <c>OrderBy</c> / <c>Select</c> over the entity's members from translating. It is added only when
/// the query's own result sequence descends (through <c>Where</c> / <c>OrderBy</c> / <c>Concat</c> /
/// …) to one of the rewriter's nodes and still has that node's element type — so a query that merely
/// uses a history query inside a subquery, or projects to a scalar / DTO, is left alone.
/// </remarks>
internal static class HistoryOriginTagger
{
    private static readonly MethodInfo _tag =
        typeof(HistoryOrigin).GetMethod(nameof(HistoryOrigin.Tag))!;

    private static readonly MethodInfo _tagVersion =
        typeof(HistoryOrigin).GetMethod(nameof(HistoryOrigin.TagVersion))!;

    private static readonly MethodInfo _queryableSelect = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Select)
            && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    private static readonly MethodInfo _enumerableSelect = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Select)
            && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2);

    private static readonly MethodInfo _queryableWhere = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Where)
            && m.GetParameters().Length == 2
            && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    public static Expression Append(Expression root, IReadOnlyDictionary<Expression, Type> markers)
    {
        if (markers.Count == 0)
        {
            return root;
        }

        // A single-result operator (First / Single / Last / ElementAt, with or without a predicate):
        // splice the tagging Select right around its sequence argument, folding a predicate into a
        // Where so it still translates against the untagged sequence.
        if (root is MethodCallExpression call && IsSingleResultOperator(call))
        {
            var predicate = MovePredicateIntoWhere(call);
            var tagged = TryTagSequence(call.Arguments[0], markers, predicate);
            if (tagged is null)
            {
                return root;
            }

            var elementType = call.Method.GetGenericArguments()[0];

            if (predicate is not null)
            {
                // First(seq, pred) -> First(Tag(Where(seq, pred)))
                return Expression.Call(typeof(Queryable), call.Method.Name, [elementType], tagged);
            }

            // No predicate (or ElementAt(seq, index)): keep the trailing args, swap the sequence.
            var args = call.Arguments.ToArray();
            args[0] = tagged;
            return call.Update(call.Object, args);
        }

        // Sequence-returning root: ToList / ToArray / AsEnumerable / Concat / foreach / …
        return TryTagSequence(root, markers, predicate: null) ?? root;
    }

    // Walk down from `sequence`; if any reachable branch is a rewritten marker yielding `sequence`'s
    // element type, wrap `sequence` (optionally filtered by `predicate` first) in the matching Tag /
    // TagVersion Select. Returns null when nothing should be tagged.
    private static MethodCallExpression? TryTagSequence(
        Expression sequence, IReadOnlyDictionary<Expression, Type> markers, LambdaExpression? predicate)
    {
        if (!TryGetElementType(sequence.Type, out var elementType) || !ReachesMarker(sequence, elementType, markers))
        {
            return null;
        }

        var filtered = predicate is null
            ? sequence
            : Expression.Call(
                _queryableWhere.MakeGenericMethod(elementType), sequence, Expression.Quote(predicate));

        return TagSelect(filtered, elementType);
    }

    // Whether `node` is, or descends to, a rewritten marker yielding `elementType`. Checks every
    // sequence-typed argument of a method call, not just the first: Concat / Union / Except /
    // Intersect all take two sequence arguments, and the marker can be reachable through either one
    // (e.g. `otherQuery.Concat(db.Policies.AsOf(t))`, where the history query is arg[1]). Finding a
    // marker through ANY branch is enough to tag the whole outer sequence — HistoryOrigin.Tag /
    // TagVersion is a no-op for an instance that didn't actually come from history (it just adds an
    // unused ConditionalWeakTable entry), so over-tagging the untagged side of the same Concat/Union
    // is harmless, whereas under-tagging the history-derived side would silently defeat the
    // save-back guard (DESIGN.md D7) — the failure mode this walk exists to avoid.
    private static bool ReachesMarker(Expression node, Type elementType, IReadOnlyDictionary<Expression, Type> markers)
    {
        if (markers.TryGetValue(node, out var resultType))
        {
            return resultType == elementType;
        }

        if (node is not MethodCallExpression m)
        {
            return false;
        }

        foreach (var argument in m.Arguments)
        {
            if (TryGetElementType(argument.Type, out _) && ReachesMarker(argument, elementType, markers))
            {
                return true;
            }
        }

        return false;
    }

    private static MethodCallExpression TagSelect(Expression sequence, Type elementType)
    {
        var param = Expression.Parameter(elementType, "e");
        var (tagMethod, isQueryable) = TagMethodFor(elementType, sequence.Type);
        var body = Expression.Call(tagMethod, param);
        var selector = Expression.Lambda(body, param);

        return isQueryable
            ? Expression.Call(
                _queryableSelect.MakeGenericMethod(elementType, elementType),
                sequence,
                Expression.Quote(selector))
            : Expression.Call(
                _enumerableSelect.MakeGenericMethod(elementType, elementType), sequence, selector);
    }

    private static (MethodInfo Method, bool IsQueryable) TagMethodFor(Type elementType, Type sequenceType)
    {
        var isQueryable = typeof(IQueryable).IsAssignableFrom(sequenceType);

        if (elementType.IsGenericType && elementType.GetGenericTypeDefinition() == typeof(Version<>))
        {
            return (_tagVersion.MakeGenericMethod(elementType.GetGenericArguments()[0]), isQueryable);
        }

        return (_tag.MakeGenericMethod(elementType), isQueryable);
    }

    private static LambdaExpression? MovePredicateIntoWhere(MethodCallExpression singleResultCall)
    {
        if (singleResultCall.Arguments.Count != 2)
        {
            return null;
        }

        var arg = singleResultCall.Arguments[1];

        // ElementAt(seq, index) — not a predicate.
        if (arg.Type == typeof(int))
        {
            return null;
        }

        return arg switch
        {
            UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda } => lambda,
            LambdaExpression lambda => lambda,
            _ => null,
        };
    }

    private static bool IsSingleResultOperator(MethodCallExpression call)
        => call.Method.DeclaringType == typeof(Queryable)
            && call.Method.Name is nameof(Queryable.First) or nameof(Queryable.FirstOrDefault)
                or nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault)
                or nameof(Queryable.Last) or nameof(Queryable.LastOrDefault)
                or nameof(Queryable.ElementAt) or nameof(Queryable.ElementAtOrDefault);

    private static bool TryGetElementType(Type type, out Type elementType)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IQueryable<>) || definition == typeof(IOrderedQueryable<>)
                || definition == typeof(IEnumerable<>) || definition == typeof(IOrderedEnumerable<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null!;
        return false;
    }
}
