using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hindsight.Query;

/// <summary>
/// The query hook that makes <see cref="HindsightQueryableExtensions.AsOf{TEntity}"/> and
/// <see cref="HindsightQueryableExtensions.AllVersions{TEntity}"/> work. Registered by
/// <see cref="Infrastructure.HindsightOptionsExtension"/>. On every query compilation it looks for one
/// of those markers; if there is none the tree is returned untouched, otherwise
/// <see cref="HistoryQueryRootRewriter"/> rewrites the marked roots (DESIGN.md D12).
/// </summary>
internal sealed class HindsightQueryExpressionInterceptor : IQueryExpressionInterceptor
{
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(queryExpression);
        ArgumentNullException.ThrowIfNull(eventData);

        var scan = MarkerScanner.Scan(queryExpression);
        if (!scan.HasHistoryMarker)
        {
            return queryExpression;
        }

        if (scan.HasInclude)
        {
            throw new NotSupportedException(
                "AsOf() / AllVersions() / History<T>() cannot be combined with Include() / ThenInclude() in v1 (DESIGN.md D8): "
                + "reading related entities from history is an interval join, and a silently wrong result would be "
                + "worse than the missing feature. Load the related rows with a separate history query, or use FromSql.");
        }

        if (scan.HasTrackingOperator)
        {
            throw new InvalidOperationException(
                "AsOf() / AllVersions() / History<T>() results are always no-tracking (DESIGN.md D7). Remove AsTracking() / "
                + "AsTrackingWithIdentityResolution() from the query.");
        }

        var model = eventData.Context?.Model
            ?? throw new InvalidOperationException(
                "AsOf() / AllVersions() / History<T>(): the query has no DbContext model to resolve the history table from.");

        return new HistoryQueryRootRewriter(model).Visit(queryExpression);
    }

    /// <summary>
    /// One pass over the tree recording whether it uses <c>AsOf</c>, <c>AllVersions</c> or
    /// <c>History&lt;T&gt;</c>, and — so their combination can be rejected with a Hindsight message
    /// rather than a downstream EF one — whether it also uses <c>Include</c> / <c>ThenInclude</c> or
    /// an explicit tracking operator.
    /// </summary>
    private sealed class MarkerScanner : ExpressionVisitor
    {
        public bool HasHistoryMarker { get; private set; }

        public bool HasInclude { get; private set; }

        public bool HasTrackingOperator { get; private set; }

        public static MarkerScanner Scan(Expression expression)
        {
            var scanner = new MarkerScanner();
            scanner.Visit(expression);
            return scanner;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var method = node.Method;

            if (method.IsGenericMethod && IsHistoryMarker(method.GetGenericMethodDefinition()))
            {
                HasHistoryMarker = true;
            }
            else if (method.DeclaringType == typeof(EntityFrameworkQueryableExtensions))
            {
                if (method.Name is nameof(EntityFrameworkQueryableExtensions.Include)
                    or nameof(EntityFrameworkQueryableExtensions.ThenInclude))
                {
                    HasInclude = true;
                }
                else if (method.Name == nameof(EntityFrameworkQueryableExtensions.AsTracking))
                {
                    HasTrackingOperator = true;
                }
            }

            return base.VisitMethodCall(node);
        }

        private static bool IsHistoryMarker(MethodInfo definition)
            => definition == HindsightQueryableExtensions.AsOfMethod
                || definition == HindsightQueryableExtensions.AllVersionsMethod
                || definition == HindsightQueryableExtensions.HistoryMethod;
    }
}
