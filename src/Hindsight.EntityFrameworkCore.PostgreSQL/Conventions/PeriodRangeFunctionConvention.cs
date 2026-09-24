using Hindsight.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Hindsight.Conventions;

/// <summary>
/// Maps <see cref="PeriodRangeFunction.TstzRange"/> to PostgreSQL's built-in <c>tstzrange</c> on every
/// Hindsight model, so the <c>FromTo</c> / <c>ContainedIn</c> rewrite can build a range over the period
/// columns that the D14 GiST index serves (DESIGN.md D17). A function mapping is not part of the
/// migrations model, so it never shows up in a migration or a model snapshot.
/// </summary>
internal sealed class PeriodRangeFunctionConvention : IModelInitializedConvention
{
    public void ProcessModelInitialized(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDbFunction(PeriodRangeFunction.Method)
            ?.HasName(PeriodRangeFunction.StoreName)
            ?.IsBuiltIn(true);
    }
}
