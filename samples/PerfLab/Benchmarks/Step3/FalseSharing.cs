using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step3;

/// <summary>
/// ParallelismVsConcurrency.md §5 — false sharing.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *FalseSharing*
///
/// The most counter-intuitive cost in parallel code: threads that never touch the same variable,
/// never take a lock, and are algorithmically perfectly independent — and still serialise.
///
/// The cache coherency protocol works in CACHE LINES (64 bytes on x64), not variables. Two
/// counters 4 bytes apart share a line. When thread A writes its counter, every other core's copy
/// of that entire line is invalidated, so thread B's next write to ITS OWN counter has to re-fetch
/// the line. The hardware ping-pongs the line between cores for the whole loop.
///
/// Nothing in the source code says "these threads interact". Only the memory layout does, which is
/// why this is invisible in code review and only shows up when you measure.
///
/// On Windows with admin, add hardware counters to see the mechanism directly:
///   dotnet run -c Release -f net10.0 -- bench --filter *FalseSharing* --counters CacheMisses
/// </summary>
public class FalseSharing
{
    private const int Iterations = 2_000_000;

    private int _threads;
    private Counter[] _packed = null!;
    private PaddedCounter[] _padded = null!;

    [GlobalSetup]
    public void Setup()
    {
        _threads = Environment.ProcessorCount;
        _packed = new Counter[_threads];
        _padded = new PaddedCounter[_threads];

        Console.WriteLine();
        Console.WriteLine($"  Cache line: 64 bytes. Threads: {_threads}.");
        Console.WriteLine($"  sizeof(Counter)       = {Marshal.SizeOf<Counter>()} bytes " +
                          $"→ {64 / Math.Max(1, Marshal.SizeOf<Counter>())} counters share one line");
        Console.WriteLine($"  sizeof(PaddedCounter) = {Marshal.SizeOf<PaddedCounter>()} bytes " +
                          "→ one counter per line");
        Console.WriteLine();
    }

    /// <summary>Counters adjacent in memory. Independent work, shared cache lines.</summary>
    [Benchmark(Baseline = true)]
    public long Packed()
    {
        Counter[] counters = _packed;
        Parallel.For(0, _threads, t =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                counters[t].Value++;
            }
        });

        long sum = 0;
        foreach (Counter c in counters)
        {
            sum += c.Value;
        }

        return sum;
    }

    /// <summary>Identical work; each counter padded onto its own cache line.</summary>
    [Benchmark]
    public long Padded()
    {
        PaddedCounter[] counters = _padded;
        Parallel.For(0, _threads, t =>
        {
            for (int i = 0; i < Iterations; i++)
            {
                counters[t].Value++;
            }
        });

        long sum = 0;
        foreach (PaddedCounter c in counters)
        {
            sum += c.Value;
        }

        return sum;
    }

    /// <summary>
    /// The fix you should usually reach for first: don't share at all. Accumulate into a local,
    /// which the JIT keeps in a register, and combine once at the end. No padding, no layout
    /// tricks, no cache-line reasoning — and it beats both of the above.
    /// </summary>
    [Benchmark]
    public long ThreadLocalAccumulation()
    {
        long total = 0;
        Parallel.For(
            0,
            _threads,
            () => 0L,                                  // per-thread local init
            (t, _, local) =>
            {
                for (int i = 0; i < Iterations; i++)
                {
                    local++;                            // register, not memory
                }

                return local;
            },
            local => Interlocked.Add(ref total, local)); // combine once per thread

        return total;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Counter
    {
        public long Value;
    }

    /// <summary>Padded to a full 64-byte cache line so no two counters can share one.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedCounter
    {
        [FieldOffset(0)]
        public long Value;
    }
}
