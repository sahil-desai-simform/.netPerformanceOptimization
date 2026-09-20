using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace PerfLab.Api;

/// <summary>
/// Hosts the API lab. Every endpoint is deliberately pathological in a specific, recognisable way;
/// the skill being taught is telling those signatures apart in dotnet-counters.
///
/// Note this is a plain console SDK project with a conditional FrameworkReference rather than
/// Microsoft.NET.Sdk.Web — that's what lets the benchmarks multi-target net8.0/9.0/10.0 while the
/// API stays net10.0-only. It means implicit usings do NOT include the ASP.NET Core namespaces,
/// hence the explicit using block above.
/// </summary>
internal static class ApiHost
{
    public static void Run(string[] args)
    {
        WarnIfNotProductionGc();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // ── WaysToMeasure.md §8.1 — permanent layer-1 observability in ten lines ────────────
        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()   // http.server.request.duration, active_requests
                .AddHttpClientInstrumentation()   // http.client.request.duration, time_in_queue
                .AddRuntimeInstrumentation()      // dotnet.gc.*, dotnet.thread_pool.*, exceptions
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                .AddMeter(AppMetrics.MeterName)   // our own instruments, from AppMetrics.cs
                .AddPrometheusExporter())
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        // ── §7.4 — catch "Scoped service captured by a Singleton", the most common
        // ASP.NET Core memory leak. Both default to true in Development only; enabling them
        // everywhere means a Staging load test fails fast instead of leaking quietly.
        builder.Host.UseDefaultServiceProvider((_, opts) =>
        {
            opts.ValidateScopes = true;
            opts.ValidateOnBuild = true;
        });

        WebApplication app = builder.Build();

        app.MapPrometheusScrapingEndpoint();   // GET /metrics

        app.MapGet("/", () => Results.Ok(new
        {
            service = "PerfLab",
            runtime = Environment.Version.ToString(),
            serverGc = System.Runtime.GCSettings.IsServerGC,
            latencyMode = System.Runtime.GCSettings.LatencyMode.ToString(),
            processorCount = Environment.ProcessorCount,
            steps = new
            {
                step1 = Step1Endpoints.Catalog,
                step2 = Step2Endpoints.Catalog,
                step3 = Step3Endpoints.Catalog,
                step4 = Step4Endpoints.Catalog,
            },
        }));

        app.MapStep1Endpoints();
        app.MapStep2Endpoints();
        app.MapStep3Endpoints();
        app.MapStep4Endpoints();

        app.Run();
    }

    /// <summary>
    /// One project means one runtimeconfig, and the benchmarks need Workstation GC (so the GC mode
    /// is an explicit per-job choice rather than something inherited — WaysToMeasure.md §4.1).
    /// ASP.NET Core would normally default to Server GC, so the API lab is running in a
    /// configuration that is NOT production-representative unless you opt back in.
    ///
    /// Say so loudly rather than letting someone draw conclusions from the wrong GC mode — which
    /// is exactly the class of measurement mistake §10 is about.
    /// </summary>
    private static void WarnIfNotProductionGc()
    {
        if (System.Runtime.GCSettings.IsServerGC)
        {
            return;
        }

        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine("  ┌─ Workstation GC is active ──────────────────────────────────────────────┐");
        Console.WriteLine("  │ ASP.NET Core normally defaults to Server GC. This project pins          │");
        Console.WriteLine("  │ Workstation GC because the benchmarks share its runtimeconfig.          │");
        Console.WriteLine("  │ For production-representative memory behaviour (and to see DATAS at     │");
        Console.WriteLine("  │ work — §7.2), restart with:                                             │");
        Console.WriteLine("  │                                                                         │");
        Console.WriteLine("  │     DOTNET_gcServer=1 dotnet run -c Release -f net10.0 -- api           │");
        Console.WriteLine("  │     $env:DOTNET_gcServer=1     # PowerShell                             │");
        Console.WriteLine("  │                                                                         │");
        Console.WriteLine("  │ GET / always reports the mode actually in effect.                       │");
        Console.WriteLine("  └─────────────────────────────────────────────────────────────────────────┘");
        Console.WriteLine();
        Console.ForegroundColor = previous;
    }
}
