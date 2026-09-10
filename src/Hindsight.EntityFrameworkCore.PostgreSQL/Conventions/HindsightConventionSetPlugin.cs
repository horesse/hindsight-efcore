using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hindsight.Conventions;

/// <summary>
/// Registers Hindsight's model conventions. Added to the provider's convention set by
/// <see cref="Infrastructure.HindsightOptionsExtension"/> when <c>UseHindsight()</c> is called.
/// </summary>
/// <remarks>
/// Takes <see cref="IMigrationsAssembly"/> so the finalizing convention can read the previous model
/// snapshot and re-materialize history columns whose source property was removed (DESIGN.md D6).
/// </remarks>
internal sealed class HindsightConventionSetPlugin(IMigrationsAssembly migrationsAssembly) : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        ArgumentNullException.ThrowIfNull(conventionSet);

        conventionSet.ModelFinalizingConventions.Add(new HistoryEntityTypeConvention(migrationsAssembly));
        return conventionSet;
    }
}
