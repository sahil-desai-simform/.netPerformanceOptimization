using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step2;

/// <summary>
/// AsyncVsSync.md §10 — `return DoAsync()` vs `return await DoAsync()`.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *ElidedAsync*
///
/// Eliding the async/await keywords removes one state machine per frame. The saving is real but
/// small; the behavioural changes are neither. Elision moves exceptions and disposal, and that is
/// what should drive the decision — not the nanoseconds in this table.
///
/// Rule of thumb that survives contact with production:
///   - Elide only in thin pass-through wrappers with no try/catch, no using, and no lock.
///   - Keep `await` everywhere else. In particular NEVER elide inside `using` (see below).
///
/// Run DemonstrateBehaviouralDifference() — it is called from GlobalSetup and prints proof that
/// the two forms are not interchangeable.
/// </summary>
[MemoryDiagnoser]
public class ElidedAsync
{
    [GlobalSetup]
    public void Setup() => DemonstrateBehaviouralDifference();

    [Benchmark(Baseline = true, Description = "return await Inner()")]
    public Task<int> Awaited() => AwaitedWrapper();

    [Benchmark(Description = "return Inner()  (elided)")]
    public Task<int> Elided() => ElidedWrapper();

    [Benchmark(Description = "return await Inner(), 3 frames")]
    public Task<int> AwaitedChain() => AwaitedWrapper3();

    [Benchmark(Description = "return Inner(), 3 frames (elided)")]
    public Task<int> ElidedChain() => ElidedWrapper3();

    // ── the shapes being compared ──────────────────────────────────────────────────────────

    private static async Task<int> AwaitedWrapper() => await LeafAsync();

    private static Task<int> ElidedWrapper() => LeafAsync();

    private static async Task<int> AwaitedWrapper3() => await AwaitedWrapper2();
    private static async Task<int> AwaitedWrapper2() => await AwaitedWrapper();

    private static Task<int> ElidedWrapper3() => ElidedWrapper2();
    private static Task<int> ElidedWrapper2() => ElidedWrapper();

    private static Task<int> LeafAsync() => Task.FromResult(42);

    // ── why the nanoseconds are not the point ──────────────────────────────────────────────

    /// <summary>
    /// Prints the two behavioural differences that matter. Both are silent bugs — nothing throws
    /// at compile time, and the elided version often passes tests that don't exercise failure.
    /// </summary>
    private static void DemonstrateBehaviouralDifference()
    {
        Console.WriteLine();
        Console.WriteLine("  Why `return Inner()` is not simply a faster `return await Inner()`");
        Console.WriteLine("  -----------------------------------------------------------------");

        // ── 1. WHERE a validation exception surfaces ────────────────────────────────────────
        // An `async` method never throws synchronously: everything, including argument
        // validation, is captured into the returned Task. Elide the keywords and validation
        // throws at CALL time instead — before the caller has a Task to await.
        try
        {
            Task<int> faulted = ValidatingAwaited(null);
            Console.WriteLine($"  awaited : call returned normally, Task.IsFaulted={faulted.IsFaulted}");
            Console.WriteLine("            → the caller sees it at `await`, where it expects to");
            _ = faulted.Exception;   // observe it so it isn't an unobserved task exception
        }
        catch (ArgumentNullException)
        {
            Console.WriteLine("  awaited : threw synchronously (unexpected)");
        }

        try
        {
            _ = ValidatingElided(null);
            Console.WriteLine("  elided  : call returned normally (unexpected)");
        }
        catch (ArgumentNullException)
        {
            Console.WriteLine("  elided  : threw SYNCHRONOUSLY at call time");
            Console.WriteLine("            → a caller whose try/catch wraps only the `await`");
            Console.WriteLine("              never catches it. Same code, different call site.");
        }

        // ── 2. Disposal ordering: the one that silently corrupts ───────────────────────────
        Console.WriteLine("  using   : " + DisposeOrderingAsync().GetAwaiter().GetResult());
        Console.WriteLine();
    }

    /// <summary>Validation is captured into the Task, like every other async exception.</summary>
    private static async Task<int> ValidatingAwaited(string? arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        return await LeafAsync();
    }

    /// <summary>Not an async method, so validation throws before a Task ever exists.</summary>
    private static Task<int> ValidatingElided(string? arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        return LeafAsync();
    }

    /// <summary>
    /// The elided form disposes the resource BEFORE the returned Task completes, because the
    /// `using` block exits as soon as the Task is handed back rather than when it finishes.
    /// Anything still using that resource is now operating on a disposed object.
    /// </summary>
    private static async Task<string> DisposeOrderingAsync()
    {
        var tracker = new DisposalTracker();

        Task<int> elided;
        using (tracker)
        {
            elided = SlowLeafAsync(tracker);       // elided: no await, so `using` exits immediately
        }

        bool disposedBeforeCompletion = tracker.Disposed && !elided.IsCompleted;
        await elided;

        return disposedBeforeCompletion
            ? "resource was DISPOSED while the elided Task was still running — this is the bug"
            : "resource outlived the Task on this run (timing-dependent, which is its own problem)";
    }

    private static async Task<int> SlowLeafAsync(DisposalTracker tracker)
    {
        await Task.Delay(20);
        return tracker.Disposed ? -1 : 42;
    }

    private sealed class DisposalTracker : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
