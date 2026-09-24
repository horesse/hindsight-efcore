using System.Reflection;
using NpgsqlTypes;

namespace Hindsight.Query;

/// <summary>
/// The model-mapped stand-in for PostgreSQL's built-in <c>tstzrange(lower, upper)</c> constructor, so
/// the time-range operators (<c>FromTo</c>, <c>ContainedIn</c>) can write their predicate as
/// <c>tstzrange(valid_from, valid_to) &amp;&amp; tstzrange(@from, @to)</c> — the exact expression the
/// D14 GiST index is built on — through LINQ (DESIGN.md D17). Npgsql does not translate
/// <c>new NpgsqlRange&lt;DateTime&gt;(column, column)</c>; mapping this method with the public
/// <c>HasDbFunction(...).HasName("tstzrange").IsBuiltIn()</c> does, with no EF Core internals.
/// Registered on every Hindsight model by <see cref="Conventions.PeriodRangeFunctionConvention"/>.
/// </summary>
internal static class PeriodRangeFunction
{
    /// <summary>The PostgreSQL function the method is mapped to.</summary>
    public const string StoreName = "tstzrange";

    public static readonly MethodInfo Method = typeof(PeriodRangeFunction)
        .GetMethod(nameof(TstzRange), BindingFlags.Public | BindingFlags.Static)!;

    // Translation only: the query rewriter puts calls to it into expression trees that EF translates.
    // Reaching this body means the expression was evaluated on the client.
    public static NpgsqlRange<DateTime> TstzRange(DateTime lower, DateTime upper)
        => throw new InvalidOperationException(
            "PeriodRangeFunction.TstzRange is only translated to SQL inside a Hindsight time-range query. "
            + "This is a bug in Hindsight.");
}
