using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PerfLab.Api;

/// <summary>
/// Leak lab — three real-world leak shapes, each with a fixed twin at the same URL plus "-fixed".
///
/// Step 1's /step1/leak retains plain byte[], which is hard to spot in a snapshot diff because
/// everything allocates byte[]. These retain NAMED types, so the diff points straight at them:
///
///   1. Take a Memory Usage snapshot in the Visual Studio profiler.
///   2. bombardier -n 5000 -c 10 "http://localhost:5199/leaks/event-subscriber"
///   3. Take a second snapshot. Open the diff, sort by Count Diff — OrderNotifier is at the top.
///   4. Select it → "Paths to Root" shows PriceFeed.PriceChanged holding every instance.
///   5. Repeat against the -fixed URL: OrderNotifier's Count Diff is ~0.
///
/// To see the allocating LINE rather than the retaining path, use .NET Object Allocation Tracking
/// instead: pick the type, then Backtrace → double-click to the source.
///
/// Snapshots answer "who is keeping this alive"; allocation tracking answers "who created it".
/// </summary>
internal static class LeakLabEndpoints
{
    public static readonly string[] Catalog =
    [
        "GET  /leaks/event-subscriber          — subscribes to a static event, never unsubscribes.",
        "GET  /leaks/event-subscriber-fixed    — same, but disposed (unsubscribes).",
        "GET  /leaks/report-cache?customerId=42       — cache key includes a timestamp, so it never hits.",
        "GET  /leaks/report-cache-fixed?customerId=42 — keyed by customer only.",
        "GET  /leaks/heartbeat                 — starts a timer per request, never disposed.",
        "GET  /leaks/heartbeat-fixed           — same, but disposed.",
        "GET  /leaks/stats                     — what each scenario is currently holding.",
        "POST /leaks/reset                     — release the event and cache leaks (timers need a restart).",
    ];

    private static readonly ConcurrentDictionary<string, CustomerReport> ReportCache = new();

    public static void MapLeakLabEndpoints(this WebApplication app)
    {
        // ── 1. Event-handler leak: the static event holds a reference to every subscriber. ──
        //    Delegate.Combine also copies the whole invocation list on every +=, so this
        //    endpoint gets slower AND allocates more as the leak grows.
        app.MapGet("/leaks/event-subscriber", () =>
        {
            var notifier = new OrderNotifier();   // ← LEAK: subscribes, never disposed
            PriceFeed.Publish(Random.Shared.Next(100, 200));
            return Results.Ok(new { lastPrice = notifier.LastPrice });
        });

        app.MapGet("/leaks/event-subscriber-fixed", () =>
        {
            using var notifier = new OrderNotifier();   // Dispose unsubscribes
            PriceFeed.Publish(Random.Shared.Next(100, 200));
            return Results.Ok(new { lastPrice = notifier.LastPrice });
        });

        // ── 2. Unbounded cache: a key that is unique per call turns a cache into a leak. ─────
        app.MapGet("/leaks/report-cache", (string customerId = "42") =>
        {
            string key = $"{customerId}:{DateTime.UtcNow.Ticks}";   // ← LEAK: never the same key twice
            CustomerReport report = ReportCache.GetOrAdd(key, _ => CustomerReport.Build(customerId));
            return Results.Ok(new { report.CustomerId, report.Body.Length, cached = ReportCache.Count });
        });

        app.MapGet("/leaks/report-cache-fixed", (string customerId = "42") =>
        {
            CustomerReport report = ReportCache.GetOrAdd(customerId, CustomerReport.Build);
            return Results.Ok(new { report.CustomerId, report.Body.Length, cached = ReportCache.Count });
        });

        // ── 3. Timer leak: the runtime's timer queue roots the callback, and the callback
        //    roots its target. Nothing in your code references it, and it still never dies.
        app.MapGet("/leaks/heartbeat", () =>
        {
            var heartbeat = new SessionHeartbeat();   // ← LEAK: timer never disposed
            return Results.Ok(new { started = true, activeTimers = Timer.ActiveCount });
        });

        app.MapGet("/leaks/heartbeat-fixed", () =>
        {
            using var heartbeat = new SessionHeartbeat();
            return Results.Ok(new { started = true, activeTimers = Timer.ActiveCount });
        });

        app.MapGet("/leaks/stats", () => Results.Ok(new
        {
            eventSubscribers = PriceFeed.SubscriberCount,
            cachedReports = ReportCache.Count,
            activeTimers = Timer.ActiveCount,
            gcHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
        }));

        app.MapPost("/leaks/reset", () =>
        {
            PriceFeed.ClearSubscribers();
            ReportCache.Clear();
            return Results.Ok(new { note = "event and cache released; leaked timers live until restart" });
        });
    }
}

/// <summary>A process-wide price feed. Static, so it lives as long as the app.</summary>
internal static class PriceFeed
{
    public static event EventHandler<decimal>? PriceChanged;

    public static int SubscriberCount => PriceChanged?.GetInvocationList().Length ?? 0;

    public static void Publish(decimal price) => PriceChanged?.Invoke(null, price);

    public static void ClearSubscribers() => PriceChanged = null;
}

/// <summary>Per-request subscriber. Only safe if it is disposed.</summary>
internal sealed class OrderNotifier : IDisposable
{
    private readonly byte[] _orderHistory = new byte[2048];   // what each leaked instance costs

    public OrderNotifier() => PriceFeed.PriceChanged += OnPriceChanged;

    public decimal LastPrice { get; private set; }

    public void Dispose() => PriceFeed.PriceChanged -= OnPriceChanged;

    private void OnPriceChanged(object? sender, decimal price)
    {
        LastPrice = price;
        _orderHistory[0]++;
    }
}

internal sealed class CustomerReport(string customerId, byte[] body)
{
    public string CustomerId { get; } = customerId;

    public byte[] Body { get; } = body;

    public static CustomerReport Build(string customerId) => new(customerId, new byte[4096]);
}

/// <summary>Fires every minute for as long as the timer is alive — which, undisposed, is forever.</summary>
internal sealed class SessionHeartbeat : IDisposable
{
    private readonly byte[] _sessionState = new byte[1024];
    private readonly Timer _timer;

    public SessionHeartbeat() => _timer = new Timer(_ => Beat(), null, dueTime: 60_000, period: 60_000);

    public void Dispose() => _timer.Dispose();

    private void Beat() => _sessionState[0]++;
}
