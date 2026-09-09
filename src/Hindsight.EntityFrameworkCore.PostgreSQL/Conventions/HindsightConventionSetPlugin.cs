using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace Hindsight.Conventions;

/// <summary>
/// Registers Hindsight's model conventions. Added to the provider's convention set by
/// <see cref="Infrastructure.HindsightOptionsExtension"/> when <c>UseHindsight()</c> is called.
/// </summary>
internal sealed class HindsightConventionSetPlugin : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        ArgumentNullException.ThrowIfNull(conventionSet);

        conventionSet.ModelFinalizingConventions.Add(new HistoryEntityTypeConvention());
        return conventionSet;
    }
}
