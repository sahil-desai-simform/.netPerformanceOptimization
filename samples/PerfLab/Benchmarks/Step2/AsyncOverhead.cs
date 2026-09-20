using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step2;

/// <summary>
/// AsyncVsSync.md §2 / §11 — what the async machinery actually costs.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *AsyncOverhead*
///
/// The point of this class is that async has THREE different cost profiles, and conflating them
/// is where most bad async advice comes from:
///
///   1. Never suspends          → cost is the state machine bookkeeping only, no allocation
///   2. Suspends                → state machine boxes to the heap + a continuation is scheduled
///   3. Suspends, deep chain    → every frame in the chain pays, and the costs compound
///
/// Case 1 is by far the most common in real services (a cache hit, a buffered stream read), which
/// is exactly why ValueTask exists — see ValueTaskVsTask.
/// </summary>
[MemoryDiagnoser]
public class AsyncOverhead
{
    private const int ChainDepth = 10;

    // Two deliberate choices here, both of which I got wrong first:
    //
    // 1. 42 rather than a small value. AsyncTaskMethodBuilder<T> caches completed Task<int>
    //    instances for roughly -1..8 and for bool. Returning 0 would measure that cache, not the
    //    machinery, and the Allocated column would be a comfortable lie.
    //
    // 2. An instance FIELD rather than a const. `=> 42` constant-folds away entirely, the sync
    //    baseline measures 0.003 ns, and every Ratio in the table becomes meaningless.
    //    (WaysToMeasure.md §4.7, pitfalls #1 and #2.)
    private int _result = 42;

    [GlobalSetup]
    public void Setup() => _result = 42;

    [Benchmark(Baseline = true, Description = "sync call")]
    public int Sync() => Compute(_result);

    [Benchmark(Description = "async, never suspends")]
    public async Task<int> AsyncNoSuspension() => await CompletedAsync(_result);

    [Benchmark(Description = "async ValueTask, never suspends")]
    public async ValueTask<int> ValueTaskNoSuspension() => await CompletedValueAsync(_result);

    [Benchmark(Description = "async, suspends once")]
    public async Task<int> AsyncSuspendsOnce()
    {
        await Task.Yield();
        return Compute(_result);
    }

    [Benchmark(Description = "sync chain, depth 10")]
    public int SyncChain() => SyncDepth(ChainDepth, _result);

    [Benchmark(Description = "async chain, depth 10, never suspends")]
    public Task<int> AsyncChainNoSuspension() => AsyncDepth(ChainDepth, _result);

    [Benchmark(Description = "async chain, depth 10, suspends at the bottom")]
    public Task<int> AsyncChainSuspends() => AsyncDepthSuspending(ChainDepth, _result);

    // ── helpers ────────────────────────────────────────────────────────────────────────────

    // NoInlining so the baseline is a real method CALL, which is the honest comparand for an
    // async method call. Inlined, it measures a field read (~0.03 ns) — below BenchmarkDotNet's
    // resolution floor, which makes every Ratio in the table "?" instead of a number.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Compute(int v) => v;

    private static Task<int> CompletedAsync(int v) => Task.FromResult(v);

    private static ValueTask<int> CompletedValueAsync(int v) => new(v);

    private static int SyncDepth(int n, int v) => n == 0 ? Compute(v) : SyncDepth(n - 1, v);

    private static async Task<int> AsyncDepth(int n, int v) =>
        n == 0 ? await CompletedAsync(v) : await AsyncDepth(n - 1, v);

    private static async Task<int> AsyncDepthSuspending(int n, int v)
    {
        if (n == 0)
        {
            await Task.Yield();
            return Compute(v);
        }

        return await AsyncDepthSuspending(n - 1, v);
    }
}
