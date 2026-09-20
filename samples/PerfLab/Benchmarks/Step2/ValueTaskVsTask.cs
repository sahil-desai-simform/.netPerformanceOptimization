using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step2;

/// <summary>
/// AsyncVsSync.md §6 — when ValueTask earns its restrictions, and when it doesn't.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *ValueTaskVsTask*
///
/// ValueTask exists for one scenario: a method that USUALLY completes synchronously but sometimes
/// doesn't. The classic shape is a cache with a fallback — a hit returns immediately, a miss goes
/// to I/O.
///
/// [Params] sweeps the hit rate, because the answer genuinely depends on it. At 100% synchronous
/// ValueTask should allocate nothing; at 0% it should be no better than Task and carries all of
/// ValueTask's constraints (await exactly once, never cache it, never touch .Result) for nothing.
///
/// That trade — real constraints for a benefit that only materialises at high hit rates — is the
/// whole decision, and it is why "just use ValueTask everywhere" is bad advice.
/// </summary>
[MemoryDiagnoser]
public class ValueTaskVsTask
{
    [Params(0, 50, 100)]
    public int SynchronousPercent;

    private int _counter;

    [Benchmark(Baseline = true)]
    public async Task<int> WithTask()
    {
        int sum = 0;
        for (int i = 0; i < 100; i++)
        {
            sum += await LookupTask(i);
        }

        return sum;
    }

    [Benchmark]
    public async Task<int> WithValueTask()
    {
        int sum = 0;
        for (int i = 0; i < 100; i++)
        {
            sum += await LookupValueTask(i);
        }

        return sum;
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────

    // Each benchmark makes exactly 100 calls and this cycles with period 100, so the hit COUNT is
    // exactly SynchronousPercent per invocation regardless of where the counter happens to start.
    // That's why there's no [IterationSetup] to reset it — one less source of noise.
    private bool IsCacheHit() => _counter++ % 100 < SynchronousPercent;

    private async Task<int> LookupTask(int key)
    {
        if (IsCacheHit())
        {
            return key;             // still allocates a Task<int> unless the value is cached
        }

        await Task.Yield();
        return key;
    }

    private async ValueTask<int> LookupValueTask(int key)
    {
        if (IsCacheHit())
        {
            return key;             // no allocation: the value lives in the ValueTask struct
        }

        await Task.Yield();
        return key;
    }
}
