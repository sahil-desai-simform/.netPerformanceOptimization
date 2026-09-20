using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step2;

/// <summary>
/// AsyncVsSync.md §9 — overlapping independent I/O.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *SequentialVsWhenAll*
///
/// This is the one place in Step 2 where async buys LATENCY rather than scalability, and the
/// effect is arithmetic rather than subtle: N independent 5 ms operations take 5N ms sequentially
/// and ~5 ms overlapped.
///
/// Task.Delay stands in for real I/O deliberately. It has the property that matters (it occupies
/// wall-clock time without occupying a thread) and none of the properties that would make the
/// benchmark unreproducible (network variance, a server to run).
///
/// Read the Allocated column too: WhenAll materialises an array of Tasks, so overlapping is not
/// free — it is just very cheap relative to the latency it removes.
/// </summary>
[MemoryDiagnoser]
public class SequentialVsWhenAll
{
    private const int DelayMs = 5;

    [Params(2, 10)]
    public int Operations;

    [Benchmark(Baseline = true)]
    public async Task<int> Sequential()
    {
        int sum = 0;
        for (int i = 0; i < Operations; i++)
        {
            sum += await FakeIoAsync(i);
        }

        return sum;
    }

    [Benchmark]
    public async Task<int> WhenAll()
    {
        Task<int>[] tasks = new Task<int>[Operations];
        for (int i = 0; i < Operations; i++)
        {
            tasks[i] = FakeIoAsync(i);      // started, not awaited — they overlap from here
        }

        int[] results = await Task.WhenAll(tasks);
        int sum = 0;
        foreach (int r in results)
        {
            sum += r;
        }

        return sum;
    }

    /// <summary>
    /// The trap worth seeing measured: calling an async method inside a LINQ Select and then
    /// awaiting Task.WhenAll looks identical to the version above, but if the sequence is lazy
    /// and enumerated more than once the work runs twice. ToArray() forces exactly one start.
    /// </summary>
    [Benchmark]
    public async Task<int> WhenAllViaLinq()
    {
        Task<int>[] tasks = Enumerable.Range(0, Operations).Select(FakeIoAsync).ToArray();
        int[] results = await Task.WhenAll(tasks);
        return results.Sum();
    }

    private static async Task<int> FakeIoAsync(int i)
    {
        await Task.Delay(DelayMs);
        return i;
    }
}
