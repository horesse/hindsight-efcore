using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;

namespace Hindsight.Benchmarks;

/// <summary>
/// One job tuned for benchmarks that hit a real database and mutate it: <see cref="RunStrategy.Monitoring"/>
/// with a single invocation per iteration, so each measured iteration is exactly one <c>SaveChanges</c>
/// and the per-iteration <c>[IterationSetup]</c> (which truncates and re-seeds) stays out of the
/// measurement. The single-row cases are inherently noisy at this scale — read the 100- and 1000-row
/// rows for the trend. Everything else (GitHub Markdown export, console logger, columns) comes from
/// the default config.
/// </summary>
public sealed class BenchmarkConfig : ManualConfig
{
    /// <summary>Wires the database job and the memory diagnoser onto the default config.</summary>
    public BenchmarkConfig()
    {
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(3)
            .WithIterationCount(20)
            .WithInvocationCount(1)
            .WithUnrollFactor(1));

        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
