# Step 1 — Ways to Measure .NET Performance

> **Baseline:** .NET 10 (LTS). `[.NET 8]` / `[.NET 9]` / `[.NET 10]` tags mark arrivals.
> **Scope:** finding where CPU, memory, and threads actually go. This is the foundation;
> Steps 2–4 are the fixes.
> **Companion code:** [samples/PerfLab/](samples/PerfLab/) — see [samples/README.md](samples/README.md).
> **All numbers here are measured** on an i5-13420H, .NET 10.0.4, `--job short`.
> Raw output: [samples/PerfLab/BenchmarkDotNet.Artifacts/results/](samples/PerfLab/BenchmarkDotNet.Artifacts/results/).

## The one-sentence version

**Measure first, measure after, and compare ratios rather than absolute numbers.** A benchmark that
got 10× faster while p99 didn't move means your model of the system was wrong — useful information,
but not a performance improvement.

## The four numbers you read first

When someone says "production is slow", attach `dotnet-counters` and read these, in order.
Everything else here is what you do *after* one of them points somewhere.

| # | Counter | What it settles |
|---|---|---|
| 1 | `cpu-usage` | Saturated or not? (% of one core × core count — 800% on 8 cores is maxed) |
| 2 | `threadpool-queue-length` | Sustained > 0 with low CPU = **starvation** |
| 3 | `time-in-gc` / `alloc-rate` | GC pressure. Over 10% time-in-gc and nothing else matters |
| 4 | `working-set` vs `gc-heap-size` | Managed objects, or native/other? |

## Symptom → fix

| What you observe | What it means | What to do |
|---|---|---|
| CPU high, latency bad | Genuinely CPU-bound | [It's the CPU](#its-the-cpu) |
| CPU high, latency fine | At capacity | Scale, or cut CPU per request. Not urgent, but it's a ceiling |
| **CPU low, latency bad, queue growing** | Thread-pool starvation | [Thread-pool health](#thread-pool-health), then [Step 2](AsyncVsSync.md) |
| CPU low, `monitor-lock-contention-count` high | Lock contention | [Step 3](ParallelismVsConcurrency.md) |
| CPU low, `time-in-gc` high | GC pauses | [It's the memory](#its-the-memory), then [Step 4](MemoryOptimizationTricks.md) |
| CPU low, everything flat | Waiting on I/O | [Is the app even the problem?](#is-the-app-even-the-problem) |
| `gc-heap-size` sawtooths to a stable floor | Allocation rate | [Step 4](MemoryOptimizationTricks.md) |
| `gc-heap-size` floor **rises** every cycle | Retention — a leak | [It's a leak](#its-a-leak). An allocation fix will not help |
| `working-set` ≫ `gc-heap-size` | Not managed objects | Native memory, loader heaps, an unreturned `ArrayPool` rent |
| Bimodal latency histogram | Two code paths, or a queueing threshold | Not noise — a diagnosis |
| Profiler says "nothing is slow" | Blocked threads emit no CPU samples | [Timeline profiling](#profilers-visual-studio-and-rider) |
| Benchmark improved 10×, p99 didn't move | You fixed 0.3% of the request | Amdahl, below |

**Amdahl's law, as a filter to apply *before* writing code:** if a method is **5% of the request**
and you make it **infinitely fast**, the ceiling is **5.3%**. That's the entire prize.

## The runbook

Six situations, and exactly what to run for each. Install the tools first — and **don't bake them
into the app image**; run them from a sidecar sharing the process namespace. `[.NET 10]` they can
attach across PID namespaces, which is why older guides saying otherwise are out of date.

```bash
dotnet tool install -g dotnet-counters   # live metrics — start here
dotnet tool install -g dotnet-trace      # CPU/event tracing (EventPipe)
dotnet tool install -g dotnet-gcdump     # managed heap graph, no full dump
dotnet tool install -g dotnet-dump       # full dumps + SOS
dotnet tool install -g dotnet-stack      # instant stack snapshot
```

### The first 10 minutes

```bash
dotnet-counters ps                       # find the process
dotnet-counters monitor -p <pid> --refresh-interval 1 \
  System.Runtime Microsoft.AspNetCore.Hosting \
  Microsoft.AspNetCore.Server.Kestrel System.Net.Http
```

Read the four numbers above, then branch to the matching section. To capture rather than watch, use
`dotnet-counters collect -p <pid> --format csv -o counters.csv --duration 00:02:00`.

| Counter | Reads as |
|---|---|
| `cpu-usage` | % of one core × core count |
| `working-set` / `gc-heap-size` | RSS vs managed heap. A large gap means native memory |
| `alloc-rate` | **The single best allocation-pressure number** |
| `gen-0/1/2-gc-count` | Gen2 more than ~1/min on a steady service is a smell |
| `time-in-gc` | See [GC pause time](#gc-pause-time-and-datas) — this is *not* pause time |
| `loh-size` / `poh-size` | LOH growth = arrays ≥ 85,000 bytes |
| `threadpool-queue-length` | **Starvation detector** |
| `monitor-lock-contention-count` | Lock contention rate |
| `exception-count` | Thousands/sec is a real cost *and* usually a logic bug |
| `il-bytes-jitted` | Should flatline after warm-up. If not, something emits dynamic code per request |

`[.NET 8]` the runtime also publishes OpenTelemetry-native `Meter` instruments
(`dotnet.gc.pause.time`, `dotnet.thread_pool.queue.length`, …). Those belong on your
[dashboards](#permanent-instrumentation); `dotnet-counters` reads both kinds on one command line.

### It's the CPU

```bash
dotnet-trace collect -p <pid> --profile cpu-sampling --duration 00:00:30 \
  --format speedscope -o cpu.speedscope.json
```

Open at <https://speedscope.app> (entirely client-side — no upload) and use **"Left Heavy"** first:
it merges identical stacks so the widest bar is genuinely your biggest cost. The default "Time Order"
view is for spotting phases, not finding hot code.

Reading it: **width = total time**; height is just stack depth and means nothing. A wide plateau of
framework code isn't automatically a problem — look one frame up for *your* code calling it in a
loop. Usual suspects are `Regex`, `String.Format`, `Enum.ToString`, `Activator.CreateInstance`,
reflection, and LINQ chains. Wide `CORINFO_HELP_*` frames are allocation cost surfacing as CPU.

### It's the memory

First decide **which** memory problem you have, because they have opposite fixes — see
[two memory problems](#two-memory-problems-with-opposite-fixes). For allocation rate, in order of
cheapness: `alloc-rate` in counters, then BDN `[MemoryDiagnoser]` for exact bytes per op, then
`dotnet-trace --profile gc-verbose` for call-sites (sampled every ~100 KB, so statistical), then
Visual Studio's allocation tracker for every allocation with a stack.

In-process, with no tooling at all:

```csharp
long before = GC.GetAllocatedBytesForCurrentThread();
DoWork();
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
```

Cheap, precise, and deterministic — which makes it an excellent **CI assertion**. See
[Step 4](MemoryOptimizationTricks.md).

### It's a leak

**A leak is a retained set that grows without bound under steady-state load.**

```bash
dotnet-gcdump collect -p <pid> -o t0.gcdump
sleep 600                                      # steady load throughout
dotnet-gcdump collect -p <pid> -o t1.gcdump
# Visual Studio: open t1, "compare to baseline" t0, sort by Size Diff
dotnet-dump collect -p <pid> && dotnet-dump analyze <file>
#   > dumpheap -stat        ← find the growing type
#   > gcroot <addr>         ← WHY it's alive. This is the command that finds the bug
```

A `.gcdump` holds the **object graph but no field values**, so it's safe to take off a production box
holding customer data — and it's megabytes where a full dump is gigabytes. (It does force a blocking
gen2 GC, so don't do every instance at once.)

**The type that grew is the symptom; the retention path is the bug.** Read the `gcroot` chain
top-down — a static handle holds a cache, which holds a dictionary, which holds your DTO. *The leak
is the cache, not the DTO.*

| Root shown by `gcroot` | Actual cause |
|---|---|
| A static field | A static `Dictionary`/`List` cache with no eviction |
| An event's invocation list | Subscriber never unsubscribed (`+=` without `-=`) |
| A `Timer` / `CancellationTokenSource` | Callback keeps the target alive; CTS registrations not disposed |
| An async state machine | A never-completing `Task` pinning its closure |
| `ThreadStatic` / `AsyncLocal` | Per-request state escaping the request |
| A DI container | A `Scoped` service captured by a `Singleton` — **the most common ASP.NET Core leak** |

That last one has a guard worth turning on everywhere. Both default to `true` in Development only —
**enable them in Staging load tests too:**

```csharp
builder.Host.UseDefaultServiceProvider((ctx, opts) =>
{
    opts.ValidateScopes = true;      // catch scoped-captured-by-singleton
    opts.ValidateOnBuild = true;     // fail fast at startup, not on first request
});
```

Separately, if `gc-fragmentation` is high while live data is small, you have **pinning** (fix with
`GC.AllocateArray<byte>(n, pinned: true)`, which uses the POH) or **LOH churn** from arrays ≥ 85,000
bytes, which the LOH doesn't compact by default — pool them ([Step 4](MemoryOptimizationTricks.md)).

### It's hung

```bash
dotnet-stack report -p <pid> > s1.txt
sleep 5
dotnet-stack report -p <pid> > s2.txt
diff s1.txt s2.txt        # identical → deadlock or blocked pool; churning → hot loop

dotnet-dump collect -p <pid> && dotnet-dump analyze <file>
#   > dumpasync --stats   ← the one that usually names the culprit
#   > syncblk             ← who holds which monitor
#   > threadpool          ← queue depths, worker/IOCP counts
#   > clrstack -all       ← managed stacks for every thread
```

**`dumpasync` deserves special mention.** In an async service `clrstack` shows a pool thread doing
nothing useful, because the interesting state is in heap-allocated state machines, not on stacks.
`dumpasync --stats` usually names the problem in one line — *12,000 pending `SendAsync` state
machines* means you have exhausted an `HttpClient` connection pool.

### Is the app even the problem?

This series is about application-side performance, so you need a fast way to rule the database in or
out:

```bash
dotnet-trace collect -p <pid> --profile database --duration 00:00:30
```

- **Query count per request.** More than a handful ⇒ **N+1**. That's an *application* bug — a missing
  `Include`, or lazy loading in a projection — and **no index will fix it**.
- **Total DB time vs total request time.** If DB is 80% of the request, stop reading this series and
  go tune queries. If it's 10%, the DB is not your problem regardless of what the DBA says.
- **Connection-pool waits.** Frequently mistaken for DB slowness when it's self-inflicted pool
  exhaustion — watch `http.client.request.time_in_queue`.

## What the numbers mean

### Latency, and Little's Law

**Percentiles, never averages** — an average hides the tail, and the tail is what users feel and what
cascades into timeouts. p50 is the typical experience; p95/p99 moves with GC pauses, lock contention,
starvation and retries; p99.9 is usually GC or connection-pool exhaustion. A **bimodal histogram is a
diagnosis, not noise** — two code paths, or a queueing effect past a concurrency threshold.

**`L = λ × W`** — requests in flight = arrival rate × latency. It predicts capacity from two numbers
you already have: 500 req/s at 200 ms ⇒ **100 in flight**. If each holds a pooled DB connection and
your pool max is **50**, you are *structurally* unable to serve 500 req/s, and no amount of CPU
optimization fixes that. Inverted: when latency doubles under constant load, in-flight count doubles,
which increases contention on every pooled resource — the feedback loop behind "it fell over all at
once".

### Two memory problems with opposite fixes

| | **Allocation rate** (throughput) | **Retained set** (capacity) |
|---|---|---|
| Symptom | High CPU in GC, p99 spikes | Working set grows monotonically, OOM, container kill |
| Metric | `alloc-rate`, gen0 collections/sec | Gen2 size, `gc-heap-size` after a full GC |
| Cause | Garbage per operation — LINQ, string concat, boxing, closures | Caches without eviction, statics, event-handler leaks |
| Fix | [Step 4](MemoryOptimizationTricks.md) | Ownership and lifetime — **an allocation fix will not help** |

**The 30-second diagnostic:** watch `gc-heap-size` across several gen2 collections. Sawtooths back to
a stable floor ⇒ allocation rate. Floor rises every cycle ⇒ retention.

The design rests on the **generational hypothesis** — most objects die young — and your job as an
optimizer is to keep it true. That's why "allocate less" and "allocate *shorter-lived*" are different
goals, and why a cache with a bad eviction policy can be worse than no cache.

### Thread-pool health

| Counter | Healthy | Meaning when unhealthy |
|---|---|---|
| `threadpool-queue-length` | ~0, transient spikes only | Work waiting for a thread. Sustained > 0 = **starvation** |
| `threadpool-thread-count` | Stable near core count | Climbing = the pool injecting threads (~1 per 0.5–1 s) to compensate for blocked ones |
| `monitor-lock-contention-count` | Low and flat | A lock is serializing your service |

**Thread-pool starvation is the classic .NET production incident:** latency through the roof while
CPU sits at 20%, so every CPU-based autoscaler declines to help. Three independent signals confirm
it, and **you want at least two agreeing** — queue length sustained > 0 while CPU < 50%, thread count
climbing slowly, and `dotnet-stack report` showing many threads parked in `Monitor.Wait`,
`Task.Result`, `.GetAwaiter().GetResult()`, or `Thread.Sleep`.

**Raising `ThreadPool.SetMinThreads` is a symptom mask, not a fix.** It buys time while you remove
the blocking call — [Step 2](AsyncVsSync.md) covers the removal.

### GC pause time and DATAS

For a web service: **< 5%** fine, **5–10%** worth a look, **> 10%** allocation rate is your top
problem and nothing else matters as much. But `time-in-gc` is **not** pause time — with background GC
most gen2 work is concurrent. Prefer `dotnet.gc.pause.time` `[.NET 9]`, which measures actual
suspension.

**DATAS is the biggest recent change in .NET memory behaviour, and it will surprise you when reading
metrics.** On by default since `[.NET 9]` for Server GC: heap size now tracks live-data size rather
than machine size, memory returns to the OS when load drops, and the same workload uses similar
memory on a 4-core and a 64-core box. Pre-.NET 9, a service that peaked at 4 GB stayed at 4 GB.
**Trade-off:** slightly lower peak throughput on some workloads — so if you migrated to .NET 9/10 and
saw a throughput dip alongside a large memory drop, this is why.

## Proving a change: BenchmarkDotNet

The only credible way to compare two implementations in .NET. It handles warm-up, tiered-compilation
settling, statistics, outlier detection, and process isolation — all things hand-rolled `Stopwatch`
loops get wrong. Project setup is in [samples/PerfLab/PerfLab.csproj](samples/PerfLab/PerfLab.csproj).

```csharp
[MemoryDiagnoser]                       // bytes allocated per op — use always
public class CsvFormatting
{
    [Params(10, 1_000)] public int N;   // does the winner change with size? usually yes
    [GlobalSetup] public void Setup() => _records = BuildRecords(N);

    [Benchmark(Baseline = true)]        // adds Ratio — the only number worth quoting
    public string StringConcat() { /* ... */ }

    [Benchmark] public string StringBuilder_() { /* ... */ }
}
```

```bash
dotnet run -c Release -f net10.0 --project samples/PerfLab -- bench --filter '*CsvFormatting*'
dotnet run -c Release -f net10.0 --project samples/PerfLab -- bench --list flat   # discover
```

### Reading the summary table

Real output from [samples/PerfLab/Benchmarks/Step1/](samples/PerfLab/Benchmarks/Step1/):

| Method | N | Mean | Error | Allocated |
|---|---|---|---|---|
| `StringBuilder_` | 10 | 293 ns | ±26 | 1,000 B |
| `StringCreate` | 10 | 468 ns | ±30 | 608 B |
| `StringConcat` (baseline) | 10 | 488 ns | ±162 | 2,144 B |
| `StringBuilder_NoInterpolation` | 1000 | 36,894 ns | ±15,054 | 102,176 B |
| `StringBuilder_` | 1000 | 37,434 ns | ±1,417 | 102,176 B |
| `StringCreate` | 1000 | 56,902 ns | ±4,850 | **72,768 B** |
| `StringConcat` (baseline) | 1000 | **923,303 ns** | ±41,069 | **18,654,920 B** |

Four readings, and three are about *not* over-reading:

1. **At N=1000 `StringConcat` is 25× slower and allocates 182× more.** O(n²) copying — the class of
   bug worth finding, not a 3% win.
2. **At N=10 it is only 1.7× slower.** Same code, same bug, effectively invisible. **This is why
   `[Params]` exists** — a single input size would have hidden the entire finding.
3. **The two `StringBuilder` rows are indistinguishable.** Means differ by 540 ns while one Error
   alone is 15,054 ns. **This benchmark has not shown a difference**; avoiding the interpolated
   string bought nothing, and only measurement told you that.
4. **`StringCreate` allocates 29% less and runs 52% slower.** "Fewer bytes" and "less time" are
   different claims in different columns, and neither implies the other.
   [Step 4 follows this one all the way down](MemoryOptimizationTricks.md#when-fewer-allocations-made-it-slower)
   — the obvious fix doesn't work either, for a more interesting reason.

**The one rule for reading any BDN table:** if two Means differ by less than the sum of their Errors,
**you have not shown a difference.** Also worth knowing: `Gen0/1/2` counts are **per 1000 operations**,
not per op; `Allocated` is managed heap only, which is why a `0 B` row proves a no-allocation rewrite
worked; and you should always read the legend and warnings BDN prints below the table.

### Diagnosers

| Attribute | Adds | Cost / caveat |
|---|---|---|
| `[MemoryDiagnoser]` | Allocated bytes, Gen0/1/2 | Nearly free. Use always |
| `[ThreadingDiagnoser]` | Lock contentions, completed work items | Essential for [Steps 2](AsyncVsSync.md) and [3](ParallelismVsConcurrency.md) |
| `[ExceptionDiagnoser]` | Exceptions/op | Finds accidental exception-driven control flow |
| `[DisassemblyDiagnoser]` | The asm actually emitted | Slow; see [reading the generated assembly](#reading-the-generated-assembly) |
| `[HardwareCounters(...)]` | CPU counters | **Windows + admin only.** See [hardware counters](#hardware-counters) |

### Comparing runtimes, and gating CI

One class, three runtimes, one table — **this is how you make an evidence-based upgrade case instead
of citing a blog post:**

```csharp
[SimpleJob(RuntimeMoniker.Net80, baseline: true)]
[SimpleJob(RuntimeMoniker.Net90)]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class RuntimeComparison { /* ... */ }
```

Requires `<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>`. Expect the largest .NET 10
wins on array/`IEnumerable` iteration (devirtualization), small short-lived arrays and delegates
(stack allocation), struct-heavy call chains, and Arm64 (write barriers — 8–20% GC pause
improvements).

```bash
dotnet run -c Release -f net10.0 --project samples/PerfLab -- bench \
  --filter '*' --runtimes net8.0 net10.0 --memory --statisticalTest 5%
```

`--statisticalTest` runs a Mann–Whitney U test and labels each result **Faster / Slower / Same**.
**Gate CI on that, and on allocations rather than time** — allocated-bytes-per-op is deterministic
and reproducible across machines; time is not. "No benchmark may allocate more than baseline + 5%"
catches most real regressions with almost no flakiness.

### Benchmark pitfalls that produce fake results

| Pitfall | The fake result | Do instead |
|---|---|---|
| Not returning the value | Dead-code elimination deletes the work | Return it, or `Consumer.Consume(x)` |
| Inputs from `readonly` / `[Params]` constants | Constant-folded at JIT time | Non-readonly fields set in `[GlobalSetup]` |
| Work in `[IterationSetup]` | Setup noise in the measurement | `[GlobalSetup]`, or add `[InvocationCount(1)]` |
| Shared mutable state between benchmarks | One fills a dictionary; the next looks fast | Reset in `[IterationCleanup]` |
| A benchmark too short to pay its own GC | The garbage lands on the *next* benchmark's time | `[GcForce(true)]` and compare |

## Permanent instrumentation

`[.NET 8]` A ten-line change that gives you layer-1 observability permanently:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()      // http.server.request.duration, active_requests
        .AddHttpClientInstrumentation()      // http.client.request.duration, time_in_queue
        .AddRuntimeInstrumentation()         // GC, thread pool, exceptions, JIT
        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
        .AddPrometheusExporter());

app.MapPrometheusScrapingEndpoint();
```

| Instrument | Tells you |
|---|---|
| `http.server.request.duration` | Histogram by route/status/method. **Your p99 source of truth** |
| `http.server.active_requests` | The `L` in Little's Law, measured directly |
| `kestrel.queued_connections` / `queued_requests` | **Saturation, before latency shows it** |
| `http.client.request.time_in_queue` | **Waiting for a pooled `HttpClient` connection** |

**`http.client.request.time_in_queue` is one of the most valuable and least-known metrics in .NET:**
it distinguishes "the downstream service is slow" from "we're rate-limiting ourselves at the
connection pool" — which look identical in end-to-end latency.

For code you own, a `Meter` is cheap and near-zero cost when nothing listens, and shows up in
`dotnet-counters`, Prometheus and your APM with no further work —
[samples/PerfLab/Api/AppMetrics.cs](samples/PerfLab/Api/AppMetrics.cs) is a working example.

**The minimum for any service:** the OTel wiring above; dashboards for p50/p95/p99 by route,
`active_requests`, `threadpool.queue.length`, `gc.pause.time`, allocation rate and `working_set`;
alerts on p99 SLO breach and sustained `threadpool.queue.length > 0`; a `dotnet-monitor` sidecar with
collection rules so incidents are captured rather than reproduced; and a BDN suite in CI gating on
allocations per op.

## Load generation

You cannot measure a web application without load. **bombardier** or **hey** for 30-second smoke
tests, **k6** for scripted scenarios and CI gates, **NBomber** for the same in C# sharing your DTOs
and auth, **Crank** for what the ASP.NET team itself uses.

Whatever you use: **ramp up, hold steady, and discard the first 60 seconds.** The warm-up contains
JIT, connection-pool fill and cache population — real effects, but not the steady state your SLOs
describe. Measure those separately if you care about deploys and cold starts.

## When you need more

### Profilers: Visual Studio and Rider

Use the **Performance Profiler** (`Alt+F2`), which works on Release without the debugger. Never trust
absolute numbers from a debug session.

| What you want | Tool |
|---|---|
| "Which line allocates this?" | VS **.NET Object Allocation Tracking** — the best allocation tool in the ecosystem. Heavy; use short runs |
| "Slow, but not CPU-bound" | **dotTrace Timeline** or VS **Async Tool** — per-thread Running/Waiting/Blocked over time |
| "What holds this object alive?" | **dotMemory** snapshots + diff, or a `gcroot` from a dump |
| N+1 queries | VS **Database** tool |
| Exact call counts | VS **Instrumentation** — but it inflates small methods enormously |

**dotTrace Timeline is the single best tool for [Steps 2](AsyncVsSync.md) and
[3](ParallelismVsConcurrency.md).** When a request takes 900 ms and the CPU profiler shows 40 ms of
CPU, Timeline shows the 860 ms of `Waiting` and what it was waiting on. For allocation hunting, sort
by **count** before bytes — huge counts of tiny objects point at a hot loop.

### Reading the generated assembly

Ground truth for "did the JIT vectorize it?" and "did that object really get stack-allocated?":

```bash
dotnet run -c Release -f net10.0 --project samples/PerfLab -- bench \
  --filter '*SimdSum*' --disasm
```

The one thing worth grepping for: **`call CORINFO_HELP_NEWSFAST` / `NEWARR_1_VC` is a heap
allocation, and its absence proves stack allocation happened.** For SIMD, look for `ymm`/`zmm`
registers and `vpaddd` / `vaddps`. [SharpLab](https://sharplab.io) does the same with no setup — set
output to **JIT Asm**.

### Hardware counters

When you've eliminated allocations and algorithmic problems and code is *still* slow, you're usually
fighting the memory hierarchy or the branch predictor. `[HardwareCounters(HardwareCounter.CacheMisses)]`
is **Windows + admin only**; on Linux use `perf stat -e cycles,instructions,cache-misses` with
`DOTNET_PerfMapEnabled=1` so managed frames resolve.

Rough reading: **IPC < ~1** means memory- or branch-bound, and high cache misses with low branch
misses means pointer chasing — exactly the `List<SomeClass>` vs `SomeStruct[]` difference, same big-O
and an order of magnitude apart. Beyond this, **PerfView**'s *Thread Time* view shows blocked time,
and `TraceEvent` lets you analyse traces programmatically.

## Mistakes that produce confident wrong answers

| Mistake | Why it's wrong | Do instead |
|---|---|---|
| Benchmarking a Debug build | No optimizations, no inlining — unrelated to production | `-c Release`. BDN enforces it |
| Timing with `Stopwatch` in a loop | No warm-up, no tiering settle, no statistics | BenchmarkDotNet |
| Profiling under the debugger | Disables optimizations, adds per-call overhead | `Alt+F2`, or detached CLI tools |
| Reporting averages | Hides the tail users actually feel | p50/p95/p99 + the histogram shape |
| One run, one number | You can't separate signal from noise at n=1 | Mean ± Error, `--statisticalTest` |
| Measuring on a laptop on battery | Thermal throttling, 20%+ swing | Plugged in, or a dedicated runner |
| Optimizing what the profiler shows first | The top frame is often framework code called correctly | Your frames, and Amdahl's ceiling |
| Trusting a CPU profile for a blocked-thread problem | **Blocked threads generate no CPU samples** | Timeline profiling |
| Not warming up before a load test | You measure JIT and cold caches | Discard the first 60 s |
| One implementation at a polymorphic call site | PGO devirtualizes perfectly in the benchmark, never in prod. **Benchmarks systematically flatter abstraction** | A realistic mix of types |
| Comparing numbers across machines | Hardware, OS and power profile all differ | Ratios, within one runner class |
| Treating `time-in-gc` as pause time | Background GC does most gen2 work concurrently | `dotnet.gc.pause.time` |
| Changing two things at once | You learn nothing about either | One variable per measurement |
| Not re-measuring end-to-end after the fix | The benchmark improved; the SLO may not have | Re-run the load test. **This is the step people skip** |

## Checklist

- [ ] Every performance claim has a ratio, from a Release build, on a quiet machine
- [ ] Two Means differing by less than the sum of their Errors are reported as **no difference**
- [ ] Amdahl's ceiling computed before the work started, not after
- [ ] The load test was re-run after the fix, and p99 actually moved
- [ ] Allocation rate vs a rising `gc-heap-size` floor distinguished before "fixing memory"
- [ ] Blocked-thread problems diagnosed with timeline profiling, not a CPU profile
- [ ] Load tests discard the first 60 seconds
- [ ] OpenTelemetry metrics wired up, with alerts on `threadpool.queue.length`
- [ ] `ValidateScopes` / `ValidateOnBuild` enabled outside Development too
- [ ] CI gates on allocations per op, not on time
- [ ] One variable changed per measurement

## Where this goes next

| Step | Document | What it builds on from here |
|---|---|---|
| 2 | [AsyncVsSync.md](AsyncVsSync.md) | Thread-pool counters detect blocking and starvation — and prove you fixed them |
| 3 | [ParallelismVsConcurrency.md](ParallelismVsConcurrency.md) | `[ThreadingDiagnoser]`, contention counters, and dotTrace Timeline |
| 4 | [MemoryOptimizationTricks.md](MemoryOptimizationTricks.md) | `[MemoryDiagnoser]`, allocation assertions, and the "no `CORINFO_HELP_NEW*`" check |

## References

- [Performance Improvements in .NET 10 — .NET Blog](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
- [.NET diagnostic tools overview — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/tools-overview)
- [dotnet-counters — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)
- [Dynamic adaptation to application sizes (DATAS) — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas)
- [ASP.NET Core built-in metrics — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/log-mon/metrics/built-in)
- [BenchmarkDotNet documentation](https://benchmarkdotnet.org/)
