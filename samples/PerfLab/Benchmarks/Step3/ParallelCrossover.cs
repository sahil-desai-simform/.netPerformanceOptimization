using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step3;

/// <summary>
/// ParallelismVsConcurrency.md §4 — parallelism is not free, and below some amount of work per
/// item it is a straight loss.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *ParallelCrossover*
///
/// Parallel.For has to partition the range, schedule work items onto the pool, and join at the
/// end. That overhead is roughly constant per call; the benefit scales with total work. So there
/// is a crossover point, and it is much further right than most people assume.
///
/// [Params] sweeps the per-item cost so you can SEE the crossover on your own hardware instead of
/// trusting a rule of thumb. The answer depends on core count, so read it off your own table.
///
/// The practical lesson: "it's a loop, make it parallel" is not a performance strategy. A parallel
/// loop over cheap work is slower than a sequential one, and it also burns pool threads that an
/// ASP.NET Core server needed for other requests (§8).
/// </summary>
[MemoryDiagnoser]
public class ParallelCrossover
{
    private int[] _data = null!;
    private int[] _output = null!;

    /// <summary>How much arithmetic each element costs. 1 ≈ trivial, 1000 ≈ genuinely expensive.</summary>
    [Params(1, 100, 10_000)]
    public int WorkPerItem;

    private const int ItemCount = 4_096;

    [GlobalSetup]
    public void Setup()
    {
        _data = Enumerable.Range(0, ItemCount).ToArray();
        _output = new int[ItemCount];
    }

    [Benchmark(Baseline = true)]
    public int SequentialFor()
    {
        for (int i = 0; i < _data.Length; i++)
        {
            _output[i] = Work(_data[i], WorkPerItem);
        }

        return _output[^1];
    }

    [Benchmark]
    public int ParallelFor()
    {
        Parallel.For(0, _data.Length, i => _output[i] = Work(_data[i], WorkPerItem));
        return _output[^1];
    }

    /// <summary>
    /// Capping the degree of parallelism matters in a server: the default lets one loop consume
    /// every pool thread, which is capacity taken from other requests. Half the cores is a
    /// reasonable starting point for work running inside a request.
    /// </summary>
    [Benchmark]
    public int ParallelForBoundedDop()
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
        };

        Parallel.For(0, _data.Length, options, i => _output[i] = Work(_data[i], WorkPerItem));
        return _output[^1];
    }

    /// <summary>
    /// Chunked manually: one work item per core instead of per element. This is what a custom
    /// partitioner buys you — it amortises the scheduling overhead that makes the fine-grained
    /// version lose at small WorkPerItem.
    /// </summary>
    [Benchmark]
    public int ParallelForChunked()
    {
        int chunks = Environment.ProcessorCount;
        int chunkSize = (_data.Length + chunks - 1) / chunks;

        Parallel.For(0, chunks, c =>
        {
            int start = c * chunkSize;
            int end = Math.Min(start + chunkSize, _data.Length);
            for (int i = start; i < end; i++)
            {
                _output[i] = Work(_data[i], WorkPerItem);
            }
        });

        return _output[^1];
    }

    private static int Work(int seed, int iterations)
    {
        int acc = seed;
        for (int i = 0; i < iterations; i++)
        {
            acc = acc * 31 + i;      // cheap, but not removable by the JIT
        }

        return acc;
    }
}
