using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Hindsight.Analyzers;

/// <summary>
/// HDST001: flags <c>ExecuteUpdate</c>/<c>ExecuteUpdateAsync</c>/<c>ExecuteDelete</c>/
/// <c>ExecuteDeleteAsync</c> calls against an entity type this compilation configures with
/// <c>IsTemporal()</c> (DESIGN.md D4).
///
/// <para>
/// <b>What this analyzer can and cannot know.</b> Roslyn hands an analyzer one compilation (one
/// project) at a time. Within that compilation it can reliably tell that a given <c>TEntity</c> is
/// marked <c>IsTemporal()</c> somewhere - a call to <c>Hindsight.TemporalEntityTypeBuilderExtensions
/// .IsTemporal&lt;TEntity&gt;</c> is ordinary syntax/semantics, not a runtime fact. It can never
/// reliably tell which <c>HistoryWriter</c> is configured for that entity's <c>DbContext</c>: that is
/// a runtime <c>UseHindsight(h => h.UseHistoryWriter(...))</c> call, and in any layered application -
/// entities and a repository in one project, <c>UseHindsight</c> in the ASP.NET host's <c>Program.cs</c>
/// in another - that call lives in a *different compilation* the analyzer never sees at all. Trying to
/// approximate it by also requiring, say, "no <c>UseHistoryWriter(HistoryWriter.Trigger)</c> call
/// anywhere in this compilation" only works for the single-project sample shape and would silently stop
/// working the moment a real project is layered that way - a false sense of precision is worse than an
/// honest "go check" (golden rule 2 in spirit, applied to diagnostic quality rather than query results).
/// </para>
///
/// <para>
/// So this analyzer reports at <see cref="DiagnosticSeverity.Info"/>, not <c>Warning</c>: it is a
/// "you probably want to check this" nudge, not a claim that history is actually being lost. Under
/// <c>HistoryWriter.Trigger</c> the flagged call is completely fine - the trigger fires regardless of
/// how the row changed - and the diagnostic message says so. See DESIGN.md D4 and
/// <c>docs/writing/interceptor.md</c> for the full write-up, including how to suppress a call site
/// that is verified to use <c>HistoryWriter.Trigger</c>.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class ExecuteUpdateOrDeleteOnTemporalEntityAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Hindsight's first analyzer diagnostic ID.</summary>
    public const string DiagnosticId = "HDST001";

    private const string Category = "Reliability";

    private static readonly LocalizableString _title =
        "ExecuteUpdate/ExecuteDelete on a temporal entity writes no history under HistoryWriter.Interceptor";

    // Plain strings, not a .resx: Hindsight is English-only end to end (CLAUDE.md - code, docs and this
    // analyzer's own messages included), so a resource-based localization layer would be pure overhead.
    private static readonly LocalizableString _messageFormat =
        "This '{0}' call targets '{1}', which this project configures with IsTemporal(). Under "
        + "HistoryWriter.Interceptor (the default) bulk operations bypass SaveChanges and write no "
        + "history (DESIGN.md D4). If this DbContext uses HistoryWriter.Trigger instead, a database "
        + "trigger records the change regardless and this call is fine - suppress HDST001 here if so";

    private static readonly LocalizableString _description =
        "This analyzer can see that the entity type is temporal from IsTemporal() in this compilation, "
        + "but it cannot see which HistoryWriter the application actually uses - that is a runtime "
        + "UseHindsight(...) call that may live in a different project entirely. It reports at Info "
        + "severity for exactly that reason: treat both a firing and a silent diagnostic as 'go check', "
        + "never as a guarantee either way.";

    internal static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        _title,
        _messageFormat,
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: _description,
        helpLinkUri: "https://horesse.github.io/hindsight-efcore/latest/writing/interceptor#hdst001-bulk-operations-on-a-temporal-entity",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly ImmutableHashSet<string> _bulkOperationMethodNames = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "ExecuteUpdate",
        "ExecuteUpdateAsync",
        "ExecuteDelete",
        "ExecuteDeleteAsync");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rule);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    /// <summary>
    /// <c>IsTemporal()</c> calls and <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> calls can appear in any
    /// order across the compilation's syntax trees (operation actions run per-node with no guaranteed
    /// cross-file ordering), so both are only collected here and correlated once at
    /// <see cref="CompilationStartAnalysisContext.RegisterCompilationEndAction"/>, after the whole
    /// compilation has been seen - the standard pattern for a diagnostic that depends on more than the
    /// one node it fires on.
    /// </summary>
    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var temporalEntityTypes = new ConcurrentBag<ITypeSymbol>();
        var candidates = new ConcurrentBag<BulkOperationCandidate>();

        context.RegisterOperationAction(
            operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;

                if (TryGetTemporalEntityType(invocation, out var entityType))
                {
                    temporalEntityTypes.Add(entityType);
                }
                else if (TryGetBulkOperationCandidate(invocation, out var candidate))
                {
                    candidates.Add(candidate);
                }
            },
            OperationKind.Invocation);

        context.RegisterCompilationEndAction(endContext =>
        {
            if (candidates.IsEmpty || temporalEntityTypes.IsEmpty)
            {
                return;
            }

            var temporalSet = new HashSet<ITypeSymbol>(temporalEntityTypes, SymbolEqualityComparer.Default);

            foreach (var candidate in candidates)
            {
                if (!temporalSet.Contains(candidate.EntityType))
                {
                    continue;
                }

                endContext.ReportDiagnostic(Diagnostic.Create(
                    Rule,
                    candidate.Location,
                    candidate.MethodName,
                    candidate.EntityType.Name));
            }
        });
    }

    /// <summary>
    /// Matches <c>Hindsight.TemporalEntityTypeBuilderExtensions.IsTemporal&lt;TEntity&gt;(...)</c> and
    /// extracts <c>TEntity</c>.
    /// </summary>
    private static bool TryGetTemporalEntityType(IInvocationOperation invocation, out ITypeSymbol entityType)
    {
        var method = invocation.TargetMethod;

        if (method is { Name: "IsTemporal", IsGenericMethod: true, TypeArguments.Length: 1 }
            && method.ContainingType is { Name: "TemporalEntityTypeBuilderExtensions" } containingType
            && containingType.ContainingNamespace?.ToDisplayString() == "Hindsight")
        {
            entityType = method.TypeArguments[0];
            return true;
        }

        entityType = null!;
        return false;
    }

    /// <summary>
    /// Matches <c>Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
    /// .ExecuteUpdate{Async}/ExecuteDelete{Async}&lt;TSource&gt;(...)</c> and extracts <c>TSource</c>.
    /// Matching by containing type also excludes an unrelated method that merely happens to share one
    /// of these names on some other type.
    /// </summary>
    private static bool TryGetBulkOperationCandidate(IInvocationOperation invocation, out BulkOperationCandidate candidate)
    {
        var method = invocation.TargetMethod;

        if (_bulkOperationMethodNames.Contains(method.Name)
            && method.ContainingType is { Name: "EntityFrameworkQueryableExtensions" } containingType
            && containingType.ContainingNamespace?.ToDisplayString() == "Microsoft.EntityFrameworkCore"
            && method.TypeArguments.Length == 1)
        {
            candidate = new BulkOperationCandidate(method.TypeArguments[0], method.Name, invocation.Syntax.GetLocation());
            return true;
        }

        candidate = default;
        return false;
    }

    private readonly struct BulkOperationCandidate
    {
        public BulkOperationCandidate(ITypeSymbol entityType, string methodName, Location location)
        {
            EntityType = entityType;
            MethodName = methodName;
            Location = location;
        }

        public ITypeSymbol EntityType { get; }

        public string MethodName { get; }

        public Location Location { get; }
    }
}
