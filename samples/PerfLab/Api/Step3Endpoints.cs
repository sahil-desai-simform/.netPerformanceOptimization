using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PerfLab.Api;

/// <summary>
/// Step 3 — ParallelismVsConcurrency.md.
///
/// The central claim of Step 3 is one a microbenchmark actively HIDES: parallelising work inside a
/// request makes that request faster when it is the only request, and makes the SERVICE slower
/// when it isn't. A benchmark runs one operation at a time, so it only ever shows you the half
/// that flatters the change.
///
/// The demonstration — run both, one caller then many:
///
///   bombardier -c 1   -d 20s "http://localhost:5199/step3/parallel-in-request?items=2000"
///   bombardier -c 1   -d 20s "http://localhost:5199/step3/sequential-in-request?items=2000"
///     → parallel wins on per-request latency. Obviously.
///
///   bombardier -c 100 -d 30s "http://localhost:5199/step3/parallel-in-request?items=2000"
///   bombardier -c 100 -d 30s "http://localhost:5199/step3/sequential-in-request?items=2000"
///     → sequential wins on THROUGHPUT, often by a lot. The server was already parallel across
///       requests; the parallel version just added scheduling overhead and stole threads from
///       its neighbours.
/// </summary>
internal static class Step3Endpoints
{
    public static readonly string[] Catalog =
    [
        "GET /step3/sequential-in-request?items=2000 — CPU work, plain loop. The control.",
        "GET /step3/parallel-in-request?items=2000   — same work via Parallel.For. Faster alone,",
        "                                              slower in aggregate. Compare at -c 1 vs -c 100.",
        "GET /step3/bounded-parallel?items=2000&dop=4— Parallel.For with MaxDegreeOfParallelism.",
        "GET /step3/unbounded-fanout?n=200           — Task.WhenAll with no limit. Watch the pool.",
        "GET /step3/bounded-fanout?n=200&dop=8       — Parallel.ForEachAsync. The right default.",
        "GET /step3/channel-pipeline?n=200&workers=4 — producer/consumer with backpressure.",
        "GET /step3/contended-cache?n=500            — one lock serialising every request.",
        "GET /step3/sharded-cache?n=500              — same work, contention removed by sharding.",
    ];

    private static readonly object CacheGate = new();
    private static readonly Dictionary<int, int> SharedCache = [];
    private static readonly Dictionary<int, int>[] ShardedCache =
        Enumerable.Range(0, 32).Select(_ => new Dictionary<int, int>()).ToArray();
    private static readonly object[] ShardGates =
        Enumerable.Range(0, 32).Select(_ => new object()).ToArray();

    public static void MapStep3Endpoints(this WebApplication app)
    {
        // ── Parallelism inside a request: the trade-off, made runnable ─────────────────────
        app.MapGet("/step3/sequential-in-request", (int items = 2000) =>
        {
            long start = Stopwatch.GetTimestamp();
            long sum = 0;
            for (int i = 0; i < items; i++)
            {
                sum += Work(i);
            }

            return Results.Ok(new
            {
                mode = "sequential",
                items,
                sum,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            });
        });

        app.MapGet("/step3/parallel-in-request", (int items = 2000) =>
        {
            long start = Stopwatch.GetTimestamp();
            long sum = 0;
            Parallel.For(0, items, () => 0L, (i, _, local) => local + Work(i),
                local => Interlocked.Add(ref sum, local));

            return Results.Ok(new
            {
                mode = "parallel-unbounded",
                items,
                sum,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                note = "compare to /step3/sequential-in-request at the SAME items value. Below the "
                     + "crossover (§4.1) this is slower even with one caller; above it, it wins "
                     + "alone and still loses on throughput at -c 100. Try items=500 and items=50000.",
            });
        });

        app.MapGet("/step3/bounded-parallel", (int items = 2000, int dop = 4) =>
        {
            long start = Stopwatch.GetTimestamp();
            long sum = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, dop) };

            Parallel.For(0, items, options, () => 0L, (i, _, local) => local + Work(i),
                local => Interlocked.Add(ref sum, local));

            return Results.Ok(new
            {
                mode = "parallel-bounded",
                items,
                dop,
                sum,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            });
        });

        // ── Fan-out: unbounded vs bounded ──────────────────────────────────────────────────
        app.MapGet("/step3/unbounded-fanout", async (int n = 200) =>
        {
            long start = Stopwatch.GetTimestamp();
            int threadsBefore = ThreadPool.ThreadCount;

            Task[] tasks = Enumerable.Range(0, n).Select(static _ => Task.Delay(20)).ToArray();
            await Task.WhenAll(tasks);

            return Results.Ok(new
            {
                mode = "unbounded",
                n,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                concurrentOperations = n,       // ← the number with no upper bound
                threadsBefore,
                threadsAfter = ThreadPool.ThreadCount,
                note = "all n in flight at once. Elapsed stays ~20 ms however large n gets, which is "
                     + "exactly why this looks good in a benchmark: the cost is borne by the "
                     + "connection pool and the downstream service, neither of which is measured here.",
            });
        });

        app.MapGet("/step3/bounded-fanout", async (int n = 200, int dop = 8) =>
        {
            long start = Stopwatch.GetTimestamp();
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, dop) };

            await Parallel.ForEachAsync(Enumerable.Range(0, n), options,
                static async (_, ct) => await Task.Delay(20, ct));

            return Results.Ok(new
            {
                mode = "bounded",
                n,
                dop,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                note = "at most dop in flight; predictable pressure on every shared resource",
            });
        });

        // ── Channel pipeline with real backpressure ────────────────────────────────────────
        app.MapGet("/step3/channel-pipeline", async (int n = 200, int workers = 4) =>
        {
            long start = Stopwatch.GetTimestamp();

            Channel<int> channel = Channel.CreateBounded<int>(new BoundedChannelOptions(16)
            {
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,   // ← the producer WAITS. That's backpressure.
            });

            long processed = 0;

            Task producer = Task.Run(async () =>
            {
                for (int i = 0; i < n; i++)
                {
                    await channel.Writer.WriteAsync(i);
                }

                channel.Writer.Complete();
            });

            Task[] consumers = Enumerable.Range(0, Math.Max(1, workers)).Select(_ => Task.Run(async () =>
            {
                await foreach (int _ in channel.Reader.ReadAllAsync())
                {
                    await Task.Delay(20);
                    Interlocked.Increment(ref processed);
                }
            })).ToArray();

            await producer;
            await Task.WhenAll(consumers);

            return Results.Ok(new
            {
                mode = "channel",
                n,
                workers,
                processed,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                note = "bounded capacity means a slow consumer slows the producer instead of OOMing",
            });
        });

        // ── Contention: one lock vs sharded locks ──────────────────────────────────────────
        app.MapGet("/step3/contended-cache", (int n = 500) =>
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                lock (CacheGate)                       // every request, every iteration, one lock
                {
                    SharedCache[i % 1000] = i;
                }
            }

            return Results.Ok(new
            {
                mode = "single lock",
                n,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                note = "watch monitor-lock-contention-count under concurrent load",
            });
        });

        app.MapGet("/step3/sharded-cache", (int n = 500) =>
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                int key = i % 1000;
                int shard = key & 31;                  // same work, 32× fewer collisions
                lock (ShardGates[shard])
                {
                    ShardedCache[shard][key] = i;
                }
            }

            return Results.Ok(new
            {
                mode = "32 shards",
                n,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                note = "identical semantics; contention divided by the shard count",
            });
        });
    }

    private static long Work(int seed)
    {
        int acc = seed;
        for (int i = 0; i < 200; i++)
        {
            acc = acc * 31 + i;
        }

        return acc;
    }
}
