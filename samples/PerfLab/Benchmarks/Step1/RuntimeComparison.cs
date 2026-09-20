using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace PerfLab.Benchmarks.Step1;

/// <summary>
/// WaysToMeasure.md §4.5 — "should we upgrade the runtime?", answered with evidence
/// instead of a blog post.
///
/// BenchmarkDotNet builds and runs this class once per target runtime and puts all three
/// in one comparison table with net8.0 as the baseline.
///
///   dotnet run -c Release -f net10.0 -- --filter *RuntimeComparison*
///
/// Each benchmark targets a specific .NET 9/10 codegen improvement:
///
///   ArrayViaInterface     → array interface method devirtualization        [.NET 10]
///   SmallValueTypeArray   → stack allocation of small value-type arrays    [.NET 10]
///   SmallRefTypeArray     → stack allocation of small reference-type arrays[.NET 10]
///   NonEscapingDelegate   → escape analysis for delegates                  [.NET 10]
///   StructThroughCall     → physical promotion into shared registers       [.NET 10]
///
/// Watch the Allocated column as much as the Mean column: where stack allocation kicks in,
/// allocations go to 0 and the Mean follows. Confirm with [DisassemblyDiagnoser] that the
/// CORINFO_HELP_NEW* call is gone (§9.1).
///
/// NOTE ON BASELINES: the baseline here is the net8.0 JOB, not a method. That makes every
/// Ratio read as "this pattern on this runtime vs. the same pattern on .NET 8" — which is the
/// question an upgrade decision actually asks. Marking a method [Benchmark(Baseline = true)]
/// as well would give two baselines in one group and BenchmarkDotNet will refuse to run.
/// For the same reason, do not pass --runtimes or --job on the command line for this class;
/// the attributes already define the matrix.
/// </summary>
// warmup/iteration counts kept modest so the whole 3-runtime matrix finishes in a few minutes.
// Drop the overrides for publication-quality numbers.
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5, baseline: true)]
[SimpleJob(RuntimeMoniker.Net90, warmupCount: 3, iterationCount: 5)]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class RuntimeComparison
{
    private int[] _array = null!;

    [GlobalSetup]
    public void Setup() => _array = Enumerable.Range(0, 1_000).ToArray();

    /// <summary>Direct indexing — already well optimized everywhere. The control.</summary>
    [Benchmark]
    public int ArrayDirect()
    {
        int sum = 0;
        for (int i = 0; i < _array.Length; i++)
        {
            sum += _array[i];
        }

        return sum;
    }

    /// <summary>
    /// Same loop through IEnumerable&lt;int&gt;. Pre-.NET 10 the array's interface methods
    /// could not be devirtualized, so every MoveNext/Current stayed a virtual call and
    /// blocked inlining and stack allocation of the enumerator.
    /// </summary>
    [Benchmark]
    public int ArrayViaInterface()
    {
        IEnumerable<int> source = _array;
        int sum = 0;
        foreach (int n in source)
        {
            sum += n;
        }

        return sum;
    }

    /// <summary>Small fixed-size array of a value type with no GC pointers.</summary>
    [Benchmark]
    public int SmallValueTypeArray()
    {
        int[] numbers = [1, 2, 3, 4];
        int sum = 0;
        for (int i = 0; i < numbers.Length; i++)
        {
            sum += numbers[i];
        }

        return sum;
    }

    /// <summary>Small fixed-size array of a reference type — heap-allocated before .NET 10.</summary>
    [Benchmark]
    public int SmallRefTypeArray()
    {
        string[] words = ["alpha", "beta", "gamma"];
        int total = 0;
        foreach (string w in words)
        {
            total += w.Length;
        }

        return total;
    }

    /// <summary>
    /// The Func object never leaves the method. .NET 10's escape analysis stack-allocates it
    /// (the closure display class itself is still heap-allocated — see the remaining
    /// CORINFO_HELP_NEWSFAST in the docs' asm listing).
    /// </summary>
    [Benchmark]
    public int NonEscapingDelegate()
    {
        int local = 1;
        Func<int, int> add = x => x + local;

        int sum = 0;
        for (int i = 0; i < 100; i++)
        {
            sum += add(i);
        }

        return sum;
    }

    /// <summary>
    /// Two int fields must be packed into one 8-byte register by the x64 calling convention.
    /// Before .NET 10 the JIT round-tripped them through memory to do it.
    /// </summary>
    [Benchmark]
    public int StructThroughCall()
    {
        int sum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            sum += Consume(new Point(i, i + 1));
        }

        return sum;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Consume(Point p) => p.X + p.Y;

    private readonly struct Point(int x, int y)
    {
        public int X { get; } = x;
        public int Y { get; } = y;
    }
}
