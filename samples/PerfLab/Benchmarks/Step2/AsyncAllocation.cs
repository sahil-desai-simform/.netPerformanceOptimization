using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step2;

/// <summary>
/// AsyncVsSync.md §11 — where async allocations actually come from.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *AsyncAllocation*
///
/// Three independent sources, which people routinely blame on each other:
///
///   1. The state machine box       — only on suspension. A struct on the stack until then.
///   2. The returned Task           — one per async call that isn't served from a cache.
///   3. ExecutionContext capture    — free until something is actually in the context.
///
/// (3) is the surprising one: AsyncLocal is not "a bit of overhead", it defeats an optimization.
/// With an empty ExecutionContext the runtime skips the capture entirely; put one value in it and
/// every await in the whole call graph starts paying.
/// </summary>
[MemoryDiagnoser]
public class AsyncAllocation
{
    private static readonly AsyncLocal<string?> Ambient = new();

    // Read from a field rather than returning a literal. `=> 42` gets constant-folded to nothing
    // and the baseline measures 0.003 ns — which makes every Ratio in the table meaningless.
    // (WaysToMeasure.md §4.7 pitfall #2, encountered the hard way while writing this file.)
    private int _value = 42;

    [GlobalSetup]
    public void Setup() => _value = 42;

    // ── 1 & 2: state machine and Task allocation ────────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "no await at all")]
    public int NoAsync() => Identity(_value);

    // NoInlining so the baseline is a real method call rather than a field read. An inlined
    // baseline measures ~0.03 ns, which is below BenchmarkDotNet's resolution floor and turns
    // every Ratio in the table into "?".
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int Identity(int v) => v;

    [Benchmark(Description = "async, completes synchronously")]
    public async Task<int> CompletesSynchronously()
    {
        await Task.CompletedTask;      // already complete: no suspension, no box
        return 42;
    }

    [Benchmark(Description = "async, suspends (boxes the state machine)")]
    public async Task<int> Suspends()
    {
        await Task.Yield();            // suspension: the struct is boxed to the heap
        return 42;
    }

    [Benchmark(Description = "Task.Run wrapping sync work")]
    public Task<int> TaskRunWrapper() => Task.Run(static () => 42);

    // ── 3: ExecutionContext flow ────────────────────────────────────────────────────────────

    [Benchmark(Description = "suspends, empty ExecutionContext")]
    public async Task<int> SuspendsWithEmptyContext()
    {
        Ambient.Value = null;
        await Task.Yield();
        return 42;
    }

    [Benchmark(Description = "suspends, AsyncLocal populated")]
    public async Task<int> SuspendsWithAsyncLocal()
    {
        Ambient.Value = "request-42";  // now every await in this graph captures and restores it
        await Task.Yield();
        return 42;
    }

    // ── Opting out of flow. Shown to quantify the cost, NOT as a recommendation — suppressing
    //    flow silently breaks AsyncLocal-based ambient state, which in ASP.NET Core includes the
    //    current Activity for distributed tracing.
    //
    //    NOTE THE SHAPE. SuppressFlow must wrap the LAUNCH of the async work, not span an await:
    //    AsyncFlowControl.Undo() has to run on the thread that called SuppressFlow, and after a
    //    suspension the continuation is usually on a different one. Writing it the obvious way —
    //    `using (SuppressFlow()) { await ...; }` — throws InvalidOperationException at runtime.
    [Benchmark(Description = "suspends, flow suppressed")]
    public async Task<int> SuspendsWithFlowSuppressed()
    {
        Ambient.Value = "request-42";

        Task<int> pending;
        using (ExecutionContext.SuppressFlow())
        {
            pending = SuspendingCore();   // capture (or rather, the lack of it) happens here
        }

        return await pending;
    }

    private static async Task<int> SuspendingCore()
    {
        await Task.Yield();
        return 42;
    }
}
