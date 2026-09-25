namespace Hindsight.Analyzers.Tests;

/// <summary>
/// A C# compiler refuses to load an analyzer built against a newer Roslyn than itself (CS9057), and the
/// analyzer ships inside the package, so the Roslyn it references is the oldest SDK a consumer can build
/// with. It must stay at the compiler of the .NET 10.0.100 SDK — the oldest SDK that builds net10.0.
/// </summary>
public class HindsightAnalyzerCompilerVersionTests
{
    private static readonly Version _oldestSupportedCompiler = new(5, 0, 0, 0);

    [Fact]
    public void The_analyzer_references_no_Roslyn_newer_than_the_oldest_net10_sdk_compiler()
    {
        var roslyn = typeof(ExecuteUpdateOrDeleteOnTemporalEntityAnalyzer).Assembly
            .GetReferencedAssemblies()
            .Where(reference => reference.Name?.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal) == true)
            .ToList();

        Assert.NotEmpty(roslyn);
        Assert.All(roslyn, reference => Assert.True(
            reference.Version <= _oldestSupportedCompiler,
            $"{reference.Name} {reference.Version} is newer than the .NET 10.0.100 SDK's compiler "
            + $"({_oldestSupportedCompiler}); consumers on that SDK would get CS9057 and lose HDST001."));
    }
}
