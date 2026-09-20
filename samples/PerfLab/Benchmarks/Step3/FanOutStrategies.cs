using System.Threading.Channels;
using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step3;

/// <summary>
/// ParallelismVsConcurrency.md §3 — four ways to run N independent I/O operations.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *FanOutStrategies*
///
/// This is the CONCURRENCY case, not the parallelism case: the work is I/O-bound, so the goal is
/// overlapping waits, not using more cores. Task.Delay stands in for I/O because it has the one
/// property that matters (occupies wall-clock time without occupying a thread) and none of the
/// properties that would make the benchmark unreproducible.
///
/// Read the table with two questions, not one:
///
///   Mean      — how long did it take? (Unbounded wins; it starts everything at once.)
///   Allocated — what did it cost to get there?
///
/// And then the question the table cannot answer: what does this do to the SHARED resources —
/// the connection pool, the downstream service, the thread pool? Unbounded fan-out wins here and
/// takes down production, which is exactly why §3 recommends the bounded options.
/// </summary>
[MemoryDiagnoser]
public class FanOutStrategies
{
    private const int OperationCount = 100;
    private const int IoDelayMs = 2;

    private int[] _items = null!;

    [GlobalSetup]
    public void Setup() => _items = Enumerable.Range(0, OperationCount).ToArray();

    /// <summary>One at a time. N × latency. The baseline nobody should ship for independent work.</summary>
    [Benchmark(Baseline = true)]
    public async Task<int> Sequential()
    {
        int sum = 0;
        foreach (int item in _items)
        {
            sum += await FakeIoAsync(item);
        }

        return sum;
    }

    /// <summary>
    /// All N at once. Fastest here, and a denial-of-service generator in production: no limit on
    /// concurrent connections, no backpressure, and peak memory holds all N operations at once.
    /// </summary>
    [Benchmark]
    public async Task<int> WhenAllUnbounded()
    {
        Task<int>[] tasks = new Task<int>[_items.Length];
        for (int i = 0; i < _items.Length; i++)
        {
            tasks[i] = FakeIoAsync(_items[i]);
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
    /// The modern default for bounded async fan-out. Purpose-built: it does NOT create N tasks up
    /// front, it runs a worker per degree-of-parallelism that pulls from the source.
    /// </summary>
    [Benchmark]
    public async Task<int> ParallelForEachAsync()
    {
        int sum = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8 };

        await Parallel.ForEachAsync(_items, options, async (item, ct) =>
        {
            int result = await FakeIoAsync(item);
            Interlocked.Add(ref sum, result);
        });

        return sum;
    }

    /// <summary>
    /// Manual throttling with a semaphore. What people wrote before Parallel.ForEachAsync existed.
    /// Still correct, still useful when you need per-item control the built-in doesn't give you —
    /// but note it DOES create all N tasks up front, so memory scales with N rather than with DOP.
    /// </summary>
    [Benchmark]
    public async Task<int> SemaphoreThrottled()
    {
        using var gate = new SemaphoreSlim(8);
        int sum = 0;

        Task[] tasks = _items.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                Interlocked.Add(ref sum, await FakeIoAsync(item));
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        return sum;
    }

    /// <summary>
    /// Producer/consumer over a bounded Channel. More moving parts than the above, and it earns
    /// them when the pipeline is long-lived, the producer and consumer rates differ, or you need
    /// real backpressure — a bounded channel makes the producer WAIT rather than buffer.
    /// </summary>
    [Benchmark]
    public async Task<int> ChannelPipeline()
    {
        Channel<int> channel = Channel.CreateBounded<int>(new BoundedChannelOptions(16)
        {
            SingleWriter = true,     // lets the implementation skip writer synchronisation
            SingleReader = false,
        });

        int sum = 0;

        Task producer = Task.Run(async () =>
        {
            foreach (int item in _items)
            {
                await channel.Writer.WriteAsync(item);
            }

            channel.Writer.Complete();
        });

        Task[] consumers = new Task[8];
        for (int c = 0; c < consumers.Length; c++)
        {
            consumers[c] = Task.Run(async () =>
            {
                await foreach (int item in channel.Reader.ReadAllAsync())
                {
                    Interlocked.Add(ref sum, await FakeIoAsync(item));
                }
            });
        }

        await producer;
        await Task.WhenAll(consumers);
        return sum;
    }

    private static async Task<int> FakeIoAsync(int i)
    {
        await Task.Delay(IoDelayMs);
        return i;
    }
}
