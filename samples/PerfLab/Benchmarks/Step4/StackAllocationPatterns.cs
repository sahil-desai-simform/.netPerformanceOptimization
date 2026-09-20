using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace PerfLab.Benchmarks.Step4;

/// <summary>
/// MemoryOptimizationTricks.md §13 — writing code the JIT can stack-allocate.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *StackAllocationPatterns*
///
/// Every other technique in Step 4 is you doing the work. This one is you getting out of the way:
/// .NET 10's escape analysis will stack-allocate objects it can prove don't outlive the method,
/// which removes the allocation with no change to how the code reads.
///
/// Each pair below is (a shape the JIT can prove) vs (the same logic with one thing added that
/// defeats the proof). The rules are not arbitrary — an object escapes if it is returned, stored
/// in a field, captured by something that escapes, or passed to a method that wasn't inlined.
///
/// The net8.0 baseline job is the control: these optimizations landed in .NET 10, so .NET 8 should
/// allocate on BOTH sides of every pair. If a row shows 0 B on net8.0 too, that row is measuring
/// something else and the conclusion drawn from it would be wrong.
///
/// Confirm the mechanism rather than inferring it from the Allocated column (WaysToMeasure.md §9.1):
///   dotnet run -c Release -f net10.0 -- bench --filter *StackAllocationPatterns* --disasm
///   → a stack-allocated shape has NO call to CORINFO_HELP_NEWSFAST / NEWARR_1_VC
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5, baseline: true)]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class StackAllocationPatterns
{
    private int[] _source = null!;
    private int[] _escapeHatch = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = Enumerable.Range(0, 100).ToArray();
        _escapeHatch = new int[4];
    }

    // ── 1. Small fixed-size array: contained vs escaping ────────────────────────────────────

    [Benchmark(Description = "small array, stays local")]
    public int ArrayContained()
    {
        int[] weights = [1, 2, 3, 4];        // provably local → stack
        int sum = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            sum += weights[i] * i;
        }

        return sum;
    }

    [Benchmark(Description = "small array, stored in a field (escapes)")]
    public int ArrayEscapes()
    {
        int[] weights = [1, 2, 3, 4];
        _escapeHatch = weights;              // ← stored outside the method → heap, always
        int sum = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            sum += weights[i] * i;
        }

        return sum;
    }

    // ── 2. Delegate: contained vs escaping ──────────────────────────────────────────────────

    [Benchmark(Description = "delegate, stays local")]
    public int DelegateContained()
    {
        int offset = 2;
        Func<int, int> shift = x => x + offset;

        int sum = 0;
        for (int i = 0; i < 100; i++)
        {
            sum += shift(i);
        }

        return sum;
    }

    [Benchmark(Description = "delegate, returned via a field (escapes)")]
    public int DelegateEscapes()
    {
        int offset = 2;
        Func<int, int> shift = x => x + offset;
        Escaped = shift;                     // ← escapes

        int sum = 0;
        for (int i = 0; i < 100; i++)
        {
            sum += shift(i);
        }

        return sum;
    }

    public Func<int, int>? Escaped { get; set; }

    // ── 3. Object referenced only through a local struct field ─────────────────────────────

    [Benchmark(Description = "object via non-escaping struct field")]
    public int StructFieldContained()
    {
        var holder = new Holder { Values = [10, 20, 30] };
        return holder.Values[0] + holder.Values[1] + holder.Values[2];
    }

    // ── 4. Enumerating an array through IEnumerable ────────────────────────────────────────

    [Benchmark(Description = "array via IEnumerable (enumerator boxing)")]
    public int ArrayViaInterface()
    {
        IEnumerable<int> source = _source;   // .NET 10 devirtualizes the array interface methods
        int sum = 0;
        foreach (int n in source)
        {
            sum += n;
        }

        return sum;
    }

    [Benchmark(Description = "array directly (control)")]
    public int ArrayDirect()
    {
        int sum = 0;
        for (int i = 0; i < _source.Length; i++)
        {
            sum += _source[i];
        }

        return sum;
    }

    private struct Holder
    {
        public int[] Values;
    }
}
