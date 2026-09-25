using System.Reflection;

namespace Hindsight.Query;

/// <summary>
/// The model-mapped stand-in for the <c>hindsight_history_retained(history_entity, at)</c> function a
/// migration creates next to the retention-horizon table (DESIGN.md D19). The query rewriter adds a call
/// to it to <c>AsOf</c> / <c>FromTo</c> / <c>ContainedIn</c> on an entity configured with
/// <c>WithRetention()</c>, so a query that reaches before the horizon fails in the database instead of
/// returning an answer built from pruned history. Registered on every Hindsight model by
/// <see cref="Conventions.RetentionGuardFunctionConvention"/>; a function mapping adds nothing to a
/// migration or a model snapshot.
/// </summary>
internal static class RetentionGuardFunction
{
    public static readonly MethodInfo Method = typeof(RetentionGuardFunction)
        .GetMethod(nameof(IsRetained), BindingFlags.Public | BindingFlags.Static)!;

    // Translation only: the query rewriter puts calls to it into expression trees that EF translates.
    // Reaching this body means the expression was evaluated on the client.
    public static bool IsRetained(string historyEntity, DateTime at)
        => throw new InvalidOperationException(
            "RetentionGuardFunction.IsRetained is only translated to SQL inside a Hindsight historical query. "
            + "This is a bug in Hindsight.");
}
