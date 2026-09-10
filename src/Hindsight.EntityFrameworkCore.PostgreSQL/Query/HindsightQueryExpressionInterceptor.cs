using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Hindsight.Query;

/// <summary>
/// The query hook that makes <see cref="HindsightQueryableExtensions.AsOf{TEntity}"/> work. Registered
/// by <see cref="Infrastructure.HindsightOptionsExtension"/>. On every query compilation it looks for
/// an <c>AsOf</c> marker; if there is none the tree is returned untouched, otherwise
/// <see cref="AsOfQueryRootRewriter"/> rewrites the marked roots (DESIGN.md D12).
/// </summary>
internal sealed class HindsightQueryExpressionInterceptor : IQueryExpressionInterceptor
{
    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(queryExpression);
        ArgumentNullException.ThrowIfNull(eventData);

        var scan = MarkerScanner.Scan(queryExpression);
        if (!scan.HasAsOf)
        {
            return queryExpression;
        }

        if (scan.HasInclude)
        {
            throw new NotSupportedException(
                "AsOf() cannot be combined with Include() / ThenInclude() in v1 (DESIGN.md D8): reading related "
                + "entities at a point in time is an interval join, and a silently wrong result would be worse "
                + "than the missing feature. Load the related rows with a separate AsOf() query, or use FromSql.");
        }

        if (scan.HasTrackingOperator)
        {
            throw new InvalidOperationException(
                "AsOf() results are always no-tracking (DESIGN.md D7). Remove AsTracking() / "
                + "AsTrackingWithIdentityResolution() from the query.");
        }

        var model = eventData.Context?.Model
            ?? throw new InvalidOperationException(
                "AsOf(): the query has no DbContext model to resolve the history table from.");

        return new AsOfQueryRootRewriter(model).Visit(queryExpression);
    }

    /// <summary>
    /// One pass over the tree recording whether it uses <c>AsOf</c>, and — so their combination can be
    /// rejected with a Hindsight message rather than a downstream EF one — whether it also uses
    /// <c>Include</c> / <c>ThenInclude</c> or an explicit tracking operator.
    /// </summary>
    private sealed class MarkerScanner : ExpressionVisitor
    {
        public bool HasAsOf { get; private set; }

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

            if (method.IsGenericMethod
                && method.GetGenericMethodDefinition() == HindsightQueryableExtensions.AsOfMethod)
            {
                HasAsOf = true;
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
    }
}
