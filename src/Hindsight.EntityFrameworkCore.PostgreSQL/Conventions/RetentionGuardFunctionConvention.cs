using Hindsight.Migrations;
using Hindsight.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Hindsight.Conventions;

/// <summary>
/// Maps <see cref="RetentionGuardFunction.IsRetained"/> to <c>hindsight_history_retained</c> on every
/// Hindsight model (DESIGN.md D19). Registered when the model is initialized, like
/// <see cref="PeriodRangeFunctionConvention"/>, so the relational conventions that run later resolve its
/// type mappings. With no schema of its own it resolves to the model's default schema, which is where the
/// retention-horizon table and the function are created too. The query rewriter only calls it for an entity configured with
/// <c>WithRetention()</c>, whose migration created the function.
/// </summary>
internal sealed class RetentionGuardFunctionConvention : IModelInitializedConvention
{
    public void ProcessModelInitialized(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDbFunction(RetentionGuardFunction.Method)
            ?.HasName(RetentionSqlGenerator.FunctionName);
    }
}
