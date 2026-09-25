using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Hindsight.Conventions;

/// <summary>
/// Registers Hindsight's model conventions. Added to the provider's convention set by
/// <see cref="Infrastructure.HindsightOptionsExtension"/> when <c>UseHindsight()</c> is called.
/// </summary>
/// <remarks>
/// Takes <see cref="IMigrationsAssembly"/> so the finalizing convention can read the previous model
/// snapshot and re-materialize history columns whose source property was removed (DESIGN.md D6), and
/// <see cref="IRelationalTypeMappingSource"/> so it can compare a live column with its snapshot shape.
/// </remarks>
internal sealed class HindsightConventionSetPlugin(
    IMigrationsAssembly migrationsAssembly,
    IRelationalTypeMappingSource typeMappingSource) : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        ArgumentNullException.ThrowIfNull(conventionSet);

        conventionSet.ModelInitializedConventions.Add(new PeriodRangeFunctionConvention());
        conventionSet.ModelInitializedConventions.Add(new RetentionGuardFunctionConvention());
        conventionSet.ModelFinalizingConventions.Add(new HistoryEntityTypeConvention(migrationsAssembly, typeMappingSource));
        return conventionSet;
    }
}
