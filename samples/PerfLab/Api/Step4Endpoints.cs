using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PerfLab.Api;

/// <summary>
/// Step 4 — MemoryOptimizationTricks.md.
///
/// The rule Step 1 §1.1 ends on: a microbenchmark improvement is a hypothesis until the service
/// level confirms it. These endpoints do identical work with different allocation strategies, so
/// you can watch `alloc-rate` and `gen-0-gc-count` move under real load rather than trusting a
/// benchmark table.
///
///   bombardier -c 50 -d 30s "http://localhost:5199/step4/naive-report?rows=500"
///   bombardier -c 50 -d 30s "http://localhost:5199/step4/pooled-report?rows=500"
///
/// with `dotnet-counters monitor -p &lt;pid&gt; System.Runtime` open. The interesting question is not
/// whether alloc-rate drops — it will — but whether p99 latency improves ENOUGH to justify the
/// code. Often it doesn't, and that answer is just as valuable.
/// </summary>
internal static class Step4Endpoints
{
    public static readonly string[] Catalog =
    [
        "GET /step4/naive-report?rows=500   — idiomatic: LINQ, string concat, Substring.",
        "GET /step4/pooled-report?rows=500  — identical output: spans, ArrayPool, TryFormat.",
        "GET /step4/naive-json?rows=500     — reflection-based System.Text.Json.",
        "GET /step4/pooled-json?rows=500    — source-generated + pooled buffer writer.",
        "GET /step4/alloc-report            — bytes-per-request for each, measured in-process.",
    ];

    private static readonly Row[] Rows = Enumerable.Range(0, 2000)
        .Select(i => new Row(i, $"item-{i:D5}", i * 1.25m))
        .ToArray();

    public static void MapStep4Endpoints(this WebApplication app)
    {
        // ── The idiomatic version. Nothing here is wrong; it just allocates. ───────────────
        app.MapGet("/step4/naive-report", (int rows = 500) =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();

            string csv = BuildNaive(rows);

            return Results.Ok(new
            {
                mode = "naive",
                rows,
                length = csv.Length,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before,
            });
        });

        // ── Same output, Step 4 techniques. ───────────────────────────────────────────────
        app.MapGet("/step4/pooled-report", (int rows = 500) =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();

            string csv = BuildPooled(rows);

            return Results.Ok(new
            {
                mode = "pooled",
                rows,
                length = csv.Length,
                elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before,
                note = "A COLD pool allocates: the very first Rent has nothing to hand you, so an "
                     + "early call here can report MORE than /naive-report. Hit it a few times, or "
                     + "use /step4/alloc-report, which warms up first.",
            });
        });

        // ── JSON: reflection vs source generation ─────────────────────────────────────────
        app.MapGet("/step4/naive-json", (int rows = 500) =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            string json = JsonSerializer.Serialize(Rows.Take(rows).ToList());
            return Results.Ok(new
            {
                mode = "reflection",
                rows,
                length = json.Length,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before,
            });
        });

        app.MapGet("/step4/pooled-json", (int rows = 500) =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();

            var buffer = new ArrayBufferWriter<byte>(rows * 64);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                for (int i = 0; i < rows; i++)
                {
                    ref readonly Row row = ref Rows[i];
                    writer.WriteStartObject();
                    writer.WriteNumber("id"u8, row.Id);      // UTF-8 literal: no transcoding
                    writer.WriteString("name"u8, row.Name);
                    writer.WriteNumber("amount"u8, row.Amount);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            return Results.Ok(new
            {
                mode = "utf8-writer",
                rows,
                length = buffer.WrittenCount,
                allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before,
            });
        });

        // ── Side-by-side, measured in-process (WaysToMeasure.md §7.3) ─────────────────────
        app.MapGet("/step4/alloc-report", (int rows = 500) =>
        {
            BuildNaive(rows);                 // warm up: measuring cold code measures the JIT
            BuildPooled(rows);

            long b1 = GC.GetAllocatedBytesForCurrentThread();
            string naive = BuildNaive(rows);
            long naiveBytes = GC.GetAllocatedBytesForCurrentThread() - b1;

            long b2 = GC.GetAllocatedBytesForCurrentThread();
            string pooled = BuildPooled(rows);
            long pooledBytes = GC.GetAllocatedBytesForCurrentThread() - b2;

            return Results.Ok(new
            {
                rows,
                identicalOutput = naive == pooled,     // the optimization must not change behaviour
                naiveBytes,
                pooledBytes,
                reduction = naiveBytes == 0 ? 0 : 1.0 - (double)pooledBytes / naiveBytes,
                note = "now check whether p99 actually moved under load — alloc-rate alone is not the goal",
            });
        });
    }

    private static string BuildNaive(int rows)
    {
        var sb = new StringBuilder();
        foreach (Row r in Rows.Take(rows))
        {
            sb.Append($"{r.Id},{r.Name},{r.Amount}\n");   // interpolation allocates per row
        }

        return sb.ToString();
    }

    private static string BuildPooled(int rows)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(rows * 48);
        try
        {
            Span<char> span = buffer;
            int pos = 0;

            for (int i = 0; i < rows; i++)
            {
                ref readonly Row r = ref Rows[i];
                r.Id.TryFormat(span[pos..], out int written);
                pos += written;
                span[pos++] = ',';
                r.Name.AsSpan().CopyTo(span[pos..]);
                pos += r.Name.Length;
                span[pos++] = ',';
                r.Amount.TryFormat(span[pos..], out written);
                pos += written;
                span[pos++] = '\n';
            }

            return new string(span[..pos]);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    internal readonly record struct Row(int Id, string Name, decimal Amount);
}

[JsonSerializable(typeof(List<Step4Endpoints.Row>))]
internal partial class RowJsonContext : JsonSerializerContext;
