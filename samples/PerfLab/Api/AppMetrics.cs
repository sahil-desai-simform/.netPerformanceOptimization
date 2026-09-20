using System.Diagnostics.Metrics;

namespace PerfLab.Api;

/// <summary>
/// WaysToMeasure.md §9.4 — custom instrumentation.
///
/// A Meter is the modern, OpenTelemetry-native replacement for EventCounter. Instruments cost
/// close to nothing when nothing is listening, and they show up automatically in
/// dotnet-counters, Prometheus, and any OTel-compatible APM:
///
///     dotnet-counters monitor -p &lt;pid&gt; PerfLab.Api
/// </summary>
internal static class AppMetrics
{
    public const string MeterName = "PerfLab.Api";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Histogram, so you get percentiles rather than an average.</summary>
    public static readonly Histogram<double> WorkDuration =
        Meter.CreateHistogram<double>("perflab.work.duration", "ms", "Time spent in the simulated workload");

    public static readonly Counter<long> WorkItems =
        Meter.CreateCounter<long>("perflab.work.items", "{item}", "Simulated work items processed");

    public static readonly Counter<long> BytesChurned =
        Meter.CreateCounter<long>("perflab.alloc.bytes", "By", "Bytes deliberately allocated and discarded");

    /// <summary>
    /// Observable gauges are pull-based: the callback only runs when something scrapes, so an
    /// expensive-to-compute value costs nothing while nobody is watching.
    /// </summary>
    public static void RegisterGauges(Func<int> leakedObjectCount)
    {
        Meter.CreateObservableGauge(
            "perflab.leak.retained_objects",
            leakedObjectCount,
            "{object}",
            "Objects deliberately retained by the /leak endpoint");

        Meter.CreateObservableGauge(
            "perflab.threadpool.busy_workers",
            static () =>
            {
                ThreadPool.GetMaxThreads(out int maxWorker, out _);
                ThreadPool.GetAvailableThreads(out int availWorker, out _);
                return maxWorker - availWorker;
            },
            "{thread}",
            "Worker threads currently in use — see §8.2");
    }
}
