using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace PerfLab.Benchmarks.Step1;

/// <summary>
/// WaysToMeasure.md §4.7 — pitfalls that produce fake results, demonstrated so you can see
/// the fake numbers with your own eyes rather than taking the warning on faith.
///
///   dotnet run -c Release -f net10.0 -- --filter *MeasurementPitfalls*
///
/// Expected shape of the results:
///   DeadCodeEliminated  ≈ a few ns, or effectively free   ← THE LIE
///   Consumed            ≈ the real cost
///   ConstantFolded      ≈ ~0 ns                           ← THE LIE
///   NotFolded           ≈ the real cost
///
/// If DeadCodeEliminated and Consumed report the same number on your machine, the JIT chose
/// not to eliminate this particular call — the hazard is real regardless, it just isn't
/// deterministic, which is exactly what makes it dangerous.
/// </summary>
[MemoryDiagnoser]
public class MeasurementPitfalls
{
    private const int ConstInput = 40;

    // Instance field assigned in setup: the JIT cannot fold it.
    private int _input;

    private readonly Consumer _consumer = new();

    [GlobalSetup]
    public void Setup() => _input = 40;

    // ---- Pitfall 1: dead-code elimination ------------------------------------------------

    /// <summary>Result discarded. The JIT is free to delete the whole call.</summary>
    [Benchmark]
    public void DeadCodeEliminated() => _ = Fib(_input % 25);

    /// <summary>Returning the value makes BenchmarkDotNet consume it.</summary>
    [Benchmark]
    public int Consumed() => Fib(_input % 25);

    // ---- Pitfall 2: constant folding ------------------------------------------------------

    /// <summary>A const argument lets the JIT precompute at compile time.</summary>
    [Benchmark]
    public int ConstantFolded() => Multiply(ConstInput, ConstInput);

    /// <summary>The same work, from a field the JIT cannot see through.</summary>
    [Benchmark]
    public int NotFolded() => Multiply(_input, _input);

    // ---- Pitfall 3: void / multi-value work needs an explicit Consumer ---------------------

    /// <summary>
    /// Enumerating lazily and throwing the values away measures nothing: LINQ is lazy, so
    /// without consuming the sequence the query never actually runs.
    /// </summary>
    [Benchmark]
    public void LazyNeverRuns() => _ = Query();

    /// <summary>Consumer.Consume forces every element to materialise.</summary>
    [Benchmark]
    public void ConsumedProperly()
    {
        foreach (int x in Query())
        {
            _consumer.Consume(x);
        }
    }

    private IEnumerable<int> Query() => Enumerable.Range(0, 1_000).Where(i => i % 3 == 0).Select(i => i * 2);

    private static int Multiply(int a, int b) => a * b;

    private static int Fib(int n) => n <= 1 ? n : Fib(n - 1) + Fib(n - 2);
}
