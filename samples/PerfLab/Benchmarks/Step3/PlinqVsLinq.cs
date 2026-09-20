using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step3;

/// <summary>
/// ParallelismVsConcurrency.md §3 — PLINQ, and what ordering costs.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *PlinqVsLinq*
///
/// PLINQ is the lowest-friction way to parallelise a query: add .AsParallel(). That is also the
/// problem — it is easy to add somewhere it does not pay, and the code gives no hint.
///
/// Three things this table is designed to show:
///
///   1. At low work-per-item PLINQ LOSES, same as Parallel.For (see ParallelCrossover).
///   2. .AsOrdered() is not free. Preserving source order requires buffering and reordering.
///   3. A hand-written parallel loop usually beats PLINQ, because PLINQ pays for generality.
///
/// PLINQ's real niche is a genuinely expensive, order-insensitive, CPU-bound query over a large
/// in-memory collection — and in a web service that describes almost nothing (§8).
/// </summary>
[MemoryDiagnoser]
public class PlinqVsLinq
{
    private int[] _data = null!;

    [Params(1, 1_000)]
    public int WorkPerItem;

    private const int ItemCount = 100_000;

    [GlobalSetup]
    public void Setup() => _data = Enumerable.Range(0, ItemCount).ToArray();

    [Benchmark(Baseline = true)]
    public long Linq() => _data.Where(x => x % 2 == 0).Select(x => (long)Work(x, WorkPerItem)).Sum();

    [Benchmark]
    public long Plinq() => _data.AsParallel()
        .Where(x => x % 2 == 0)
        .Select(x => (long)Work(x, WorkPerItem))
        .Sum();

    /// <summary>Ordering preserved. Compare directly against Plinq to price the guarantee.</summary>
    [Benchmark]
    public long PlinqOrdered() => _data.AsParallel()
        .AsOrdered()
        .Where(x => x % 2 == 0)
        .Select(x => (long)Work(x, WorkPerItem))
        .Sum();

    /// <summary>Explicit degree of parallelism — what you'd use inside a request handler.</summary>
    [Benchmark]
    public long PlinqBoundedDop() => _data.AsParallel()
        .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
        .Where(x => x % 2 == 0)
        .Select(x => (long)Work(x, WorkPerItem))
        .Sum();

    /// <summary>No LINQ at all: a plain loop. The control that keeps the others honest.</summary>
    [Benchmark]
    public long ManualLoop()
    {
        long sum = 0;
        for (int i = 0; i < _data.Length; i++)
        {
            int x = _data[i];
            if (x % 2 == 0)
            {
                sum += Work(x, WorkPerItem);
            }
        }

        return sum;
    }

    /// <summary>Hand-rolled parallel aggregation — no query operators, no delegates per element.</summary>
    [Benchmark]
    public long ManualParallel()
    {
        long total = 0;
        int chunks = Environment.ProcessorCount;
        int chunkSize = (_data.Length + chunks - 1) / chunks;
        int work = WorkPerItem;
        int[] data = _data;

        Parallel.For(0, chunks, c =>
        {
            int start = c * chunkSize;
            int end = Math.Min(start + chunkSize, data.Length);
            long local = 0;
            for (int i = start; i < end; i++)
            {
                int x = data[i];
                if (x % 2 == 0)
                {
                    local += Work(x, work);
                }
            }

            Interlocked.Add(ref total, local);   // once per chunk, not once per element
        });

        return total;
    }

    private static int Work(int seed, int iterations)
    {
        int acc = seed;
        for (int i = 0; i < iterations; i++)
        {
            acc = acc * 31 + i;
        }

        return acc;
    }
}
