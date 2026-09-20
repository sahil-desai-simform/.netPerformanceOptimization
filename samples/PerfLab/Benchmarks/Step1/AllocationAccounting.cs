using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step1;

/// <summary>
/// WaysToMeasure.md §7.3 — measuring allocation directly, without a profiler.
///
/// GC.GetAllocatedBytesForCurrentThread() is cheap, precise, and deterministic, which makes
/// it the basis of the "this hot path must not allocate" unit test described in §7.3.
/// This class both benchmarks the patterns and prints the exact per-call byte counts, so you
/// can see how BenchmarkDotNet's Allocated column is derived.
///
///   dotnet run -c Release -f net10.0 -- --filter *AllocationAccounting*
/// </summary>
[MemoryDiagnoser(displayGenColumns: true)]
public class AllocationAccounting
{
    private int[] _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = Enumerable.Range(0, 1_000).ToArray();
        Report();
    }

    /// <summary>Boxing: each int becomes a heap object.</summary>
    [Benchmark(Baseline = true)]
    public long BoxingSum()
    {
        long sum = 0;
        foreach (int n in _data)
        {
            object boxed = n;          // 24 bytes on x64, per element
            sum += (int)boxed;
        }

        return sum;
    }

    /// <summary>Closure capture: one display class + one delegate per call.</summary>
    [Benchmark]
    public long ClosureSum()
    {
        int offset = 1;
        return _data.Sum(n => n + offset);   // captures 'offset'
    }

    /// <summary>Static lambda: no capture, so the delegate is cached by the compiler.</summary>
    [Benchmark]
    public long StaticLambdaSum() => _data.Sum(static n => n + 1);

    /// <summary>Plain loop over a span: zero managed allocations.</summary>
    [Benchmark]
    public long SpanSum()
    {
        long sum = 0;
        ReadOnlySpan<int> span = _data;
        for (int i = 0; i < span.Length; i++)
        {
            sum += span[i] + 1;
        }

        return sum;
    }

    /// <summary>
    /// Prints the same numbers BenchmarkDotNet's Allocated column will report, measured with
    /// the in-process API. Runs once during GlobalSetup, before the benchmark table appears.
    /// </summary>
    private void Report()
    {
        Console.WriteLine();
        Console.WriteLine("  GC.GetAllocatedBytesForCurrentThread() — bytes per call (100-call average)");
        Console.WriteLine("  ------------------------------------------------------------------------");
        Measure(nameof(BoxingSum), () => BoxingSum());
        Measure(nameof(ClosureSum), () => ClosureSum());
        Measure(nameof(StaticLambdaSum), () => StaticLambdaSum());
        Measure(nameof(SpanSum), () => SpanSum());
        Console.WriteLine();

        static void Measure(string name, Func<long> action)
        {
            for (int i = 0; i < 50; i++)
            {
                action();      // JIT + tiering warm-up; measuring cold code measures the JIT
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                action();
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            Console.WriteLine($"  {name,-20} {(after - before) / 100.0,10:N1} bytes/call");
        }
    }
}
