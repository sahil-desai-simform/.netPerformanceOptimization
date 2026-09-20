using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PerfLab.Api;

/// <summary>
/// Step 1 — WaysToMeasure.md.
///
/// Each endpoint produces a DIFFERENT signature in dotnet-counters. Run each under load with
/// `dotnet-counters monitor` open and learn the shapes; that recognition is most of production
/// triage.
/// </summary>
internal static class Step1Endpoints
{
    public static readonly string[] Catalog =
    [
        "GET  /step1/healthy?ms=5      — well-behaved baseline. Everything flat.",
        "GET  /step1/cpu?ms=200        — CPU-bound. cpu-usage saturates; queue stays ~0.",
        "GET  /step1/alloc?kb=512      — allocation churn. alloc-rate + gen-0-gc-count spike.",
        "GET  /step1/loh?kb=256        — LOH churn. loh-size grows; gen-2 collections rise.",
        "GET  /step1/leak?n=1000       — retention. gc-heap-size floor rises after every gen2.",
        "GET  /step1/leak/stats        — how many objects are currently retained.",
        "POST /step1/leak/clear        — release them; watch the floor drop.",
        "GET  /step1/block?ms=200      — sync-over-async. queue-length climbs, cpu stays low.",
        "GET  /step1/contend?ms=5      — lock contention. monitor-lock-contention-count spikes.",
        "GET  /step1/exceptions?n=100  — exception-count. Exceptions are not free.",
    ];

    private static readonly ConcurrentBag<byte[]> Leaked = [];
    private static readonly object Gate = new();

    public static void MapStep1Endpoints(this WebApplication app)
    {
        AppMetrics.RegisterGauges(() => Leaked.Count);

        // ── Baseline: healthy async I/O. This is what "normal" looks like. ──────────────────
        app.MapGet("/step1/healthy", async (int ms = 5) =>
        {
            long start = Stopwatch.GetTimestamp();
            await Task.Delay(ms);
            AppMetrics.WorkItems.Add(1, new KeyValuePair<string, object?>("kind", "healthy"));
            AppMetrics.WorkDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return Results.Ok(new { ok = true, ms });
        });

        // ── CPU-bound: real work, no blocking. cpu-usage → 100% × cores. ────────────────────
        app.MapGet("/step1/cpu", (int ms = 200) =>
        {
            long start = Stopwatch.GetTimestamp();
            double sink = 0;
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < ms)
            {
                for (int i = 1; i < 10_000; i++)
                {
                    sink += Math.Sqrt(i) * Math.Sin(i);
                }
            }

            AppMetrics.WorkDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return Results.Ok(new { ms, sink });
        });

        // ── Allocation churn: high alloc-rate, high gen-0, heap floor stays flat. ───────────
        app.MapGet("/step1/alloc", (int kb = 512) =>
        {
            long allocated = 0;
            string result = "";
            for (int i = 0; i < kb; i++)
            {
                byte[] block = new byte[1024];    // gen0, dies immediately
                block[0] = (byte)i;
                result += i.ToString();           // O(n²) string churn on top
                allocated += 1024;
            }

            AppMetrics.BytesChurned.Add(allocated);
            return Results.Ok(new { kb, resultLength = result.Length });
        });

        // ── LOH churn: arrays ≥ 85,000 bytes skip gen0 entirely. ────────────────────────────
        app.MapGet("/step1/loh", (int kb = 256) =>
        {
            byte[] big = new byte[Math.Max(kb, 85) * 1024];
            big[^1] = 1;
            AppMetrics.BytesChurned.Add(big.Length);
            return Results.Ok(new { bytes = big.Length, onLoh = big.Length >= 85_000 });
        });

        // ── Retention: the gc-heap-size floor rises and never comes back down. ──────────────
        app.MapGet("/step1/leak", (int n = 1000) =>
        {
            for (int i = 0; i < n; i++)
            {
                Leaked.Add(new byte[1024]);
            }

            return Results.Ok(new { retainedObjects = Leaked.Count });
        });

        app.MapGet("/step1/leak/stats", () =>
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            return Results.Ok(new
            {
                retainedObjects = Leaked.Count,
                gcHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                gen2Collections = GC.CollectionCount(2),
                // §2.5 — the numbers a real service should export continuously
                pauseTimePercentage = info.PauseTimePercentage,
                fragmentedBytes = info.FragmentedBytes,
                committedBytes = info.TotalCommittedBytes,
            });
        });

        app.MapPost("/step1/leak/clear", () =>
        {
            Leaked.Clear();
            return Results.Ok(new { retainedObjects = Leaked.Count, note = "watch gc-heap-size after the next gen2" });
        });

        // ── Thread-pool starvation: HIGH LATENCY WITH LOW CPU. ──────────────────────────────
        //    Hit with ~200 concurrent requests and watch threadpool-queue-length climb while
        //    cpu-usage sits near idle. Step 2 is about never writing this.
        app.MapGet("/step1/block", (int ms = 200) =>
        {
            Task.Delay(ms).GetAwaiter().GetResult();   // ← blocks a pool thread. Do not do this.
            return Results.Ok(new { ms, note = "a thread-pool thread was held hostage for this request" });
        });

        // ── Lock contention: one lock serialises every request through it. ──────────────────
        app.MapGet("/step1/contend", (int ms = 5) =>
        {
            lock (Gate)
            {
                long start = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < ms)
                {
                    Thread.SpinWait(100);
                }
            }

            return Results.Ok(new { ms });
        });

        // ── Exceptions as control flow: microseconds each, and they add up. ─────────────────
        app.MapGet("/step1/exceptions", (int n = 100) =>
        {
            int caught = 0;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    throw new InvalidOperationException("this is what exception-count measures");
                }
                catch (InvalidOperationException)
                {
                    caught++;
                }
            }

            return Results.Ok(new { thrown = caught });
        });
    }
}
