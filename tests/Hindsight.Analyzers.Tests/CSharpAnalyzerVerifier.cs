using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.EntityFrameworkCore;

namespace Hindsight.Analyzers.Tests;

/// <summary>
/// Thin wrapper around <see cref="CSharpAnalyzerTest{TAnalyzer, TVerifier}"/> pinned to
/// <see cref="DefaultVerifier"/> (no xunit-specific adapter - see the csproj comment) and to the
/// reference assemblies and extra metadata references every HDST001 test needs: the BCL, EF Core's
/// core assembly (where <c>DbContext</c>/<c>DbSet&lt;T&gt;</c>/<c>EntityFrameworkQueryableExtensions</c>
/// live) and Hindsight itself (<c>IsTemporal</c>).
/// </summary>
internal static class CSharpAnalyzerVerifier<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    public static DiagnosticResult Diagnostic() =>
        CSharpAnalyzerVerifier<TAnalyzer, DefaultVerifier>.Diagnostic();

    public static DiagnosticResult Diagnostic(string diagnosticId) =>
        CSharpAnalyzerVerifier<TAnalyzer, DefaultVerifier>.Diagnostic(diagnosticId);

    public static Task VerifyAnalyzerAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net100,
        };

        test.TestState.AdditionalReferences.Add(
            MetadataReference.CreateFromFile(typeof(DbContext).Assembly.Location));
        test.TestState.AdditionalReferences.Add(
            MetadataReference.CreateFromFile(typeof(TemporalEntityTypeBuilderExtensions).Assembly.Location));

        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }
}
