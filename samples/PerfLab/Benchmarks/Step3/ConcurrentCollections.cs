using System.Collections.Concurrent;
using System.Collections.Frozen;
using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step3;

/// <summary>
/// ParallelismVsConcurrency.md §7 — concurrent collections, and the ConcurrentDictionary trap.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *ConcurrentCollections*
///
/// Two separate lessons:
///
///   1. READ-MOSTLY lookup. ConcurrentDictionary is not free for reads, and if the contents never
///      change after startup, FrozenDictionary [.NET 8] is built for exactly that shape.
///
///   2. GetOrAdd is NOT ATOMIC with respect to its factory. Under contention the factory can run
///      multiple times for the same key — only one result is stored, the rest are discarded. If
///      the factory opens a connection, starts a timer, or costs money, you have a bug that only
///      appears under load. GlobalSetup prints proof.
/// </summary>
[MemoryDiagnoser]
public class ConcurrentCollections
{
    private const int Entries = 1_000;

    private Dictionary<string, int> _dictionary = null!;
    private ConcurrentDictionary<string, int> _concurrent = null!;
    private FrozenDictionary<string, int> _frozen = null!;
    private string[] _keys = null!;

    [GlobalSetup]
    public void Setup()
    {
        _keys = Enumerable.Range(0, Entries).Select(i => $"key-{i}").ToArray();
        _dictionary = _keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        _concurrent = new ConcurrentDictionary<string, int>(_dictionary);
        _frozen = _dictionary.ToFrozenDictionary();

        DemonstrateGetOrAddIsNotAtomic();
    }

    // ── 1. Read-mostly lookup ───────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "Dictionary (not thread-safe)")]
    public long PlainDictionary()
    {
        long sum = 0;
        foreach (string key in _keys)
        {
            sum += _dictionary[key];
        }

        return sum;
    }

    [Benchmark(Description = "ConcurrentDictionary")]
    public long Concurrent()
    {
        long sum = 0;
        foreach (string key in _keys)
        {
            sum += _concurrent[key];
        }

        return sum;
    }

    [Benchmark(Description = "FrozenDictionary [.NET 8]")]
    public long Frozen()
    {
        long sum = 0;
        foreach (string key in _keys)
        {
            sum += _frozen[key];
        }

        return sum;
    }

    // ── 2. Concurrent writes ────────────────────────────────────────────────────────────────

    [Benchmark(Description = "ConcurrentDictionary: parallel GetOrAdd")]
    public int ParallelGetOrAdd()
    {
        var map = new ConcurrentDictionary<int, int>();
        Parallel.For(0, Entries, i => map.GetOrAdd(i, static k => k * 2));
        return map.Count;
    }

    [Benchmark(Description = "Dictionary + lock: parallel add")]
    public int LockedDictionaryAdd()
    {
        var map = new Dictionary<int, int>();
        object gate = new();

        Parallel.For(0, Entries, i =>
        {
            lock (gate)
            {
                map.TryAdd(i, i * 2);
            }
        });

        return map.Count;
    }

    /// <summary>
    /// Proof, printed during setup, that GetOrAdd's value factory is not run under a lock.
    /// Hammer one key from every core and count factory invocations. Expected: 1. Observed: more.
    /// </summary>
    private static void DemonstrateGetOrAddIsNotAtomic()
    {
        const int Attempts = 200;
        int totalExtraInvocations = 0;

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            var map = new ConcurrentDictionary<string, object>();
            int factoryCalls = 0;

            Parallel.For(0, Environment.ProcessorCount, _ =>
                map.GetOrAdd("the-one-key", _ =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    Thread.SpinWait(200);        // widen the window; a real factory does real work
                    return new object();
                }));

            totalExtraInvocations += factoryCalls - 1;
        }

        Console.WriteLine();
        Console.WriteLine("  ConcurrentDictionary.GetOrAdd — is the value factory atomic?");
        Console.WriteLine("  ------------------------------------------------------------");
        Console.WriteLine($"  {Attempts} attempts, {Environment.ProcessorCount} threads racing on ONE key.");
        Console.WriteLine($"  Factory invocations beyond the required 1: {totalExtraInvocations}");
        Console.WriteLine(totalExtraInvocations > 0
            ? "  → NOT atomic. Every extra invocation created a value that was then thrown away.\n" +
              "    Fine for a pure computation. A bug if the factory opens a connection, starts a\n" +
              "    timer, or has any other side effect. Use Lazy<T> as the value to fix it."
            : "  → No duplicates on this run. That is TIMING, not a guarantee — the contract does\n" +
              "    not promise single invocation, so do not rely on what you just observed.");
        Console.WriteLine();
    }
}
