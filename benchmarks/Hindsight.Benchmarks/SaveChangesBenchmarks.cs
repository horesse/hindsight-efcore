using BenchmarkDotNet.Attributes;

namespace Hindsight.Benchmarks;

/// <summary>
/// Placeholder. Real benchmarks land with the history writer: SaveChanges with no Hindsight /
/// interceptor writer / trigger writer, on 1, 100 and 1000 entities. Numbers go into README.
/// </summary>
[MemoryDiagnoser]
public class SaveChangesBenchmarks
{
    [Params(1, 100, 1000)]
    public int EntityCount { get; set; }

    [Benchmark(Baseline = true)]
    public int Baseline() => EntityCount;
}
