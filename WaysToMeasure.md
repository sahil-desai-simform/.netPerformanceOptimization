# Step 1 — Ways to Measure Performance in a .NET Application

> **Baseline:** .NET 10 (LTS, GA Nov 2025). Features that arrived in .NET 8 or 9 are tagged so you know
> what you lose on an older LTS. .NET 11 (RC at time of writing, GA Nov 2026) items are tagged `[.NET 11]`
> and are forward-looking only.
>
> **Scope:** application-side CPU, memory, and thread behaviour. Query plans, index tuning, and
> DB server metrics are deliberately out of scope — but see [§8.4](#84-when-the-app-is-innocent) for how
> to *prove* the bottleneck is the database before you go looking there.
>
> **Companion code:** [samples/](samples/) — a runnable ASP.NET Core API with deliberately pathological
> endpoints, plus a BenchmarkDotNet project. Every command in this document is meant to be run against it.

---

## Table of contents

1. [The measurement hierarchy](#1-the-measurement-hierarchy)
2. [What to measure, and what the numbers mean](#2-what-to-measure-and-what-the-numbers-mean)
3. [Layer 1 — `dotnet-*` CLI diagnostics](#3-layer-1--dotnet--cli-diagnostics)
4. [Layer 2 — BenchmarkDotNet](#4-layer-2--benchmarkdotnet)
5. [Layer 3 — Visual Studio & Rider profilers](#5-layer-3--visual-studio--rider-profilers)
6. [CPU analysis in depth](#6-cpu-analysis-in-depth)
7. [Memory analysis in depth](#7-memory-analysis-in-depth)
8. [ASP.NET Core specifics](#8-aspnet-core-specifics)
9. [Advanced: codegen, hardware counters, and custom trace analysis](#9-advanced-codegen-hardware-counters-and-custom-trace-analysis)
10. [Measurement mistakes that produce confident wrong answers](#10-measurement-mistakes-that-produce-confident-wrong-answers)
11. [Runbook / cheat sheet](#11-runbook--cheat-sheet)

---

## 1. The measurement hierarchy

There are four distinct questions, and each one has its own tool. Using the wrong layer is the single
most common way to waste a week.

| # | Question | Layer | Tools | Environment |
|---|----------|-------|-------|-------------|
| 1 | *Is there a problem, and does anyone care?* | Observability | OpenTelemetry metrics, `dotnet-counters`, APM | Production |
| 2 | *Where in the code is the time or memory going?* | Profiling / tracing | `dotnet-trace`, `dotnet-gcdump`, VS/Rider profilers, PerfView | Prod-like load test |
| 3 | *Is candidate B actually faster than A?* | Microbenchmarking | BenchmarkDotNet | Quiet dev/CI machine |
| 4 | *Did it stay fast?* | Regression gating | BDN in CI + `ThresholdColumn`, load-test gates | CI |

Rules that follow from this:

- **Never start at layer 3.** A microbenchmark tells you the relative cost of two implementations of a
  method. It tells you nothing about whether that method matters. Teams routinely make a method 40×
  faster and observe zero change in p99 because the method accounted for 0.3% of the request.
- **Never stop at layer 2.** A profiler shows you *where* time goes but is bad at proving a fix works,
  because the profiler's own overhead and the noise of a full application swamp small deltas.
- **Layer 1 defines "done".** If your SLO is p99 < 200 ms, then the optimization is finished when
  production p99 is under 200 ms, not when the benchmark table looks impressive.

### 1.1 The loop

```
        ┌──────────────────────────────────────────────┐
        │  1. Observe prod  →  SLO breach? which one?  │
        └───────────────────────┬──────────────────────┘
                                ▼
        ┌──────────────────────────────────────────────┐
        │  2. Reproduce under load  →  trace/profile   │
        │     Produces: a ranked list of suspects      │
        └───────────────────────┬──────────────────────┘
                                ▼
        ┌──────────────────────────────────────────────┐
        │  3. Isolate top suspect into a benchmark     │
        │     Prove the fix in isolation (A/B + ratio) │
        └───────────────────────┬──────────────────────┘
                                ▼
        ┌──────────────────────────────────────────────┐
        │  4. Re-run the load test  →  did p99 move?   │
        │     No  → your model was wrong, go to 2      │
        │     Yes → ship + add a regression gate       │
        └──────────────────────────────────────────────┘
```

Step 4 is the one people skip, and it is the one that catches a wrong mental model. If the benchmark
improved 10× and the load test didn't move, the method wasn't the bottleneck — Amdahl's law is not
negotiable.

### 1.2 Amdahl's law as a sanity filter

Before optimizing anything, compute the ceiling:

```
Speedup_total = 1 / ((1 - P) + P / S)
```

where `P` is the fraction of total time in the component and `S` is how much faster you make it.
If a method is 5% of the request (`P = 0.05`) and you make it infinitely fast (`S = ∞`), the ceiling
is 1 / 0.95 ≈ **5.3% improvement**. That's the entire prize. Decide whether it's worth the change
*before* you write the code.

---

## 2. What to measure, and what the numbers mean

### 2.1 Latency: percentiles, never averages

An average hides the tail, and the tail is what users experience and what cascades into timeouts.

| Metric | What it tells you |
|--------|-------------------|
| p50 | The typical experience. Moves when the common path is slow. |
| p95 / p99 | The tail. Moves with GC pauses, lock contention, thread-pool starvation, cold cache, retries. |
| p99.9 | Usually GC pauses, connection-pool exhaustion, or a noisy neighbour. |
| max | Almost always an outlier event (JIT warm-up, a gen2 blocking GC, a DNS timeout). Useful for *classifying*, not for SLOs. |

A **bimodal histogram** is a diagnosis, not noise: two humps almost always means two code paths
(cache hit vs. miss), or a queueing effect kicking in past a concurrency threshold.

### 2.2 Throughput and Little's Law

```
L = λ × W
```

`L` = concurrent requests in flight, `λ` = arrival rate (req/s), `W` = average latency (s).

This is the most useful formula in application performance work, because it lets you predict
capacity from two numbers you already have:

- 500 req/s at 200 ms latency ⇒ 100 requests in flight at any moment.
- If each in-flight request holds a pooled DB connection and your pool max is 50, you are
  **structurally** unable to serve 500 req/s. No amount of CPU optimization fixes that.
- Inverted: if latency doubles under constant load, in-flight count doubles, which increases
  contention on every pooled resource — this is the feedback loop behind "it fell over all at once".

### 2.3 Memory: two independent problems

People say "memory problem" to mean two things that have opposite fixes.

| | **Allocation rate** (throughput problem) | **Retained set** (capacity problem) |
|---|---|---|
| Symptom | High CPU in GC, p99 spikes, `time-in-gc` high | Working set grows monotonically, OOM, container kill |
| Metric | `alloc-rate` (bytes/sec), Gen0 collections/sec | Gen2 size, `gc-heap-size` after a forced full GC |
| Cause | Garbage per operation (LINQ, string concat, boxing, closures) | Caches without eviction, static collections, event-handler leaks, un-disposed resources |
| Tool | `dotnet-counters`, BDN `MemoryDiagnoser`, allocation profiler | `dotnet-gcdump` diffs, `dotnet-dump` + SOS `!gcroot` |
| Fix | Step 4 of this series (spans, pooling, no-alloc code) | Ownership and lifetime fixes — an allocation fix will not help |

**Diagnostic that separates them in 30 seconds:** watch `gc-heap-size` across several Gen2
collections. If it sawtooths back down to a stable floor, you have an allocation-rate problem. If the
floor rises with every cycle, you have a retention problem.

### 2.4 Thread-pool health

For an ASP.NET Core service, these three are as important as CPU:

| Counter | Healthy | Meaning when unhealthy |
|---|---|---|
| `threadpool-queue-length` | ~0, transient spikes only | Work items waiting for a thread. Sustained > 0 = **starvation**. |
| `threadpool-thread-count` | Stable near core count | Climbing steadily = pool injecting threads (1 per ~0.5–1 s) to compensate for blocked threads. |
| `monitor-lock-contention-count` | Low and flat | Rate of `Monitor` contention — a lock is serializing your service. |

Thread-pool starvation is the classic .NET production incident: latency goes through the roof while
CPU sits at 20%. Steps 2 and 3 of this series deal with the causes; this document deals with proving
it's happening.

### 2.5 GC pause time

`dotnet.gc.pause.time` (Meter, .NET 9+) or `time-in-gc` (EventCounter) — the percentage of time the
process spent in GC since the last sample. Rules of thumb for a web service:

- **< 5%** — fine, ignore.
- **5–10%** — worth a look; you can probably win something in Step 4.
- **> 10%** — allocation rate is your top problem; nothing else you do will matter as much.

Note that `time-in-gc` is *not* pause time — with background GC, much of gen2 work is concurrent.
Prefer the .NET 9+ `dotnet.gc.pause.time` metric, which measures actual suspension, or read
`GCSettings`/`GC.GetGCMemoryInfo().PauseDurations` programmatically:

```csharp
GCMemoryInfo info = GC.GetGCMemoryInfo();
// PauseDurations[0] = most recent pause; [1] = the other half of a BGC's two suspensions
TimeSpan lastPause  = info.PauseDurations[0];
double   pausePct   = info.PauseTimePercentage;   // since process start
long     heapBytes  = info.HeapSizeBytes;
long     committed  = info.TotalCommittedBytes;
long     fragmented = info.FragmentedBytes;
bool     wasCompacting = info.Compacted;
bool     wasConcurrent = info.Concurrent;
int      generation = info.Generation;           // which generation was condemned
```

`GC.GetGCMemoryInfo()` describes the *most recent* GC, so sample it on a timer rather than reading
it once. `GC.GetGCMemoryInfo(GCKind.Background)` / `GCKind.Ephemeral` narrow it to the last
collection of that kind, which is how you separate background gen2 behaviour from gen0/1 churn.

---

## 3. Layer 1 — `dotnet-*` CLI diagnostics

These are the tools that work where you actually have the problem: in a container, on a VM, with no
GUI, on a process you must not restart. Learn these before anything else.

### 3.1 Installation

```bash
dotnet tool install -g dotnet-counters     # live metrics
dotnet tool install -g dotnet-trace        # CPU/event tracing (EventPipe)
dotnet tool install -g dotnet-dump         # full process dumps + SOS analysis
dotnet tool install -g dotnet-gcdump       # managed heap graph, no full dump
dotnet tool install -g dotnet-stack        # instant managed stack snapshot
dotnet tool install -g dotnet-monitor      # HTTP-exposed diagnostics for k8s/sidecars
dotnet tool install -g dotnet-symbol       # symbol download for dump analysis
```

Update them all at once later with `dotnet tool update -g <name>`.

For containers, do **not** install these into the app image. Either:

- run them from a **sidecar** sharing the process namespace (`shareProcessNamespace: true` in k8s), or
- use **`dotnet-monitor`** as a sidecar, which exposes `/trace`, `/dump`, `/gcdump`, `/metrics`,
  `/livemetrics` and `/stacks` over HTTP.

> **[.NET 10]** The diagnostic tools can now discover and attach to processes across PID namespaces
> (e.g. a sidecar with `pid: host`) by resolving the diagnostic socket via `/proc/{pid}/status`.
> Before .NET 10 this required the tool and app to be in the same namespace, which is why so many
> older guides tell you to bake the tools into the app image. You no longer need to.

### 3.2 `dotnet-counters` — the first thing you run

```bash
dotnet-counters ps                       # list attachable .NET processes
dotnet-counters monitor -p <pid>         # live, System.Runtime by default
```

A more useful invocation for an ASP.NET Core service — runtime, hosting, Kestrel, and outbound HTTP
in one view:

```bash
dotnet-counters monitor -p <pid> --refresh-interval 1 \
  System.Runtime \
  Microsoft.AspNetCore.Hosting \
  Microsoft.AspNetCore.Server.Kestrel \
  System.Net.Http
```

Export for later analysis instead of watching a TTY:

```bash
dotnet-counters collect -p <pid> --format csv -o counters.csv \
  --counters System.Runtime,Microsoft.AspNetCore.Hosting \
  --duration 00:02:00
```

Launch-and-monitor, so you capture startup (you cannot attach fast enough otherwise):

```bash
dotnet-counters monitor --counters System.Runtime -- dotnet run -c Release
```

#### The counters worth memorizing

`System.Runtime` (EventCounters):

| Counter | Reads as |
|---|---|
| `cpu-usage` | % of one core... times core count. 800% on 8 cores = saturated. |
| `working-set` | RSS in MB. What the container limit is compared against. |
| `gc-heap-size` | Managed heap in MB. Compare to working-set to see native/other overhead. |
| `alloc-rate` | **Bytes allocated per interval.** The single best allocation-pressure number. |
| `gen-0-gc-count` / `gen-1` / `gen-2` | Collections per interval. Gen2 > ~1/min on a steady service is a smell. |
| `time-in-gc` | % of time since last GC spent in GC. |
| `gc-fragmentation` | % free space in the heap. High + high working set = fragmentation/pinning. |
| `loh-size` / `poh-size` | Large Object Heap / Pinned Object Heap. LOH growth = arrays ≥ 85,000 bytes. |
| `threadpool-queue-length` | **Starvation detector.** |
| `threadpool-thread-count` | Injection detector. |
| `monitor-lock-contention-count` | Lock contention rate. |
| `exception-count` | Exceptions/interval. Exceptions are ~microseconds each; thousands/sec is a real cost *and* usually a logic bug. |
| `il-bytes-jitted`, `time-in-jit` | Startup cost. Should flatline after warm-up; if it doesn't, something is emitting dynamic code (bad LINQ-to-object-graph patterns, `Expression.Compile` per request). |

**[.NET 8+]** The runtime also publishes `System.Diagnostics.Metrics` meters, which are the
OpenTelemetry-native path and what you should wire into production dashboards:

| Meter instrument | Notes |
|---|---|
| `dotnet.gc.collections` | Counter, tagged by `gc.heap.generation`. |
| `dotnet.gc.pause.time` | **True suspension time**, unlike `time-in-gc`. |
| `dotnet.gc.heap.total_allocated_bytes` | Monotonic; rate-of-change = allocation rate. |
| `dotnet.gc.last_collection.heap.fragmentation.size` | Fragmentation in bytes. |
| `dotnet.gc.last_collection.memory.committed_size` | Committed, which is what DATAS moves. |
| `dotnet.thread_pool.queue.length`, `dotnet.thread_pool.thread.count` | Starvation. |
| `dotnet.monitor.lock_contentions` | Contention. |
| `dotnet.process.cpu.time` | Tagged `user` / `system`. |
| `dotnet.exceptions` | Tagged by exception type. |

`dotnet-counters` reads both kinds, so you can mix them on one command line.

> **[.NET 10]** Processes running .NET 10+ emit additional always-on lightweight GC, JIT, and
> assembly-load events. In practice this means a plain `dotnet-trace` capture contains enough GC
> and JIT context to reason about without needing to crank the GC keyword up to `Verbose` (which
> is expensive and can perturb the thing you are measuring).

### 3.3 `dotnet-trace` — where the CPU actually went

`dotnet-trace` drives **EventPipe**, the cross-platform, in-process tracing mechanism. It works on
Linux containers with no elevation, which is its whole reason for existing.

```bash
# 30 s CPU sample, straight to a flamegraph-ready file
dotnet-trace collect -p <pid> --profile cpu-sampling --duration 00:00:30 \
  --format speedscope -o cpu.speedscope.json
```

Open the result at <https://speedscope.app> (runs entirely client-side — no upload). Use the
**"Left Heavy"** view first: it merges all identical stacks so the widest bar is genuinely your
biggest cost. The default "Time Order" view is for spotting phases, not for finding hot code.

Built-in profiles:

| Profile | Keywords enabled | Use for |
|---|---|---|
| `cpu-sampling` | Sample profiler + minimal runtime | "Why is CPU at 100%?" — **start here** |
| `gc-verbose` | GC keyword `Verbose` + allocation-tick sampling | Which types/call-sites are allocating |
| `gc-collect` | GC keyword `Informational`, no allocations | Low-overhead GC-behaviour-only trace |
| `database` | ADO.NET + EF Core events | Query counts, N+1 detection (see §8.4) |

Custom provider sets — the syntax is `Provider:Keywords:Level`:

```bash
# Thread pool + task events at Informational — for starvation analysis
dotnet-trace collect -p <pid> \
  --providers System.Threading.Tasks.TplEventSource:0x1FF:4,Microsoft-Windows-DotNETRuntime:0x10000:4

# Full ASP.NET Core request pipeline
dotnet-trace collect -p <pid> \
  --providers Microsoft-AspNetCore-Server-Kestrel,Microsoft.AspNetCore.Hosting
```

Launch-and-trace, for startup problems:

```bash
dotnet-trace collect --format speedscope -- dotnet run -c Release --no-build
```

Discover what a process can emit:

```bash
dotnet-trace list-profiles
```

**Overhead expectation:** `cpu-sampling` is a sampling profiler at ~1 kHz per thread. Real-world
overhead is typically a few percent — acceptable on a canary instance in production. `gc-verbose`
with allocation events is much heavier (it fires an event per ~100 KB allocated) and *will* distort
an allocation-heavy workload. Use `gc-collect` unless you specifically need allocation call-sites.

### 3.4 `dotnet-gcdump` — heap composition without a full dump

```bash
dotnet-gcdump collect -p <pid> -o before.gcdump
# ... apply load / wait for growth ...
dotnet-gcdump collect -p <pid> -o after.gcdump
```

A `.gcdump` contains the **object graph** (types, counts, sizes, references) but **no field values**,
which makes it safe to take off a production box containing customer data — an important practical
difference from a full dump. It's a few MB where a full dump is gigabytes.

**Leak workflow:** take two dumps minutes apart under steady load, open both in Visual Studio
(File → Open → `after.gcdump`, then "Compare to baseline" → `before.gcdump`). Sort by **Size Diff**.
The type that grew is rarely the leak itself — it's the *symptom*. What you actually want is the
retention path, which is why you then go to a real dump and `!gcroot` (§7.5).

> Taking a gcdump forces a **blocking gen2 GC**. Expect a pause proportional to your heap. Don't do
> it on every instance at once.

### 3.5 `dotnet-dump` + SOS — the last resort that always works

```bash
dotnet-dump collect -p <pid>                 # full dump (default)
dotnet-dump collect -p <pid> --type Heap     # managed heap only, much smaller
dotnet-dump analyze core_20260913_121500
```

Essential SOS commands once inside `analyze`:

```
clrstack -all                 # managed stacks for every thread
dumpasync                     # async state machines — the async-aware "stacks"
dumpasync --stats             # grouped: which async method has thousands of pending instances
dumpheap -stat                # every type, count, total size — sort by the bottom
dumpheap -mt <MT> -min 85000  # instances of one type over LOH threshold
gcroot <addr>                 # WHY is this object alive — the money command
objsize <addr>                # retained size of an object graph
eeheap -gc                    # per-generation, per-heap segment/region layout
threads                       # thread list; look for many threads in the same frame
syncblk                       # who holds which monitor — deadlock/contention
analyzeoom                    # post-OOM: which allocation failed and why
threadpool                    # queue depths, worker/IOCP counts
```

`dumpasync` deserves special mention: in an async service, `clrstack` shows you a thread-pool thread
doing nothing useful, because the interesting state is in heap-allocated state machines, not on
stacks. `dumpasync --stats` on a hung service usually names the culprit in one line
(e.g. 12,000 pending `SendAsync` state machines ⇒ you have exhausted an `HttpClient` connection pool).

### 3.6 `dotnet-stack` — the 2-second triage

```bash
dotnet-stack report -p <pid>
```

Prints managed stacks for all threads immediately, no dump, no analysis session. When someone says
"prod is hung", run this twice, 5 seconds apart, and diff. If the stacks are identical, you have a
deadlock or a blocked pool; if they're churning through the same method, you have a hot loop.

### 3.7 `dotnet-monitor` — the production-grade wrapper

For Kubernetes, run `dotnet-monitor` as a sidecar in the same pod. It exposes:

| Endpoint | Gives you |
|---|---|
| `/processes` | Discoverable processes |
| `/metrics` | Prometheus-format counters |
| `/livemetrics` | Streaming counter values |
| `/trace?profile=cpu` | An EventPipe trace as a download |
| `/dump`, `/gcdump` | Dumps on demand |
| `/stacks` | Thread stacks |

Its real value is **collection rules**: trigger an automatic dump/trace when a condition fires, so
you capture the incident instead of trying to reproduce it later.

```json
{
  "CollectionRules": {
    "CaptureTraceOnHighCpu": {
      "Trigger": {
        "Type": "EventCounter",
        "Settings": {
          "ProviderName": "System.Runtime",
          "CounterName": "cpu-usage",
          "GreaterThan": 80,
          "SlidingWindowDuration": "00:00:30"
        }
      },
      "Actions": [
        { "Type": "CollectTrace",
          "Settings": { "Profile": "Cpu", "Duration": "00:00:30", "Egress": "artifacts" } }
      ],
      "Limits": { "ActionCount": 3, "ActionCountSlidingWindowDuration": "01:00:00" }
    }
  }
}
```

That `Limits` block is not optional in practice — without it a flapping condition will fill your blob
storage and add its own load to an already-struggling process.

---

## 4. Layer 2 — BenchmarkDotNet

BenchmarkDotNet (BDN) is the only credible way to compare two implementations in .NET. It handles
warm-up, tiered-compilation settling, statistical analysis, outlier detection, and process isolation
— all things that hand-rolled `Stopwatch` loops get wrong.

**Current version: 0.15.8.**

### 4.1 Project setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <!-- Release is mandatory; BDN refuses to run a Debug build for good reason -->
    <Configuration Condition="'$(Configuration)'==''">Release</Configuration>
    <!-- Needed by DisassemblyDiagnoser and for readable stacks in traces -->
    <DebugType>pdbonly</DebugType>
    <DebugSymbols>true</DebugSymbols>
    <!-- Turn OFF for benchmarking: server GC changes allocation behaviour vs. the default.
         Set it explicitly per-Job instead, so it's visible in the results. -->
    <ServerGarbageCollection>false</ServerGarbageCollection>
    <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.15.8" />
  </ItemGroup>
</Project>
```

```csharp
// Program.cs
using BenchmarkDotNet.Running;
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
```

Run it:

```bash
cd samples/PerfLab
dotnet run -c Release -f net10.0 -- bench --filter '*StringConcat*'
dotnet run -c Release -f net10.0 -- bench --list flat            # discover
```

### 4.2 A benchmark that answers a real question

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;

[MemoryDiagnoser]                       // Gen0/1/2 counts + bytes allocated per op
[ThreadingDiagnoser]                    // lock contentions + completed work items per op
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job", "Error", "StdDev")] // keep the table readable; add back when investigating
public class CsvFormatting
{
    private Record[] _records = null!;

    [Params(10, 1_000)]                 // does the winner change with size? usually yes
    public int N;

    [GlobalSetup]
    public void Setup() => _records = Enumerable.Range(0, N)
        .Select(i => new Record(i, $"name-{i}", i * 1.5m))
        .ToArray();

    [Benchmark(Baseline = true)]
    public string StringConcat()
    {
        string s = "";
        foreach (var r in _records)
            s += $"{r.Id},{r.Name},{r.Amount}\n";
        return s;
    }

    [Benchmark]
    public string StringBuilder_()
    {
        var sb = new StringBuilder(N * 32);
        foreach (var r in _records)
            sb.Append(r.Id).Append(',').Append(r.Name).Append(',').Append(r.Amount).Append('\n');
        return sb.ToString();
    }

    public readonly record struct Record(int Id, string Name, decimal Amount);
}
```

Note `[Baseline = true]`: it adds a **Ratio** and **RatioSD** column. Ratios are the only numbers you
should quote to anyone, because absolute nanoseconds are machine-specific and meaningless across
hardware.

### 4.3 Reading the summary table

Real output from [samples/PerfLab/Benchmarks/Step1/](samples/PerfLab/Benchmarks/Step1/),
`--job short`, on an i5-13420H / .NET 10.0.4 (your absolute numbers will differ; the ratios
shouldn't much):

```
| Method                        | N    | Mean         | Error       | StdDev     | Ratio | Gen0      | Allocated  | Alloc Ratio |
|------------------------------ |----- |-------------:|------------:|-----------:|------:|----------:|-----------:|------------:|
| StringBuilder_NoInterpolation | 1000 |  32,451.4 ns |  9,944.9 ns |   545.1 ns |  0.04 |   16.1133 |   102176 B |       0.005 |
| StringBuilder_                | 1000 |  33,157.3 ns |  5,318.7 ns |   291.5 ns |  0.04 |   16.1133 |   102176 B |       0.005 |
| StringCreate                  | 1000 |  48,783.4 ns |  7,535.9 ns |   413.1 ns |  0.05 |   11.5356 |    72768 B |       0.004 |
| StringConcat                  | 1000 | 906,022.4 ns | 453,204.6 ns | 24,841.7 ns |  1.00 | 2965.8203 | 18654920 B |       1.000 |
```

Four things worth extracting from that table before the column reference:

1. **`StringConcat` is 28× slower and allocates 182× more.** O(n²) copying. This is the class of
   bug worth finding — not a 3% win.
2. **`StringBuilder_` and `StringBuilder_NoInterpolation` are indistinguishable.** Their Means
   differ by 700 ns while their Errors are 9,900 and 5,300 ns. Per the Error rule below,
   **this benchmark has not shown a difference** — do not report one. Avoiding the interpolated
   string here bought nothing, and only measurement told you that.
3. **`StringCreate` allocates 30% less than `StringBuilder` yet is 47% *slower*.** "Fewer bytes" and
   "less time" are different claims, measured in different columns, and neither implies the other.
   The obvious culprit is the sizing pass calling `decimal.ToString()` to count characters —
   allocating a string per record just to throw it away. **That turns out to be only half the
   story:** [Step 4 §6.1](MemoryOptimizationTricks.md#61-closing-the-loop-on-step-1--and-being-wrong-about-it)
   removes that allocation and the result is *still* slower than `StringBuilder`, because the real
   cost is the two-pass structure itself. Worth reading once you get there — it's a good example of
   fixing what you measured without fixing what was slow.
4. **`StringConcat`'s Error is 50% of its Mean.** That's a red flag on the measurement itself —
   here it's genuine GC interference from allocating 18 MB per operation, which is itself the
   finding. A large Error on a *cheap* benchmark means a noisy machine instead.

| Column | How to read it |
|---|---|
| **Mean** | Arithmetic mean per operation. |
| **Error** | Half of the 99.9% confidence interval. If two Means differ by less than the sum of their Errors, **you have not shown a difference**. |
| **StdDev** | Run-to-run variability. StdDev > ~10% of Mean means a noisy machine or a benchmark with unstable state — fix that before believing anything. |
| **Median** | Show it (`[MedianColumn]`) when the distribution is skewed; a Mean far from Median means outliers, often GC. |
| **Ratio / RatioSD** | vs. baseline. RatioSD large ⇒ the comparison itself is unstable. |
| **Gen0/1/2** | Collections **per 1000 operations**. Not per op — this trips everyone up once. A dash means fewer than one per 1000 ops, not zero. |
| **Allocated** | **Managed heap** bytes per single operation. Stack allocations and `stackalloc` don't appear here, which is exactly what you want: a 0 B row is the proof that a no-allocation rewrite worked. |
| **Alloc Ratio** | The number to quote for a memory optimization. |

Also always read the **legend and warnings** BDN prints below the table. It tells you about
multimodal distributions, outliers removed, and — critically — when a benchmark was too fast to
measure reliably.

### 4.4 Diagnosers worth knowing

| Attribute | Adds | Cost / caveat |
|---|---|---|
| `[MemoryDiagnoser]` | Allocated bytes, Gen0/1/2 | Nearly free. Use always. |
| `[MemoryDiagnoser(displayGenColumns: false)]` | Just Allocated | Cleaner table |
| `[ThreadingDiagnoser]` | Lock contentions, completed work items | Essential for Steps 2 & 3 |
| `[ExceptionDiagnoser]` | Exceptions/op | Finds accidental exception-driven control flow |
| `[DisassemblyDiagnoser(maxDepth: 3)]` | Actual asm/IL emitted | Slow; the ground truth for "did the JIT vectorize it?" |
| `[EventPipeProfiler(EventPipeProfile.CpuSampling)]` | A `.nettrace` per benchmark | Cross-platform; open in speedscope/PerfView |
| `[EtwProfiler]` | ETW trace | Windows + admin only; richer than EventPipe |
| `[NativeMemoryProfiler]` | Native alloc/leak tracking | For `NativeMemory.Alloc`, interop |
| `[HardwareCounters(HardwareCounter.BranchMispredictions, HardwareCounter.CacheMisses)]` | CPU counters | **Windows + admin only.** See §9.2 |

### 4.5 Comparing runtimes — directly relevant to "should we upgrade?"

This is the highest-value BDN feature for this repo's purpose. One class, three runtimes, one table:

```csharp
[SimpleJob(RuntimeMoniker.Net80, baseline: true)]
[SimpleJob(RuntimeMoniker.Net90)]
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
public class RuntimeComparison { /* ... */ }
```

Requires multi-targeting the benchmark project:

```xml
<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
```

BDN builds and runs a separate process per runtime and puts them in one comparison table. This is how
you produce an evidence-based upgrade case instead of citing a blog post. Expect the largest wins in
.NET 10 on code with: array/`IEnumerable` iteration over arrays (devirtualization), small short-lived
arrays and delegates (stack allocation), struct-heavy call chains (physical promotion into shared
registers), and Arm64 workloads (write barriers → 8–20% GC pause improvements).

### 4.6 Controlling the environment explicitly

```csharp
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 5, iterationCount: 15)]
[GcServer(true)]          // match production
[GcConcurrent(true)]
[InvocationCount(16)]     // for very fast benchmarks: batch invocations per iteration
[MinIterationTime(100)]   // ms
public class Configured { }
```

Or via the command line, which keeps the source clean:

```bash
dotnet run -c Release -- --filter '*' --runtimes net8.0 net10.0 \
  --memory --threading --statisticalTest 3ms --exporters json html
```

`--statisticalTest` makes BDN run a Mann–Whitney U test and label each result
**Faster / Slower / Same** relative to the baseline, with a threshold you choose. This is what you
gate CI on, not a raw ns comparison.

### 4.7 Pitfalls that produce fake results

1. **Dead-code elimination.** If you don't return the value, the JIT may delete the work.
   ```csharp
   [Benchmark] public void Bad()  { _ = Compute(); }        // may measure nothing
   [Benchmark] public int  Good() => Compute();             // BDN consumes the return value
   ```
   For `void`-returning or multi-value work, use `Consumer`:
   ```csharp
   private readonly Consumer _consumer = new();
   [Benchmark] public void Many() { foreach (var x in Query()) _consumer.Consume(x); }
   ```

2. **Constant folding.** `[Params]` fields and readonly-assigned constants can be folded at JIT time.
   Take inputs from non-readonly instance fields set in `[GlobalSetup]`.

3. **Measuring setup.** `[GlobalSetup]` runs once, `[IterationSetup]` runs per iteration and **is
   included in the measurement overhead accounting but adds noise**. If you need per-op fresh state,
   use `[IterationSetup]` with `[InvocationCount(1)]` and accept the reduced precision — or better,
   restructure so setup isn't needed.

4. **Shared mutable state between benchmarks.** A dictionary that one benchmark fills makes the next
   one look fast. Reset in `[IterationCleanup]` or use separate instances.

5. **Tiered compilation.** BDN handles warm-up, but if you're comparing *startup* behaviour, you must
   disable it explicitly (`[SimpleJob]` with `Jit`/env settings) or you'll measure tier-1 code for a
   scenario that in production always runs tier-0.

6. **A busy machine.** Close Teams, Docker Desktop, antivirus scans, and anything else. Laptops on
   battery throttle aggressively — BDN will warn about it, believe the warning. For CI, pin to a
   dedicated runner and compare only against results from the *same* runner class.

7. **Benchmarking something the GC didn't pay for yet.** Very short benchmarks can finish before the
   garbage they created is collected, so the GC cost lands on the *next* benchmark. `MemoryDiagnoser`
   reports allocations correctly regardless; it's the *time* column that gets misattributed. If Gen0
   counts are high and Means look suspiciously flat, force `[GcForce(true)]` and compare.

### 4.8 Regression gating in CI

```bash
dotnet run -c Release -- --filter '*' --exporters json --artifacts ./artifacts
```

Then compare `./artifacts/results/*-report-full-compressed.json` against a stored baseline. Options,
in increasing order of setup:

- **`--statisticalTest`** within a single run, when both the old and new implementation exist as
  benchmarks side by side. Simplest and most reliable — no stored baseline to drift.
- **`ResultsComparer`** from the [dotnet/performance](https://github.com/dotnet/performance) repo —
  diffs two BDN JSON result sets with a configurable threshold. This is what the .NET team uses.
- A **30-line script** that parses the JSON and asserts on `Statistics.Mean` and
  `Memory.BytesAllocatedPerOperation`. Often the right answer, because you control the thresholds
  per benchmark.

Practical advice: gate on **allocations**, not on time. Allocated-bytes-per-op is deterministic and
reproducible across machines; time is not. A CI rule of "no benchmark may allocate more than its
baseline + 5%" catches the vast majority of real regressions with almost no flakiness.

---

## 5. Layer 3 — Visual Studio & Rider profilers

The GUI tools win on two things: allocation call-tree attribution, and the ability to hand a
screenshot to someone who won't read a flamegraph.

### 5.1 Visual Studio

**Diagnostic Tools window** (during F5 debugging) — CPU and memory graphs, and the **Take Snapshot**
button. Snapshots are diffable: take one, exercise the feature, take another, click the diff column.
Caveat: the debugger disables several JIT optimizations and adds overhead, so **never trust absolute
numbers from a debug session**. It is for *finding* the object that's growing, not for timing.

**Performance Profiler** (`Alt+F2`, works on Release without the debugger — this is the one to use):

| Tool | Use for |
|---|---|
| **CPU Usage** | Sampling profiler. Enable "Show External Code" off first, then on when you suspect the framework. |
| **.NET Object Allocation Tracking** | **The best allocation tool in the ecosystem.** Every allocation with its call stack, grouped by type, plus "Collections" view showing what survived each GC. Heavy overhead — use short runs. |
| **Database** | EF Core / ADO.NET queries with durations and call stacks. Catches N+1 instantly. |
| **Async Tool** | Async operation durations and waits, visualized as a timeline. Underused and excellent for Step 2. |
| **Instrumentation** | Exact call counts (not sampled). Use when you need "how many times is this called", not "how long". |
| **Events Viewer** | Raw ETW event list. |

Workflow for allocation hunting: run **.NET Object Allocation Tracking**, exercise the path, stop,
sort the type list by **Allocations** (count) not bytes first — huge counts of tiny objects
(`string`, `object[]`, closure display classes, boxed `int`) tell you about a hot loop. Then switch
to bytes to find the LOH offenders. Click a type → **Backtraces** → the call stack that allocated it.

**Instrumentation caution:** it rewrites the binary and inflates the cost of small methods enormously,
which makes cheap-but-frequent methods look like bottlenecks. Sampling is the default for a reason.

### 5.2 JetBrains Rider / dotTrace / dotMemory

| Tool | Mode | Notes |
|---|---|---|
| **dotTrace** | *Sampling* | Default. Low overhead, accurate for wall-clock. |
| | *Tracing* | Exact call counts, high overhead, distorts small methods. |
| | *Line-by-line* | Per-line timings. Very heavy — use on one method you've already isolated. |
| | *Timeline* | **The one that matters for async/threading.** Shows per-thread states over time: Running, Waiting, GC Wait, Blocked, I/O. This is how you *see* thread-pool starvation rather than infer it. |
| **dotMemory** | Snapshots + diff | Retention paths, "Group by dominators", key-retention-path view. Comparable to VS allocation tracking; better at answering "what holds this alive". |

**dotTrace Timeline is the single best tool for Steps 2 and 3 of this series.** When you have a
request taking 900 ms and the CPU profiler shows 40 ms of CPU, Timeline shows you the 860 ms of
`Waiting` and what it was waiting on.

### 5.3 When to reach for GUI vs. CLI

| Situation | Tool |
|---|---|
| Prod container, no restart possible | `dotnet-*` CLI / `dotnet-monitor` |
| "Which line allocates this?" on your dev box | VS Allocation Tracking / dotMemory |
| "Why is this async request slow but not CPU-bound?" | dotTrace Timeline / VS Async Tool |
| "Is version B faster than version A?" | BenchmarkDotNet, always |
| Cross-platform, scriptable, CI | `dotnet-trace` + speedscope, BDN |
| Deep event analysis, GC internals, custom queries | PerfView / TraceEvent (§9.3) |

---

## 6. CPU analysis in depth

### 6.1 First, classify the problem

```
Is CPU high?
├─ YES, and latency is bad
│   └─ Real CPU-bound work. → CPU sampling profile. §6.2
├─ YES, but latency is fine
│   └─ You're at capacity. Either scale, or reduce CPU per request. Not urgent, but a ceiling.
└─ NO (CPU low, latency bad)  ← the interesting case
    ├─ threadpool-queue-length > 0        → thread-pool starvation.  Step 2 territory.
    ├─ monitor-lock-contention-count high → lock contention.         Step 3 territory.
    ├─ time-in-gc high                    → GC pauses.               §7 + Step 4.
    └─ all flat                           → you're waiting on I/O (DB, HTTP, disk).
                                            Prove it with dotTrace Timeline or §8.4.
```

Most "performance problems" in ASP.NET Core services land in the bottom branch. That's why this
document spends as much time on thread-pool and GC counters as on CPU sampling.

### 6.2 Sampling vs. instrumentation

| | Sampling | Instrumentation |
|---|---|---|
| Mechanism | Interrupt threads at ~1 kHz, record stacks | Inject probes at method entry/exit |
| Overhead | ~1–5% | 10× and up for small methods |
| Answers | "Where is wall-clock/CPU time?" | "How many times was X called?" |
| Blind spot | Methods faster than the sample interval; **misses blocked time unless the profiler samples blocked threads too** | Distorts the thing it measures |
| Statistical validity | Needs enough samples — 30 s minimum, longer for rare paths | Exact |

A crucial subtlety: **CPU sampling does not show blocked time.** A thread blocked on
`Task.Result` consumes no CPU, so it produces no samples, so it is invisible in a flamegraph — while
being the actual cause of your latency. This is the #1 reason people conclude "the profiler says
nothing is slow". Use wall-clock/timeline profiling (dotTrace Timeline, PerfView's
`ThreadTime` view, or `dotnet-trace` with TPL + contention providers) for those.

### 6.3 Reading a flamegraph

- **Width = total time**, nothing else. Height is just stack depth, it means nothing.
- Use **Left Heavy / merged** ordering to find aggregate costs.
- A wide plateau of framework code (`System.Text.Json`, `Microsoft.EntityFrameworkCore`) is not
  automatically a problem — look one frame up to see *your* code that's calling it in a loop.
- Look for **unexpected recursion** (repeating stack patterns) and **unexpectedly wide `Regex`,
  `String.Format`, `Enum.ToString`, `Activator.CreateInstance`, reflection, and LINQ chains** — these
  are the usual suspects.
- Wide `CORINFO_HELP_*` or `JIT_New` frames = allocation cost surfacing as CPU.

### 6.4 Tiered compilation, OSR, and PGO — and how they distort measurement

.NET compiles methods in tiers:

- **Tier 0**: fast to compile, slow code, emitted on first call.
- **Tier 1**: fully optimized, emitted after ~30 calls, in the background.
- **OSR (On-Stack Replacement)** *[default since .NET 7]*: a long-running loop that started in tier 0
  gets swapped to optimized code mid-execution. Without it, a method entered once with a million-
  iteration loop would run tier-0 code forever.
- **Dynamic PGO** *[on by default since .NET 8]*: tier-0 code is instrumented, and the profile
  (which types actually showed up at a virtual call site, which branches were taken) feeds tier-1
  compilation — enabling guarded devirtualization and inlining that static analysis can't justify.

Measurement consequences:

- **Short-lived processes never reach tier 1.** If you benchmark a CLI tool's total runtime, you are
  measuring tier-0 code. That's legitimate if it matches production — just know which you're
  measuring.
- **The first N requests of a web app are slower.** Always warm up before a load test, or your
  results describe JIT, not your code.
- **Dynamic PGO makes interface-heavy code faster in steady state than a naive benchmark suggests**,
  because in a benchmark only one implementation is ever seen, so PGO devirtualizes perfectly. In
  production with 5 implementations at the same call site, the guarded devirtualization check fails
  and you pay the virtual call. **Benchmarks systematically flatter abstraction.** If it matters,
  benchmark with a mix of types.

Environment variables for investigating:

```bash
DOTNET_TieredCompilation=0     # everything at tier 1 immediately (slow startup, steady-state code)
DOTNET_TieredPGO=0             # disable dynamic PGO
DOTNET_ReadyToRun=0            # ignore precompiled framework code, force JIT — for measuring JIT quality
DOTNET_TC_QuickJitForLoops=0   # pre-OSR behaviour
```

> These are diagnostic switches, not production tuning. `TieredCompilation=0` in production trades
> much worse startup for marginal steady-state gain and is almost always wrong.

### 6.5 Startup time measurement

Different problem, different tools:

```bash
# What was JITted, and how much?
DOTNET_JitDisasmSummary=1 DOTNET_JitStdOutFile=jit.txt dotnet run -c Release

# Event-level startup trace
dotnet-trace collect --format speedscope -- ./MyApp
```

Watch `il-bytes-jitted` and `time-in-jit` in `dotnet-counters` — if they're large at startup,
**ReadyToRun** (`<PublishReadyToRun>true</PublishReadyToRun>`) or **NativeAOT**
(`<PublishAot>true</PublishAot>`) is the lever. Measure with:

```bash
dotnet publish -c Release -r win-x64 -p:PublishReadyToRun=true
# then measure cold start properly — hyperfine or a simple loop, 20+ runs, report median
```

> **[.NET 10]** NativeAOT's type preinitializer handles all `conv.*` and `neg` opcodes, so more static
> constructors get folded at publish time — measurably better NativeAOT startup for code with
> numeric/casting-heavy static initialization.

---

## 7. Memory analysis in depth

### 7.1 The layout you're reasoning about

```
Process memory
├── Managed heap
│   ├── SOH (Small Object Heap) — objects < 85,000 bytes
│   │   ├── Gen 0   allocation happens here; collected constantly; cheap
│   │   ├── Gen 1   survivors of gen0; a buffer generation
│   │   └── Gen 2   survivors of gen1; long-lived; collection is expensive
│   ├── LOH (Large Object Heap) — arrays/objects ≥ 85,000 bytes
│   │   └── collected with gen2; NOT compacted by default → fragmentation
│   └── POH (Pinned Object Heap) [.NET 5+] — GC.AllocateArray(pinned: true)
│       └── keeps pinned buffers out of the SOH so they don't fragment it
├── Native heap — interop, NativeMemory.Alloc, some BCL internals, the runtime itself
├── Loader heaps — types, methods, statics, generic instantiations. Grows with assembly/generic count.
└── Stacks — 1 MB reserved per thread by default (Windows); relevant when thread count explodes
```

`working-set ≫ gc-heap-size` means your problem is **not** managed objects. Look at native memory:
unmanaged libraries, `RecyclableMemoryStream` pools, gRPC/HTTP buffers, loader heap growth from
runtime-generated generics, or simply an unreturned `ArrayPool` rent.

**The generational hypothesis** the whole design rests on: most objects die young. Your job as an
optimizer is to keep it true. An object that survives to gen2 costs vastly more than one that dies in
gen0 — this is why "allocate less" and "allocate *shorter-lived*" are different goals, and why a
cache with a bad eviction policy can be worse than no cache.

### 7.2 GC modes — what you're actually running

| Setting | Default | Effect |
|---|---|---|
| `ServerGarbageCollection` | **true** for ASP.NET Core, false for console | One heap + one GC thread **per core**. Higher throughput, higher memory, longer individual pauses. |
| `ConcurrentGarbageCollection` | true | Background GC: gen2 collection runs mostly concurrently with the app. Two short suspensions rather than one long one. |
| `GCDynamicAdaptationMode` (DATAS) | **1 (on)** since **.NET 9** for Server GC | Dynamically tunes heap count and committed memory to actual load. |
| `GCHeapHardLimit` / `GCHeapHardLimitPercent` | 75% of container limit | The GC respects cgroup limits automatically in containers. |
| `GCConserveMemory` (0–9) | 0 | Trades CPU for smaller heap. Useful when memory-constrained. |
| `GCHeapCount` | = cores (Server GC) | Manual heap-count pin. |

**DATAS is the biggest practical change in recent .NET memory behaviour** and it will surprise you
when reading metrics. Pre-.NET 9, Server GC grew the heap under load and never gave it back; a
service that peaked at 4 GB stayed at 4 GB. With DATAS on by default in .NET 9+:

- Heap size tracks live-data size rather than machine size.
- Memory is returned to the OS when load drops.
- The same workload uses similar memory on a 4-core and a 64-core box.
- **Trade-off:** slightly lower peak throughput on some workloads, because the GC starts with fewer
  heaps and scales up. If you migrated to .NET 9/10 and saw a throughput dip alongside a large
  memory drop, this is why.

Configure explicitly in the csproj so it's visible in code review:

```xml
<PropertyGroup>
  <ServerGarbageCollection>true</ServerGarbageCollection>
  <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
  <!-- Only turn DATAS off with a measurement that justifies it -->
  <GarbageCollectionAdaptationMode>1</GarbageCollectionAdaptationMode>
</PropertyGroup>
```

or in `runtimeconfig.json` / env (`DOTNET_GCDynamicAdaptationMode=0`).

> **[.NET 10]** Background GC compaction improvements reduce fragmentation that previously accumulated
> in long-running services, and on **Arm64** the new default write-barrier implementation tracks GC
> regions more precisely, yielding **8–20% GC pause improvements**. If you run on Graviton/Ampere,
> this alone can justify the upgrade.

### 7.3 Measuring allocation rate

Cheapest signal, in order:

1. `dotnet-counters` → `alloc-rate` and `gen-0-gc-count`. Zero code changes.
2. BDN `[MemoryDiagnoser]` → exact bytes per operation for the code path you suspect.
3. `dotnet-trace --profile gc-verbose` → allocation call-sites via `GCAllocationTick` (sampled every
   ~100 KB, so it's statistical, not exhaustive).
4. VS **.NET Object Allocation Tracking** → every allocation with a stack. Most detailed, most
   overhead.

In-process, for a targeted measurement without any tooling:

```csharp
long before = GC.GetAllocatedBytesForCurrentThread();
DoWork();
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
```

`GC.GetAllocatedBytesForCurrentThread()` is cheap and precise, which makes it excellent for an
assertion in a unit test:

```csharp
[Fact]
public void Parse_Is_Allocation_Free()
{
    Parse(Sample);                        // warm up / JIT
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 100; i++) Parse(Sample);
    long after = GC.GetAllocatedBytesForCurrentThread();
    Assert.Equal(0, after - before);
}
```

This is a genuinely underused technique: it turns "don't regress this hot path" into a test that
fails in CI, and it's far more stable than timing assertions.

### 7.4 Detecting a leak

Definition for our purposes: **retained set grows without bound under steady-state load.**

Procedure:

1. Put the service under constant, representative load.
2. Wait for it to reach steady state (caches warm, pools sized) — typically 5–10 minutes.
3. Record `gc-heap-size` immediately after each gen2 GC. Rising floor = leak.
4. `dotnet-gcdump collect` → wait 10 min → `dotnet-gcdump collect` again.
5. Diff in Visual Studio. Note the types with the largest **count** growth.
6. `dotnet-dump collect` → `analyze` → `!dumpheap -stat` → `!gcroot <addr>` on an instance of the
   growing type.

Step 6 is what actually finds the bug. Usual answers, roughly in order of frequency:

| Root shown by `!gcroot` | Actual cause |
|---|---|
| A static field | A static `Dictionary`/`List` cache with no eviction |
| An event's invocation list | Subscriber never unsubscribed (`+=` without `-=`) |
| A `Timer` / `CancellationTokenSource` | Registered callback keeps the target alive; CTS registrations not disposed |
| An async state machine | A never-completing `Task` pinning its closure |
| `ThreadStatic` / `AsyncLocal` | Per-request state escaping the request |
| A DI container | A `Scoped` service captured by a `Singleton` — **the most common ASP.NET Core leak** |

That last one has a compile-time-ish guard worth turning on in every project:

```csharp
builder.Host.UseDefaultServiceProvider((ctx, opts) =>
{
    opts.ValidateScopes = true;      // catch scoped-captured-by-singleton
    opts.ValidateOnBuild = true;     // fail fast at startup, not on first request
});
```

(Both default to `true` in Development only; enable them in Staging load tests too.)

### 7.5 Reading a dump like you mean it

```
> dumpheap -stat
...
00007ffb1c2a4d30   127384     12228864 System.String
00007ffb1c2b1180    98211      7856880 MyApp.Models.OrderDto
                                        ^count   ^total bytes

> dumpheap -mt 00007ffb1c2b1180        # list instances
> gcroot 000001f8a4c31a20              # why is one alive
HandleTable:
    000001f89c0f1288 (strong handle)
    -> 000001f8a4001234 MyApp.Services.OrderCache
    -> 000001f8a4005678 System.Collections.Generic.Dictionary<...>
    -> 000001f8a4c31a20 MyApp.Models.OrderDto
```

Read the chain top-down: a strong handle (usually a static) holds `OrderCache`, which holds a
`Dictionary`, which holds your DTO. The leak is the cache, not the DTO.

Other high-value commands:

```
objsize <addr>       # retained size — how much memory frees if this dies
eeheap -gc           # region/segment layout; shows LOH and POH sizes
dumpheap -type Free -min 1000   # free-space blocks = fragmentation map
gchandles -stat      # pinned/strong handle counts — pinning source
```

### 7.6 Fragmentation and the LOH

Symptoms: `gc-fragmentation` high, working set high, but `gc-heap-size` live data is small.

Causes and levers:

- **Pinning** — `fixed`, `GCHandle.Alloc(..., Pinned)`, some interop and socket code. Fix by
  allocating pinned buffers on the **POH**:
  ```csharp
  byte[] buffer = GC.AllocateArray<byte>(4096, pinned: true);   // .NET 5+
  ```
- **LOH churn** — arrays ≥ 85,000 bytes. The LOH is not compacted by default. Either pool them
  (`ArrayPool<T>.Shared` — Step 4) or, as a last resort, compact once:
  ```csharp
  GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
  GC.Collect();   // expensive, blocking. Maintenance windows only.
  ```
- **Regions** *[.NET 7+]*: the GC replaced segments with smaller regions, which materially improved
  fragmentation and made memory return to the OS more granular. Another reason older guidance about
  .NET Framework heap behaviour doesn't transfer.

---

## 8. ASP.NET Core specifics

### 8.1 Built-in metrics — wire these up before anything else

**[.NET 8+]** ASP.NET Core publishes first-class `Meter` instruments. This is a 10-line change that
gives you layer-1 observability permanently:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()      // http.server.request.duration, active_requests
        .AddHttpClientInstrumentation()      // http.client.request.duration, time_in_queue
        .AddRuntimeInstrumentation()         // GC, thread pool, exceptions, JIT
        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
        .AddPrometheusExporter())
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());

app.MapPrometheusScrapingEndpoint();
```

The instruments that earn their keep:

| Instrument | Tells you |
|---|---|
| `http.server.request.duration` | Histogram, tagged by route/status/method. Your p99 source of truth. |
| `http.server.active_requests` | The `L` in Little's Law, measured directly. |
| `kestrel.queued_connections` / `kestrel.queued_requests` | Connections accepted but not yet served — **saturation**, before latency shows it. |
| `http.client.request.time_in_queue` | **Waiting for a pooled `HttpClient` connection.** If this is non-zero, raise `MaxConnectionsPerServer` or fix connection churn. |
| `aspnetcore.routing.match_attempts` | Routing cost with large route tables. |

`http.client.request.time_in_queue` is one of the most valuable and least-known metrics in .NET: it
distinguishes "the downstream service is slow" from "we're rate-limiting ourselves at the connection
pool", which look identical in end-to-end latency.

### 8.2 Detecting thread-pool starvation in production

Three independent signals; you want at least two agreeing:

1. `threadpool-queue-length` sustained > 0 while CPU < 50%.
2. `threadpool-thread-count` climbing slowly (the hill-climbing algorithm injects roughly one thread
   per 0.5–1 s when the queue doesn't drain).
3. `dotnet-stack report` shows many threads parked in `Monitor.Wait`, `Task.Result`,
   `.GetAwaiter().GetResult()`, `Thread.Sleep`, or a synchronous I/O frame.

Confirm with a trace:

```bash
dotnet-trace collect -p <pid> --duration 00:00:30 \
  --providers Microsoft-DotNETCore-SampleProfiler,System.Threading.Tasks.TplEventSource:0x1FF:4
```

A useful guard rail for load tests — log when the pool is under pressure:

```csharp
ThreadPool.GetAvailableThreads(out int worker, out int io);
ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
logger.LogWarning("Worker threads in use: {InUse}/{Max}", maxWorker - worker, maxWorker);
```

Raising `ThreadPool.SetMinThreads` is a **symptom mask**, not a fix — it buys you time while you
remove the blocking call. Step 2 covers the removal.

### 8.3 Load generation

You cannot measure a web application without load. Options, roughly in order of setup cost:

| Tool | Good for |
|---|---|
| **bombardier** / **hey** | 30-second smoke tests, single endpoint |
| **k6** | Scripted scenarios, ramping profiles, thresholds-as-code, CI gates |
| **NBomber** | Same in C#, so your load test can share DTOs/auth with the app |
| **Crank** (`dotnet tool install -g Microsoft.Crank.Controller`) | What the ASP.NET team itself uses; reproducible, runs on dedicated hardware, emits counters alongside throughput |

Whatever you use: **ramp up, hold steady, and discard the first 60 seconds**. The warm-up period
contains JIT, connection-pool fill, and cache population — real effects, but not the steady-state
behaviour your SLOs describe. Measure them separately if you care about deploys and cold starts.

### 8.4 When the app is innocent

This series is about application-side performance, so you need a fast way to rule the DB in or out:

```bash
dotnet-trace collect -p <pid> --profile database --duration 00:00:30
```

Or instrument EF Core directly:

```csharp
builder.Services.AddDbContext<AppDb>(o => o
    .UseSqlServer(cs)
    .LogTo(Console.WriteLine, LogLevel.Information)
    .EnableSensitiveDataLogging(builder.Environment.IsDevelopment()));
```

What you're looking for:

- **Query count per request.** More than a handful ⇒ N+1. This is an *application* bug (missing
  `Include`, lazy loading in a projection), not a database problem, and no index will fix it.
- **Total DB time vs. total request time.** If DB is 80% of the request, stop reading this series and
  go tune queries. If it's 10%, the DB is not your problem regardless of what the DBA says.
- **`http.client.request.time_in_queue` / connection-pool waits.** Frequently mistaken for DB
  slowness when it's actually self-inflicted pool exhaustion.

---

## 9. Advanced: codegen, hardware counters, and custom trace analysis

### 9.1 Looking at the generated assembly

Two routes. The zero-setup one, using runtime env vars (works in **Release** builds since .NET 7):

```bash
DOTNET_JitDisasm="MyMethod"        # dump asm for matching methods
DOTNET_JitDisasmSummary=1          # one line per JITted method — what got compiled, and at which tier
DOTNET_JitStdOutFile=jit.asm
DOTNET_TieredCompilation=0         # so you see optimized code, not tier-0
dotnet run -c Release
```

The structured one, inside BDN:

```csharp
[DisassemblyDiagnoser(maxDepth: 3, printSource: true, exportHtml: true)]
public class Vectorization { /* ... */ }
```

What you're actually looking for — this is where a lot of Step 4 gets verified:

| Look for | Means |
|---|---|
| `vmovdqu` / `vpaddd` / `ymm*` / `zmm*` registers | SIMD — the JIT vectorized your loop |
| `call CORINFO_HELP_NEWSFAST` / `NEWARR_1_VC` | Heap allocation. Its **absence** proves stack allocation happened. |
| `call CORINFO_HELP_ASSIGN_REF` | A write barrier — a reference field assignment |
| `cmp` + `jae` before array access | A bounds check that wasn't elided |
| An actual `call` where you expected inlining | Method too big, or has a construct that blocks inlining |
| `call [rax+0x18]` style indirect calls | A virtual/interface call that wasn't devirtualized |

> **[.NET 10]** The stack-allocation and devirtualization improvements listed in §4.5 are *visible
> here*: small `int[]`/`string[]` literals, non-escaping delegates, and objects referenced only via
> non-escaping local struct fields should show **no** `CORINFO_HELP_NEW*` call. This is the cleanest
> way to confirm you're getting the .NET 10 benefit rather than assuming it.

Also useful: [SharpLab](https://sharplab.io) for a quick "what IL/asm does this C# produce" without
setting anything up. Set the output to **JIT Asm** and the branch to the newest available.

### 9.2 Hardware performance counters

When you've eliminated allocations and algorithmic problems and code is still slow, you're usually
fighting the memory hierarchy or the branch predictor.

```csharp
[HardwareCounters(
    HardwareCounter.BranchMispredictions,
    HardwareCounter.CacheMisses,
    HardwareCounter.InstructionRetired,
    HardwareCounter.TotalCycles)]
public class DataLayout { }
```

**Windows + administrator only** (it uses ETW kernel providers). On Linux, use `perf stat` /
`perf record` against the dotnet process with `DOTNET_PerfMapEnabled=1` so managed frames resolve:

```bash
DOTNET_PerfMapEnabled=1 dotnet run -c Release &
perf stat -e cycles,instructions,cache-misses,branch-misses -p <pid> -- sleep 30
```

Interpretation:

| Observation | Likely cause | Fix direction |
|---|---|---|
| IPC (instructions/cycle) < ~1 | Memory-bound or branch-bound | Data layout, prefetch-friendly access |
| High cache misses, low branch misses | Pointer chasing / poor locality | Struct-of-arrays, contiguous storage, arrays over linked structures |
| High branch mispredictions | Unpredictable branches in a hot loop | Branchless code, sorting input, `Math.Max` over `if` |
| High cycles, low cache misses, low branch misses | Genuinely compute-bound | SIMD, better algorithm |

This is also where the classic "array of structs vs. list of classes" difference shows up
concretely — a `List<SomeClass>` is an array of pointers into scattered heap objects; a
`SomeStruct[]` is contiguous. Same big-O, order-of-magnitude different cache behaviour. Step 4
covers the techniques; this is how you prove they worked.

### 9.3 PerfView and TraceEvent

PerfView is the deepest tool available for Windows .NET analysis, and it is ugly enough that most
people bounce off it. The views worth learning:

| View | Answers |
|---|---|
| **CPU Stacks** | Classic sampled CPU profile, with grouping/folding far beyond other tools |
| **Thread Time (with StartStop Activities)** | **Wall-clock, not CPU** — shows blocked time and attributes it to async activities. The single best view for async latency. |
| **GC Heap Alloc Ignore Free** | Allocation call-sites by type |
| **GCStats** | Per-GC table: gen, pause ms, reason, condemned generation, promoted bytes. Definitive GC report. |
| **Any Stacks** | Arbitrary events with stacks — e.g. every exception thrown, with its stack |

PerfView's grouping/folding syntax (`GroupPats`, `FoldPats`) is what makes it powerful: you can fold
all framework code into one node to see only your code, then progressively unfold.

Programmatic analysis via **`Microsoft.Diagnostics.Tracing.TraceEvent`** — this is how you build
custom, repeatable analysis instead of clicking through a GUI every time:

```csharp
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

using var log = TraceLog.OpenOrConvert("trace.nettrace");
var gcs = log.Events
    .Where(e => e.EventName == "GC/Stop")
    .Select(e => new { e.TimeStampRelativeMSec, Gen = e.PayloadByName("Depth") })
    .ToList();
Console.WriteLine($"Gen2 collections: {gcs.Count(g => (int)g.Gen! == 2)}");
```

Use case: a CI job that captures a 60-second trace of a load test and fails the build if gen2
collections exceed a threshold, or if any single GC pause exceeds 50 ms. That's a far better
regression gate than a latency number, because it's causal rather than correlational.

### 9.4 Custom instrumentation you should add anyway

For code you own, cheap purpose-built instrumentation beats general tooling:

```csharp
// A Meter is the modern, OTel-native EventSource. Near-zero cost when nothing listens.
internal static class AppMetrics
{
    private static readonly Meter Meter = new("MyApp.Orders", "1.0.0");

    public static readonly Histogram<double> PricingDuration =
        Meter.CreateHistogram<double>("myapp.orders.pricing.duration", "ms", "Time to price an order");

    public static readonly Counter<long> CacheMisses =
        Meter.CreateCounter<long>("myapp.orders.cache.misses");

    // .NET 9+: observe a value only when someone actually scrapes it
    public static void RegisterGauges(Func<int> queueDepth) =>
        Meter.CreateObservableGauge("myapp.orders.queue.depth", queueDepth);
}
```

Then it shows up in `dotnet-counters`, Prometheus, and your APM with no further work:

```bash
dotnet-counters monitor -p <pid> MyApp.Orders
```

For high-frequency events where even a `Meter` is too much, drop to `EventSource` with
`IsEnabled()` guards — the cost when no listener is attached is a single boolean check:

```csharp
[EventSource(Name = "MyApp-Orders")]
internal sealed class OrderEventSource : EventSource
{
    public static readonly OrderEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void OrderPriced(int orderId, double ms)
    {
        if (IsEnabled()) WriteEvent(1, orderId, ms);
    }
}
```

Now `dotnet-trace collect --providers MyApp-Orders` gives you a production-attachable, zero-cost-when-off
trace of exactly the thing you care about.

---

## 10. Measurement mistakes that produce confident wrong answers

| # | Mistake | Why it's wrong | Do instead |
|---|---|---|---|
| 1 | Benchmarking a Debug build | No optimizations, no inlining; results are unrelated to production | `-c Release`, always. BDN enforces it. |
| 2 | Timing with `Stopwatch` in a loop | No warm-up, no tiering settling, no statistics, no outlier handling | BenchmarkDotNet |
| 3 | Profiling under the debugger | The debugger disables optimizations and adds per-call overhead | `Alt+F2` Performance Profiler, or detached CLI tools |
| 4 | Reporting averages | Hides the tail users actually feel | p50/p95/p99 + the histogram shape |
| 5 | Measuring on a laptop on battery | Thermal/power throttling, 20%+ swing | Plugged in, dedicated CI runner, or at minimum acknowledge the variance |
| 6 | One run, one number | You can't distinguish signal from noise with n=1 | Multiple iterations, report Mean ± Error, use `--statisticalTest` |
| 7 | Optimizing what the profiler shows first | The top frame is often framework code called correctly | Look at *your* frames and at Amdahl's ceiling (§1.2) |
| 8 | Trusting a CPU profile for a blocked-thread problem | Blocked threads generate no CPU samples | Wall-clock/timeline profiling (dotTrace Timeline, PerfView Thread Time) |
| 9 | Not warming up a web app before load testing | You measure JIT and cold caches | Discard the first 60 s |
| 10 | Benchmarking with one implementation at a polymorphic call site | Dynamic PGO devirtualizes perfectly in the benchmark, never in prod | Feed a realistic mix of types |
| 11 | Comparing numbers across machines/runners | Hardware, OS, power profile, and CPU generation all differ | Compare ratios, and only within one runner class |
| 12 | Treating `time-in-gc` as pause time | Background GC does most gen2 work concurrently | `dotnet.gc.pause.time` or `GCMemoryInfo.PauseDurations` |
| 13 | Changing two things at once | You learn nothing about either | One variable per measurement |
| 14 | Not re-measuring end-to-end after the fix | The benchmark improved; the SLO may not have | Always close the loop (§1.1 step 4) |

---

## 11. Runbook / cheat sheet

### 11.1 "Production is slow" — first 10 minutes

```bash
dotnet-counters ps
dotnet-counters monitor -p <pid> --refresh-interval 1 \
  System.Runtime Microsoft.AspNetCore.Hosting
```

Read four numbers, in this order:

1. `cpu-usage` — saturated or not?
2. `threadpool-queue-length` — starvation?
3. `time-in-gc` / `alloc-rate` — GC pressure?
4. `working-set` vs `gc-heap-size` — managed or native memory?

Then branch per §6.1's decision tree.

### 11.2 "It's the CPU"

```bash
dotnet-trace collect -p <pid> --profile cpu-sampling --duration 00:00:30 \
  --format speedscope -o cpu.speedscope.json
# open in speedscope.app → Left Heavy view
```

### 11.3 "It's the memory"

```bash
dotnet-gcdump collect -p <pid> -o t0.gcdump
sleep 600
dotnet-gcdump collect -p <pid> -o t1.gcdump
# Visual Studio: open t1, compare to baseline t0, sort by size diff
# then: dotnet-dump collect -p <pid>; dotnet-dump analyze <file>
#       > dumpheap -stat ; gcroot <addr>
```

### 11.4 "It's hung"

```bash
dotnet-stack report -p <pid> > s1.txt
sleep 5
dotnet-stack report -p <pid> > s2.txt
diff s1.txt s2.txt          # identical → deadlock/blocked pool
# deeper:
dotnet-dump collect -p <pid> && dotnet-dump analyze <file>
#   > dumpasync --stats ; syncblk ; threadpool
```

### 11.5 "Is B faster than A?"

```bash
dotnet run -c Release --project benchmarks/PerfLab.Benchmarks -- \
  --filter '*MyThing*' --memory --statisticalTest 5% --exporters json html
```

### 11.6 "Should we upgrade the runtime?"

```bash
dotnet run -c Release --project benchmarks/PerfLab.Benchmarks -- \
  --filter '*' --runtimes net8.0 net10.0 --memory
```

### 11.7 Minimum permanent instrumentation for any service

- OpenTelemetry metrics: ASP.NET Core + HttpClient + Runtime instrumentation (§8.1)
- Dashboards: p50/p95/p99 by route, `active_requests`, `threadpool.queue.length`,
  `gc.pause.time`, `gc.heap.total_allocated_bytes` rate, `working_set`
- Alerts: p99 SLO breach, `threadpool.queue.length > 0` sustained, gen2 rate, working-set trend
- `dotnet-monitor` sidecar with collection rules for auto-capture on CPU/memory triggers (§3.7)
- A BDN suite in CI gating on **allocations per op** (§4.8)

---

## Where this goes next

| Step | Document | What it builds on from here |
|---|---|---|
| 2 | [AsyncVsSync.md](AsyncVsSync.md) | The thread-pool counters in §2.4 / §8.2 are how you detect and then *prove you fixed* blocking and starvation. |
| 3 | [ParallelismVsConcurrency.md](ParallelismVsConcurrency.md) | `[ThreadingDiagnoser]`, `monitor-lock-contention-count`, and dotTrace Timeline are the measurement basis for parallel work. |
| 4 | [MemoryOptimizationTricks.md](MemoryOptimizationTricks.md) | `[MemoryDiagnoser]`, `GC.GetAllocatedBytesForCurrentThread()` assertions, and §9.1's "no `CORINFO_HELP_NEW*` in the asm" check are how you verify no-allocation code actually allocates nothing. |

---

## References

- [Performance Improvements in .NET 10 — .NET Blog](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
- [What's new in the .NET 10 runtime — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime)
- [.NET diagnostic tools overview — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/tools-overview)
- [dotnet-counters — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)
- [dotnet-trace — dotnet/docs](https://github.com/dotnet/docs/blob/main/docs/core/diagnostics/dotnet-trace.md)
- [Dynamic adaptation to application sizes (DATAS) — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas)
- [dotnet/diagnostics releases](https://github.com/dotnet/diagnostics/releases)
- [.NET 10 de-abstraction plans — dotnet/runtime#108913](https://github.com/dotnet/runtime/issues/108913)
- [Improve Arm64 performance in .NET 10 — dotnet/runtime#109652](https://github.com/dotnet/runtime/issues/109652)
- [What's new in .NET 11 — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)
- [BenchmarkDotNet documentation](https://benchmarkdotnet.org/)
