using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PerfLab.Api;

/// <summary>
/// Step 2 — AsyncVsSync.md.
///
/// These endpoints exist because the central claim of Step 2 cannot be shown in a microbenchmark.
/// BenchmarkDotNet measures one operation at a time on a quiet machine; thread-pool starvation is
/// a property of MANY operations competing for a shared, slowly-growing pool. You need concurrent
/// load to see it at all.
///
/// The demonstration:
///
///   bombardier -c 200 -d 30s http://localhost:5199/step2/async-io?ms=200
///   bombardier -c 200 -d 30s http://localhost:5199/step2/sync-io?ms=200
///
/// Same work, same latency per request in isolation. Under 200 concurrent connections the async
/// version holds throughput near 1000 req/s while the sync version collapses to roughly the pool
/// size — and `dotnet-counters` shows the divergence that names the cause: queue-length climbing
/// while cpu-usage stays near idle.
/// </summary>
internal static class Step2Endpoints
{
    public static readonly string[] Catalog =
    [
        "GET  /step2/async-io?ms=200        — correct: awaits, frees the thread. The control.",
        "GET  /step2/sync-io?ms=200         — blocking 'I/O'. Holds a pool thread for the duration.",
        "GET  /step2/sync-over-async?ms=200 — .GetAwaiter().GetResult(). Worst of both worlds.",
        "GET  /step2/async-over-sync?ms=200 — Task.Run around sync work. Hides, does not fix.",
        "GET  /step2/fire-and-forget        — unobserved task; the exception vanishes.",
        "GET  /step2/cancellable?ms=5000    — honours RequestAborted. Disconnect and watch.",
        "GET  /step2/threadpool             — live pool stats, no external tooling needed.",
    ];

    public static void MapStep2Endpoints(this WebApplication app)
    {
        // ── The control: real async I/O. No thread is held while waiting. ───────────────────
        app.MapGet("/step2/async-io", async (int ms = 200) =>
        {
            long start = Stopwatch.GetTimestamp();
            await Task.Delay(ms);
            return Results.Ok(new
            {
                mode = "async",
                ms,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                threadHeld = false,
            });
        });

        // ── Blocking I/O. One request = one thread, occupied, for the whole duration. ───────
        //    Thread.Sleep stands in for a synchronous DB driver or File.ReadAllBytes.
        app.MapGet("/step2/sync-io", (int ms = 200) =>
        {
            long start = Stopwatch.GetTimestamp();
            Thread.Sleep(ms);
            return Results.Ok(new
            {
                mode = "sync",
                ms,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                threadHeld = true,
            });
        });

        // ── Sync-over-async: an async API consumed synchronously. ──────────────────────────
        //    Strictly worse than /sync-io — it pays the full async machinery cost AND blocks a
        //    thread. In a context with a SynchronizationContext this also deadlocks outright.
        app.MapGet("/step2/sync-over-async", (int ms = 200) =>
        {
            long start = Stopwatch.GetTimestamp();
            Task.Delay(ms).GetAwaiter().GetResult();
            return Results.Ok(new
            {
                mode = "sync-over-async",
                ms,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                note = "async machinery cost, paid, for nothing — and a thread blocked anyway",
            });
        });

        // ── Async-over-sync: Task.Run around blocking work. ────────────────────────────────
        //    Frees the REQUEST thread but occupies a different pool thread. Net thread usage is
        //    unchanged; you have only moved the problem somewhere harder to see.
        app.MapGet("/step2/async-over-sync", async (int ms = 200) =>
        {
            long start = Stopwatch.GetTimestamp();
            await Task.Run(() => Thread.Sleep(ms));
            return Results.Ok(new
            {
                mode = "async-over-sync",
                ms,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                note = "a pool thread is still blocked — just not this one",
            });
        });

        // ── Fire-and-forget: the request returns, the work is unobserved. ──────────────────
        app.MapGet("/step2/fire-and-forget", () =>
        {
            _ = Task.Run(static async () =>
            {
                await Task.Delay(50);
                throw new InvalidOperationException("nobody will ever see this");
            });

            return Results.Ok(new
            {
                note = "returned immediately; the background task will fault and be swallowed",
                whatToDoInstead = "IHostedService / Channel + a consumer, so failures are observable",
            });
        });

        // ── Cancellation done properly. Disconnect mid-request and watch the log. ──────────
        app.MapGet("/step2/cancellable", async (HttpContext ctx, int ms = 5000) =>
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                // ctx.RequestAborted fires when the client disconnects. Plumbing it through means
                // abandoned work stops instead of running to completion for nobody.
                await Task.Delay(ms, ctx.RequestAborted);
                return Results.Ok(new { completed = true, ms });
            }
            catch (OperationCanceledException)
            {
                double wasted = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Console.WriteLine($"  /step2/cancellable: client disconnected after {wasted:N0} ms — work abandoned");
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
        });

        // ── Pool stats without attaching anything. ─────────────────────────────────────────
        app.MapGet("/step2/threadpool", () =>
        {
            ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
            ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);
            ThreadPool.GetMinThreads(out int minWorker, out int minIo);

            return Results.Ok(new
            {
                processorCount = Environment.ProcessorCount,
                worker = new { min = minWorker, max = maxWorker, busy = maxWorker - availWorker },
                io = new { min = minIo, max = maxIo, busy = maxIo - availIo },
                pendingWorkItems = ThreadPool.PendingWorkItemCount,   // the starvation signal
                totalThreads = ThreadPool.ThreadCount,
                completedWorkItems = ThreadPool.CompletedWorkItemCount,
            });
        });
    }
}
