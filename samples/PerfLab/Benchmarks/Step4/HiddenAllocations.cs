using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step4;

/// <summary>
/// MemoryOptimizationTricks.md §9 — allocations you didn't write.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *HiddenAllocations*
///
/// Every pair here is (idiomatic, allocating) vs (equivalent, not). None of the allocating
/// versions LOOK like they allocate — that's the point. They are the most common sources of
/// surprise gen0 pressure in otherwise reasonable code.
///
/// Read the Allocated column first, then decide whether the Mean difference justifies the
/// readability difference. For most code the honest answer is no. §15 is about saying so.
/// </summary>
[MemoryDiagnoser]
public class HiddenAllocations
{
    private int[] _numbers = null!;
    private int _threshold;
    private Level _level;

    [GlobalSetup]
    public void Setup()
    {
        _numbers = Enumerable.Range(0, 1_000).ToArray();
        _threshold = 500;
        _level = Level.Warning;
    }

    // ── 1. Boxing ───────────────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "boxing: object holds an int")]
    public long Boxing()
    {
        long sum = 0;
        foreach (int n in _numbers)
        {
            object boxed = n;             // 24 B on x64, per element
            sum += (int)boxed;
        }

        return sum;
    }

    [Benchmark(Description = "no boxing: stay in the value type")]
    public long NoBoxing()
    {
        long sum = 0;
        foreach (int n in _numbers)
        {
            sum += n;
        }

        return sum;
    }

    // ── 2. Closures ─────────────────────────────────────────────────────────────────────────

    [Benchmark(Description = "capturing lambda (display class per call)")]
    public int CapturingLambda()
    {
        int threshold = _threshold;                    // captured → closure object + delegate
        return _numbers.Count(n => n > threshold);
    }

    [Benchmark(Description = "static lambda (delegate cached)")]
    public int StaticLambda() =>
        _numbers.Count(static n => n > 500);           // captures nothing → cached delegate

    [Benchmark(Description = "plain loop (no delegate at all)")]
    public int PlainLoop()
    {
        int threshold = _threshold;
        int count = 0;
        for (int i = 0; i < _numbers.Length; i++)
        {
            if (_numbers[i] > threshold)
            {
                count++;
            }
        }

        return count;
    }

    // ── 3. Enum.ToString ────────────────────────────────────────────────────────────────────

    [Benchmark(Description = "Enum.ToString()")]
    public int EnumToString()
    {
        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            total += _level.ToString().Length;         // reflection-backed, allocates
        }

        return total;
    }

    [Benchmark(Description = "switch expression over the enum")]
    public int EnumSwitch()
    {
        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            total += LevelName(_level).Length;         // interned literal, no allocation
        }

        return total;
    }

    // ── 4. params array vs params ReadOnlySpan [C# 13] ─────────────────────────────────────

    [Benchmark(Description = "params int[] (array per call)")]
    public int ParamsArray()
    {
        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            total += SumArray(1, 2, 3, 4);             // allocates an int[4] every call
        }

        return total;
    }

    [Benchmark(Description = "params ReadOnlySpan<int> [C# 13]")]
    public int ParamsSpan()
    {
        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            total += SumSpan(1, 2, 3, 4);              // compiler uses an inline array on the stack
        }

        return total;
    }

    // ── 5. LINQ chain vs single pass ────────────────────────────────────────────────────────

    [Benchmark(Description = "LINQ chain (enumerators + delegates)")]
    public long LinqChain() =>
        _numbers.Where(n => n % 2 == 0).Select(n => (long)n * 2).Sum();

    [Benchmark(Description = "single pass")]
    public long SinglePass()
    {
        long sum = 0;
        for (int i = 0; i < _numbers.Length; i++)
        {
            int n = _numbers[i];
            if (n % 2 == 0)
            {
                sum += (long)n * 2;
            }
        }

        return sum;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static int SumArray(params int[] values)
    {
        int sum = 0;
        foreach (int v in values)
        {
            sum += v;
        }

        return sum;
    }

    private static int SumSpan(params ReadOnlySpan<int> values)
    {
        int sum = 0;
        foreach (int v in values)
        {
            sum += v;
        }

        return sum;
    }

    private static string LevelName(Level level) => level switch
    {
        Level.Debug => "Debug",
        Level.Info => "Info",
        Level.Warning => "Warning",
        Level.Error => "Error",
        _ => "Unknown",
    };

    public enum Level
    {
        Debug,
        Info,
        Warning,
        Error,
    }
}
