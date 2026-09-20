# Step 1 — Ways to Measure .NET Performance

> **Baseline:** .NET 10 (LTS). `[.NET 8]` / `[.NET 9]` / `[.NET 10]` tags mark arrivals.
> **Scope:** finding where CPU, memory, and threads actually go — in production, in a profiler, and
> in a benchmark. This is the foundation; Steps 2–4 are the fixes.
> **Companion code:** [samples/PerfLab/](samples/PerfLab/) — see [samples/README.md](samples/README.md).
> **All numbers here are measured** on an i5-13420H, .NET 10.0.4, `--job short`.
> Raw output: [samples/PerfLab/BenchmarkDotNet.Artifacts/results/](samples/PerfLab/BenchmarkDotNet.Artifacts/results/).

## The one-sentence version

**Measure first, measure after, and compare ratios rather than absolute numbers.** A benchmark that
got 10× faster while p99 didn't move means your model of the system was wrong — useful information,
but not a performance improvement.

## The four numbers you read first

When someone says "production is slow", attach `dotnet-counters` and read these, in this order.
Everything else in this document is what you do *after* one of them points somewhere.

| # | Counter | What it settles |
|---|---|---|
| 1 | `cpu-usage` | Saturated, or not? (% of one core × core count — 800% on 8 cores is maxed) |
| 2 | `threadpool-queue-length` | Sustained > 0 with low CPU = **starvation**. [Step 2](AsyncVsSync.md) |
| 3 | `time-in-gc` / `alloc-rate` | GC pressure. Over 10% time-in-gc and nothing else matters |
| 4 | `working-set` vs `gc-heap-size` | Managed objects, or native/other? |

## Symptom → fix

```
Is CPU high?
├─ YES, and latency is bad   → real CPU-bound work. CPU sampling profile (§3.2)
├─ YES, but latency is fine  → you're at capacity. Scale, or cut CPU per request. Not urgent
└─ NO (CPU low, latency bad)  ← the interesting case, and the common one
    ├─ threadpool-queue-length > 0        → thread-pool starvation.  Step 2
    ├─ monitor-lock-contention-count high → lock contention.         Step 3
    ├─ time-in-gc high                    → GC pauses.               §7 + Step 4
    └─ all flat                           → waiting on I/O (DB, HTTP, disk). §8.4
```

| What you observe | What it means | Where to go |
|---|---|---|
| Latency bad, **CPU low**, queue growing | Thread-pool starvation | §8.2, then [Step 2](AsyncVsSync.md) |
| `gc-heap-size` sawtooths to a stable floor | Allocation-rate problem | §2.3, then [Step 4](MemoryOptimizationTricks.md) |
| `gc-heap-size` floor **rises** every cycle | Retention problem — a leak | §7.4. An allocation fix will not help |
| `working-set` ≫ `gc-heap-size` | Not managed objects | Native memory, loader heaps, unreturned pool rents (§7.1) |
| Bimodal latency histogram | Two code paths, or a queueing threshold | Not noise — a diagnosis (§2.1) |
| Profiler says "nothing is slow" | Blocked threads emit no CPU samples | Timeline profiling (§5.2, §6.1) |
| Benchmark improved 10×, p99 didn't move | Amdahl — you fixed 0.3% of the request | §1.2 |
| `http.client.request.time_in_queue` > 0 | You're rate-limiting *yourself* at the connection pool | §8.1 — not a slow downstream |
| Gen2 collections more than ~1/min, steady load | Promotion pressure | §2.5, [Step 4](MemoryOptimizationTricks.md) |

## 1. The measurement hierarchy

Four distinct questions, each with its own tool. Using the wrong layer is the single most common way
to waste a week.

| # | Question | Layer | Tools | Environment |
|---|---|---|---|---|
| 1 | *Is there a problem, and does anyone care?* | Observability | OpenTelemetry, `dotnet-counters`, APM | Production |
| 2 | *Where is the time or memory going?* | Profiling / tracing | `dotnet-trace`, `dotnet-gcdump`, VS/Rider | Prod-like load test |
| 3 | *Is B actually faster than A?* | Microbenchmarking | BenchmarkDotNet | Quiet dev/CI machine |
| 4 | *Did it stay fast?* | Regression gating | BDN in CI, load-test gates | CI |

- **Never start at layer 3.** A microbenchmark tells you the relative cost of two implementations. It
  tells you nothing about whether that method matters. Teams routinely make a method 40× faster and
  see zero change in p99.
- **Never stop at layer 2.** A profiler shows *where* time goes but is bad at proving a fix works —
  its own overhead and application noise swamp small deltas.
- **Layer 1 defines "done".** If the SLO is p99 < 200 ms, you're finished when production p99 is
  under 200 ms, not when the benchmark table looks impressive.

### 1.1 The loop

1. **Observe prod** → which SLO is breached?
2. **Reproduce under load** → trace/profile → a ranked list of suspects.
3. **Isolate the top suspect into a benchmark** → prove the fix in isolation, as a ratio.
4. **Re-run the load test** → did p99 move? **No → your model was wrong, go back to 2.** Yes → ship
   it and add a regression gate.

**Step 4 is the one people skip, and it is the one that catches a wrong mental model.**

### 1.2 Amdahl's law as a sanity filter

`Speedup_total = 1 / ((1 - P) + P / S)`, where `P` is the fraction of total time in the component and
`S` is how much faster you make it.

If a method is **5% of the request** and you make it **infinitely fast**, the ceiling is
**5.3% improvement**. That's the entire prize. Decide whether it's worth the change *before* you
write the code.

## 2. What to measure, and what the numbers mean

### 2.1 Latency: percentiles, never averages

An average hides the tail, and the tail is what users feel and what cascades into timeouts.

| Metric | What it tells you |
|---|---|
| p50 | The typical experience. Moves when the common path is slow |
| p95 / p99 | The tail. Moves with GC pauses, lock contention, starvation, cold cache, retries |
| p99.9 | Usually GC pauses, connection-pool exhaustion, or a noisy neighbour |
| max | Almost always an outlier (JIT warm-up, a blocking gen2, a DNS timeout). For classifying, not for SLOs |

A **bimodal histogram is a diagnosis, not noise** — two humps almost always means two code paths
(cache hit vs miss), or a queueing effect past a concurrency threshold.

### 2.2 Throughput and Little's Law

**`L = λ × W`** — concurrent requests in flight = arrival rate × average latency. The most useful
formula in this work, because it predicts capacity from two numbers you already have:

- 500 req/s at 200 ms ⇒ **100 requests in flight** at any moment.
- If each holds a pooled DB connection and your pool max is **50**, you are *structurally* unable to
  serve 500 req/s. No amount of CPU optimization fixes that.
- Inverted: if latency doubles under constant load, in-flight count doubles, which increases
  contention on every pooled resource. That's the feedback loop behind "it fell over all at once".

### 2.3 Memory: two independent problems

People say "memory problem" to mean two things that have **opposite fixes**.

| | **Allocation rate** (throughput problem) | **Retained set** (capacity problem) |
|---|---|---|
| Symptom | High CPU in GC, p99 spikes, `time-in-gc` high | Working set grows monotonically, OOM, container kill |
| Metric | `alloc-rate`, Gen0 collections/sec | Gen2 size, `gc-heap-size` after a full GC |
| Cause | Garbage per operation — LINQ, string concat, boxing, closures | Caches without eviction, statics, event-handler leaks, undisposed resources |
| Tool | `dotnet-counters`, BDN `[MemoryDiagnoser]` | `dotnet-gcdump` diffs, `!gcroot` |
| Fix | [Step 4](MemoryOptimizationTricks.md) | Ownership and lifetime — **an allocation fix will not help** |

**The 30-second diagnostic:** watch `gc-heap-size` across several gen2 collections. Sawtooths back
to a stable floor ⇒ allocation rate. Floor rises every cycle ⇒ retention.

### 2.4 Thread-pool health

For an ASP.NET Core service these three matter as much as CPU:

| Counter | Healthy | Meaning when unhealthy |
|---|---|---|
| `threadpool-queue-length` | ~0, transient spikes only | Work items waiting for a thread. Sustained > 0 = **starvation** |
| `threadpool-thread-count` | Stable near core count | Climbing steadily = the pool injecting threads (~1 per 0.5–1 s) to compensate for blocked ones |
| `monitor-lock-contention-count` | Low and flat | A lock is serializing your service |

Thread-pool starvation is the classic .NET production incident: latency through the roof while CPU
sits at 20%. [Steps 2](AsyncVsSync.md) and [3](ParallelismVsConcurrency.md) deal with the causes;
this document proves it's happening.

### 2.5 GC pause time

`dotnet.gc.pause.time` `[.NET 9]` or `time-in-gc`. For a web service: **< 5%** fine, **5–10%** worth
a look, **> 10%** allocation rate is your top problem and nothing else you do will matter as much.

`time-in-gc` is **not** pause time — with background GC much of gen2 work is concurrent. Prefer
`dotnet.gc.pause.time`, which measures actual suspension, or read
`GC.GetGCMemoryInfo().PauseDurations` on a timer.

## 3. Layer 1 — `dotnet-*` CLI diagnostics

These work where you actually have the problem: in a container, with no GUI, on a process you must
not restart. Learn them before anything else.

```bash
dotnet tool install -g dotnet-counters    # live metrics — start here
dotnet tool install -g dotnet-trace       # CPU/event tracing (EventPipe)
dotnet tool install -g dotnet-dump        # full dumps + SOS
dotnet tool install -g dotnet-gcdump      # managed heap graph, no full dump
dotnet tool install -g dotnet-stack       # instant stack snapshot
dotnet tool install -g dotnet-monitor     # HTTP-exposed diagnostics for k8s sidecars
```

**Do not bake these into the app image.** Run them from a sidecar sharing the process namespace, or
use `dotnet-monitor` as a sidecar. `[.NET 10]` the tools can now attach across PID namespaces, which
is why older guides telling you to bake them in are out of date.

### 3.1 `dotnet-counters` — the first thing you run

```bash
dotnet-counters ps                       # list attachable processes
dotnet-counters monitor -p <pid> --refresh-interval 1 \
  System.Runtime Microsoft.AspNetCore.Hosting \
  Microsoft.AspNetCore.Server.Kestrel System.Net.Http

dotnet-counters collect -p <pid> --format csv -o counters.csv --duration 00:02:00
dotnet-counters monitor --counters System.Runtime -- dotnet run -c Release   # capture startup
```

| `System.Runtime` counter | `[.NET 8]` Meter equivalent | Reads as |
|---|---|---|
| `cpu-usage` | `dotnet.process.cpu.time` | % of one core × core count |
| `working-set` | — | RSS in MB. What the container limit compares against |
| `gc-heap-size` | — | Managed heap. Compare to working-set for native overhead |
| `alloc-rate` | `dotnet.gc.heap.total_allocated_bytes` | **The single best allocation-pressure number** |
| `gen-0/1/2-gc-count` | `dotnet.gc.collections` | Gen2 > ~1/min on a steady service is a smell |
| `time-in-gc` | `dotnet.gc.pause.time` | The Meter one is **true suspension**; the counter isn't |
| `gc-fragmentation` | `dotnet.gc.last_collection.heap.fragmentation.size` | High + high working set = fragmentation/pinning |
| `loh-size` / `poh-size` | — | LOH growth = arrays ≥ 85,000 bytes |
| `threadpool-queue-length` | `dotnet.thread_pool.queue.length` | **Starvation detector** |
| `monitor-lock-contention-count` | `dotnet.monitor.lock_contentions` | Lock contention rate |
| `exception-count` | `dotnet.exceptions` | Thousands/sec is a real cost *and* usually a logic bug |
| `il-bytes-jitted`, `time-in-jit` | — | Should flatline after warm-up. If not, something emits dynamic code per request |

The Meter instruments are the OpenTelemetry-native path and what belongs on production dashboards
(§8.1). `dotnet-counters` reads both kinds on one command line.

### 3.2 `dotnet-trace` — where the CPU actually went

```bash
dotnet-trace collect -p <pid> --profile cpu-sampling --duration 00:00:30 \
  --format speedscope -o cpu.speedscope.json
```

Open at <https://speedscope.app> (entirely client-side — no upload). Use **"Left Heavy"** first: it
merges identical stacks so the widest bar is genuinely your biggest cost. The default "Time Order"
view is for spotting phases, not finding hot code.

| Profile | Use for |
|---|---|
| `cpu-sampling` | "Why is CPU at 100%?" — **start here.** ~1 kHz sampling, a few percent overhead |
| `gc-verbose` | Which types/call-sites allocate. **Heavy** — distorts allocation-heavy workloads |
| `gc-collect` | Low-overhead GC-behaviour-only trace. Prefer this unless you need call-sites |
| `database` | ADO.NET + EF Core events. N+1 detection — see §8.4 |

### 3.3 `dotnet-gcdump` — heap composition without a full dump

```bash
dotnet-gcdump collect -p <pid> -o before.gcdump
# ... apply load / wait for growth ...
dotnet-gcdump collect -p <pid> -o after.gcdump
```

A `.gcdump` has the **object graph but no field values**, which makes it safe to take off a
production box holding customer data — an important practical difference from a full dump, and it's
megabytes where a dump is gigabytes. Open both in Visual Studio, compare to baseline, sort by **Size
Diff**. The type that grew is the *symptom*; the retention path is the bug (§7.4).

> Taking a gcdump forces a **blocking gen2 GC**. Don't do it on every instance at once.

### 3.4 `dotnet-stack` — the 2-second triage

```bash
dotnet-stack report -p <pid>
```

When someone says "prod is hung", run it twice five seconds apart and diff. Identical stacks ⇒
deadlock or blocked pool. Churning through the same method ⇒ hot loop.

### 3.5 `dotnet-dump` + SOS — the last resort that always works

```bash
dotnet-dump collect -p <pid> --type Heap     # managed heap only, much smaller
dotnet-dump analyze core_20260913_121500
```

```
dumpasync --stats             # which async method has thousands of pending instances
clrstack -all                 # managed stacks for every thread
dumpheap -stat                # every type, count, total size
gcroot <addr>                 # WHY is this object alive — the money command
objsize <addr>                # retained size of an object graph
syncblk                       # who holds which monitor — deadlock/contention
threadpool                    # queue depths, worker/IOCP counts
analyzeoom                    # post-OOM: which allocation failed and why
```

**`dumpasync` deserves special mention.** In an async service `clrstack` shows a pool thread doing
nothing useful, because the interesting state is in heap-allocated state machines, not on stacks.
`dumpasync --stats` on a hung service usually names the culprit in one line — 12,000 pending
`SendAsync` state machines means you have exhausted an `HttpClient` connection pool.

### 3.6 `dotnet-monitor` for Kubernetes

Run it as a sidecar; it exposes `/metrics`, `/livemetrics`, `/trace`, `/dump`, `/gcdump` and
`/stacks` over HTTP. Its real value is **collection rules** — trigger an automatic dump or trace when
a condition fires, so you capture the incident instead of trying to reproduce it.
[Configure them here](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-monitor-collection-rules);
always set the `Limits` block, or a flapping condition will fill your blob storage and add load to an
already-struggling process.

## 4. Layer 2 — BenchmarkDotNet

The only credible way to compare two implementations in .NET. It handles warm-up, tiered-compilation
settling, statistics, outlier detection, and process isolation — all things hand-rolled `Stopwatch`
loops get wrong. **Current version: 0.15.8**; the project setup is in
[samples/PerfLab/PerfLab.csproj](samples/PerfLab/PerfLab.csproj).

### 4.1 A benchmark that answers a real question

```csharp
[MemoryDiagnoser]                       // bytes allocated per op — use always
[ThreadingDiagnoser]                    // lock contentions per op
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

**Quote ratios, never absolute nanoseconds** — those are machine-specific and meaningless across
hardware.

### 4.2 Reading the summary table

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

Four readings, and three of them are about *not* over-reading:

1. **At N=1000 `StringConcat` is 25× slower and allocates 182× more.** O(n²) copying. This is the
   class of bug worth finding — not a 3% win.
2. **At N=10 it is only 1.7× slower, and beats nothing.** The same code, the same bug, effectively
   invisible. **This is why `[Params]` exists** — a single input size would have hidden the entire
   finding.
3. **The two `StringBuilder` rows are indistinguishable.** Means differ by 540 ns while one Error
   alone is 15,054 ns — 41% of its own Mean, which is itself a red flag on that measurement.
   **This benchmark has not shown a difference**; avoiding the interpolated string bought nothing,
   and only measurement told you that.
4. **`StringCreate` allocates 29% less and runs 52% slower.** "Fewer bytes" and "less time" are
   different claims in different columns, and neither implies the other.
   [Step 4 follows this one all the way down](MemoryOptimizationTricks.md#when-fewer-allocations-made-it-slower)
   — the obvious fix doesn't work either, for a more interesting reason.

| Column | How to read it |
|---|---|
| **Error** | Half the 99.9% CI. **If two Means differ by less than the sum of their Errors, you have not shown a difference** |
| **StdDev** | Run-to-run variability. > ~10% of Mean = noisy machine or unstable state. Fix that before believing anything |
| **Ratio / RatioSD** | vs baseline. Large RatioSD ⇒ the comparison itself is unstable |
| **Gen0/1/2** | Collections **per 1000 operations**, not per op. A dash means < 1 per 1000, not zero |
| **Allocated** | **Managed heap** bytes per op. `stackalloc` doesn't appear — which is why a 0 B row proves a no-allocation rewrite worked |

Always read the **legend and warnings** BDN prints below the table — multimodal distributions,
removed outliers, and benchmarks too fast to measure reliably.

### 4.3 Comparing runtimes — "should we upgrade?"

The highest-value BDN feature for this purpose. One class, three runtimes, one table:

```csharp
[SimpleJob(RuntimeMoniker.Net80, baseline: true)]
[SimpleJob(RuntimeMoniker.Net90)]
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
public class RuntimeComparison { /* ... */ }
```

Requires `<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>`. BDN builds and runs a separate
process per runtime. **This is how you make an evidence-based upgrade case instead of citing a blog
post.** Expect the largest .NET 10 wins on array/`IEnumerable` iteration (devirtualization), small
short-lived arrays and delegates (stack allocation), struct-heavy call chains, and Arm64 (write
barriers — 8–20% GC pause improvements).

### 4.4 Diagnosers worth knowing

| Attribute | Adds | Cost / caveat |
|---|---|---|
| `[MemoryDiagnoser]` | Allocated bytes, Gen0/1/2 | Nearly free. Use always |
| `[ThreadingDiagnoser]` | Lock contentions, completed work items | Essential for [Steps 2](AsyncVsSync.md) and [3](ParallelismVsConcurrency.md) |
| `[ExceptionDiagnoser]` | Exceptions/op | Finds accidental exception-driven control flow |
| `[DisassemblyDiagnoser(maxDepth: 3)]` | The asm actually emitted | Slow; ground truth for "did the JIT vectorize it?" (§9.1) |
| `[EventPipeProfiler(EventPipeProfile.CpuSampling)]` | A `.nettrace` per benchmark | Cross-platform; open in speedscope |
| `[HardwareCounters(...)]` | CPU counters | **Windows + admin only.** See §9.2 |

### 4.5 Controlling the environment, and gating CI

```bash
dotnet run -c Release -f net10.0 --project samples/PerfLab -- bench \
  --filter '*' --runtimes net8.0 net10.0 --memory --threading --statisticalTest 5%
```

`--statisticalTest` runs a Mann–Whitney U test and labels each result **Faster / Slower / Same**
against the baseline. **This is what you gate CI on, not a raw ns comparison.**

**Gate on allocations, not on time.** Allocated-bytes-per-op is deterministic and reproducible across
machines; time is not. A rule of "no benchmark may allocate more than its baseline + 5%" catches the
vast majority of real regressions with almost no flakiness.

### 4.6 Pitfalls that produce fake results

| Pitfall | The fake result | Do instead |
|---|---|---|
| Not returning the value | Dead-code elimination deletes the work; you measure nothing | Return it, or use `Consumer.Consume(x)` |
| Inputs from `readonly` / `[Params]` constants | Constant-folded at JIT time | Non-readonly instance fields set in `[GlobalSetup]` |
| Work in `[IterationSetup]` | Setup noise in the measurement | `[GlobalSetup]`, or `[IterationSetup]` + `[InvocationCount(1)]` |
| Shared mutable state between benchmarks | One benchmark fills a dictionary; the next looks fast | Reset in `[IterationCleanup]`, or separate instances |
| Comparing *startup* without disabling tiering | You measure tier-1 code for a tier-0 scenario | Disable tiered compilation explicitly |
| A busy machine, or a laptop on battery | 20%+ swing from throttling | Plugged in, dedicated runner. **Believe BDN's warning** |
| A benchmark too short to pay its own GC | The garbage lands on the *next* benchmark's time column | `[GcForce(true)]` and compare |

## 5. Layer 3 — Visual Studio & Rider profilers

The GUI tools win on two things: allocation call-tree attribution, and handing a screenshot to
someone who won't read a flamegraph.

### 5.1 Visual Studio

**Performance Profiler** (`Alt+F2`) works on Release without the debugger — this is the one to use.
Never trust absolute numbers from a debug session; the debugger disables JIT optimizations.

| Tool | Use for |
|---|---|
| **.NET Object Allocation Tracking** | **The best allocation tool in the ecosystem.** Every allocation with its call stack, plus a "Collections" view showing what survived each GC. Heavy — use short runs |
| **CPU Usage** | Sampling profiler |
| **Database** | EF Core / ADO.NET queries with durations and stacks. Catches N+1 instantly |
| **Async Tool** | Async durations and waits on a timeline. Underused, excellent for [Step 2](AsyncVsSync.md) |
| **Instrumentation** | Exact call counts. **Caution:** it rewrites the binary and inflates small methods enormously, making cheap-but-frequent methods look like bottlenecks |

For allocation hunting, sort by **count** before bytes — huge counts of tiny objects (`string`,
`object[]`, closure display classes, boxed `int`) point at a hot loop. Then switch to bytes for the
LOH offenders, and click through to **Backtraces**.

### 5.2 JetBrains Rider / dotTrace / dotMemory

| Tool | Mode | Notes |
|---|---|---|
| **dotTrace** | *Sampling* | Default. Low overhead, accurate for wall-clock |
| | *Tracing* / *Line-by-line* | Exact counts / per-line timings. Heavy; distorts small methods |
| | *Timeline* | **The one that matters for async and threading.** Per-thread states over time: Running, Waiting, GC Wait, Blocked, I/O |
| **dotMemory** | Snapshots + diff | Retention paths and dominators. Best at "what holds this alive" |

**dotTrace Timeline is the single best tool for [Steps 2](AsyncVsSync.md) and
[3](ParallelismVsConcurrency.md).** When a request takes 900 ms and the CPU profiler shows 40 ms of
CPU, Timeline shows you the 860 ms of `Waiting` and what it was waiting on.

### 5.3 GUI or CLI?

| Situation | Tool |
|---|---|
| Prod container, no restart possible | `dotnet-*` CLI / `dotnet-monitor` |
| "Which line allocates this?" on your dev box | VS Allocation Tracking / dotMemory |
| "Slow but not CPU-bound?" | dotTrace Timeline / VS Async Tool |
| "Is B faster than A?" | BenchmarkDotNet, always |
| Cross-platform, scriptable, CI | `dotnet-trace` + speedscope, BDN |

## 6. CPU analysis

### 6.1 Sampling vs instrumentation

Sampling interrupts threads at ~1 kHz and records stacks: ~1–5% overhead, answers "where is the
time?", blind to methods faster than the sample interval. Instrumentation injects probes: exact call
counts, 10×+ overhead on small methods, distorts what it measures.

**The crucial subtlety: CPU sampling does not show blocked time.** A thread blocked on `Task.Result`
consumes no CPU, produces no samples, and is invisible in a flamegraph — while being the actual cause
of your latency. **This is the #1 reason people conclude "the profiler says nothing is slow."** Use
wall-clock/timeline profiling for those.

### 6.2 Reading a flamegraph

- **Width = total time.** Height is just stack depth and means nothing.
- Use **Left Heavy / merged** ordering to find aggregate costs.
- A wide plateau of framework code isn't automatically a problem — look one frame up for *your* code
  calling it in a loop.
- Usual suspects: unexpected recursion, and unexpectedly wide `Regex`, `String.Format`,
  `Enum.ToString`, `Activator.CreateInstance`, reflection, and LINQ chains.
- Wide `CORINFO_HELP_*` / `JIT_New` frames = allocation cost surfacing as CPU.

### 6.3 Warm up, and know that benchmarks flatter abstraction

.NET compiles in tiers (tier 0 on first call, tier 1 after ~30 calls, with OSR swapping long-running
loops mid-execution). Two consequences that change what you measure:

- **Always warm up before a load test**, or your results describe the JIT rather than your code.
  Short-lived processes never reach tier 1 at all.
- **Dynamic PGO makes benchmarks systematically flatter abstraction.** In a benchmark only one
  implementation is ever seen at a call site, so PGO devirtualizes perfectly. In production with five
  implementations the guarded check fails and you pay the virtual call. If it matters, benchmark with
  a realistic mix of types.

For startup specifically, watch `il-bytes-jitted` and `time-in-jit`; the levers are **ReadyToRun**
(`<PublishReadyToRun>`) and **NativeAOT** (`<PublishAot>`).

## 7. Memory analysis

### 7.1 The layout you're reasoning about

- **SOH** — objects < 85,000 bytes, in **gen0** (allocation, collected constantly, cheap), **gen1**
  (a buffer generation), **gen2** (long-lived, expensive to collect).
- **LOH** — arrays/objects ≥ 85,000 bytes. Collected with gen2, **not compacted by default**.
- **POH** `[.NET 5]` — `GC.AllocateArray(pinned: true)`, keeps pinned buffers from fragmenting the SOH.
- **Native heap, loader heaps, and 1 MB-per-thread stacks** are outside all of that.

**`working-set` ≫ `gc-heap-size` means your problem is not managed objects.** Look at native memory:
unmanaged libraries, gRPC/HTTP buffers, loader-heap growth from runtime-generated generics, or an
unreturned `ArrayPool` rent.

**The generational hypothesis** the whole design rests on: most objects die young. Your job as an
optimizer is to keep it true — which is why "allocate less" and "allocate *shorter-lived*" are
different goals, and why a cache with a bad eviction policy can be worse than no cache.

### 7.2 GC modes — what you're actually running

| Setting | Default | Effect |
|---|---|---|
| `ServerGarbageCollection` | **true** for ASP.NET Core | One heap + one GC thread **per core**. More throughput, more memory, longer individual pauses |
| `ConcurrentGarbageCollection` | true | Background GC — gen2 runs mostly concurrently; two short suspensions, not one long one |
| `GCDynamicAdaptationMode` (DATAS) | **1 (on)** since `[.NET 9]` for Server GC | Tunes heap count and committed memory to actual load |
| `GCHeapHardLimit` | 75% of container limit | The GC respects cgroup limits automatically |

**DATAS is the biggest recent change in .NET memory behaviour, and it will surprise you when reading
metrics.** Pre-.NET 9, Server GC grew the heap and never gave it back — a service that peaked at 4 GB
stayed at 4 GB. Now heap size tracks live-data size, memory returns to the OS when load drops, and
the same workload uses similar memory on a 4-core and a 64-core box. **Trade-off:** slightly lower
peak throughput on some workloads. If you migrated to .NET 9/10 and saw a throughput dip alongside a
large memory drop, this is why.

### 7.3 Measuring allocation rate

In order of cheapness: `dotnet-counters` → `alloc-rate`; BDN `[MemoryDiagnoser]` for exact bytes per
op; `dotnet-trace --profile gc-verbose` for call-sites (sampled every ~100 KB, so statistical); VS
Allocation Tracking for every allocation with a stack.

In-process, with no tooling at all:

```csharp
long before = GC.GetAllocatedBytesForCurrentThread();
DoWork();
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
```

Cheap, precise, and deterministic — which makes it excellent as a **CI assertion**. See
[Step 4](MemoryOptimizationTricks.md).

### 7.4 Detecting a leak

**A leak is a retained set that grows without bound under steady-state load.**

1. Put the service under constant, representative load.
2. Wait for steady state — caches warm, pools sized. Typically 5–10 minutes.
3. Record `gc-heap-size` immediately after each gen2. **Rising floor = leak.**
4. `dotnet-gcdump collect` → wait 10 min → collect again.
5. Diff in Visual Studio; note the largest **count** growth.
6. `dotnet-dump` → `dumpheap -stat` → **`gcroot <addr>`** on an instance of the growing type.

**Step 6 is what actually finds the bug.** Read the retention chain top-down — a strong handle
(usually a static) holds a cache, which holds a dictionary, which holds your DTO. *The leak is the
cache, not the DTO.*

| Root shown by `gcroot` | Actual cause |
|---|---|
| A static field | A static `Dictionary`/`List` cache with no eviction |
| An event's invocation list | Subscriber never unsubscribed (`+=` without `-=`) |
| A `Timer` / `CancellationTokenSource` | Callback keeps the target alive; CTS registrations not disposed |
| An async state machine | A never-completing `Task` pinning its closure |
| `ThreadStatic` / `AsyncLocal` | Per-request state escaping the request |
| A DI container | A `Scoped` service captured by a `Singleton` — **the most common ASP.NET Core leak** |

That last one has a guard worth turning on in every project:

```csharp
builder.Host.UseDefaultServiceProvider((ctx, opts) =>
{
    opts.ValidateScopes = true;      // catch scoped-captured-by-singleton
    opts.ValidateOnBuild = true;     // fail fast at startup, not on first request
});
```

Both default to `true` in Development only — **enable them in Staging load tests too.**

### 7.5 Fragmentation and the LOH

Symptoms: `gc-fragmentation` high and working set high, but live data small.

- **Pinning** — `fixed`, `GCHandle.Alloc(..., Pinned)`, interop and socket code. Fix by allocating
  pinned buffers on the POH: `GC.AllocateArray<byte>(4096, pinned: true)`.
- **LOH churn** — arrays ≥ 85,000 bytes, and the LOH isn't compacted by default. Pool them
  ([Step 4](MemoryOptimizationTricks.md)), or as a last resort set
  `GCSettings.LargeObjectHeapCompactionMode = CompactOnce` and force a collect. Expensive and
  blocking — maintenance windows only.

## 8. ASP.NET Core specifics

### 8.1 Built-in metrics — wire these up before anything else

`[.NET 8]` A ten-line change that gives you permanent layer-1 observability:

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
| `kestrel.queued_connections` / `queued_requests` | Accepted but not yet served — **saturation, before latency shows it** |
| `http.client.request.time_in_queue` | **Waiting for a pooled `HttpClient` connection** |
| `aspnetcore.routing.match_attempts` | Routing cost with large route tables |

**`http.client.request.time_in_queue` is one of the most valuable and least-known metrics in .NET:**
it distinguishes "the downstream service is slow" from "we're rate-limiting ourselves at the
connection pool" — which look identical in end-to-end latency.

For code you own, a `Meter` is cheap, purpose-built, and near-zero cost when nothing listens — and it
then shows up in `dotnet-counters -p <pid> MyApp.Orders`, Prometheus, and your APM with no further
work:

```csharp
private static readonly Meter Meter = new("MyApp.Orders", "1.0.0");
public static readonly Histogram<double> PricingDuration =
    Meter.CreateHistogram<double>("myapp.orders.pricing.duration", "ms");
```

[samples/PerfLab/Api/AppMetrics.cs](samples/PerfLab/Api/AppMetrics.cs) is a working example.

### 8.2 Detecting thread-pool starvation in production

Three independent signals — **you want at least two agreeing**:

1. `threadpool-queue-length` sustained > 0 while CPU < 50%.
2. `threadpool-thread-count` climbing slowly (hill-climbing injects ~1 thread per 0.5–1 s when the
   queue doesn't drain).
3. `dotnet-stack report` shows many threads parked in `Monitor.Wait`, `Task.Result`,
   `.GetAwaiter().GetResult()`, `Thread.Sleep`, or a synchronous I/O frame.

```bash
dotnet-trace collect -p <pid> --duration 00:00:30 \
  --providers Microsoft-DotNETCore-SampleProfiler,System.Threading.Tasks.TplEventSource:0x1FF:4
```

**Raising `ThreadPool.SetMinThreads` is a symptom mask, not a fix.** It buys time while you remove the
blocking call — [Step 2](AsyncVsSync.md) covers the removal.

### 8.3 Load generation

You cannot measure a web application without load.

| Tool | Good for |
|---|---|
| **bombardier** / **hey** | 30-second smoke tests, single endpoint |
| **k6** | Scripted scenarios, ramping, thresholds-as-code, CI gates |
| **NBomber** | Same in C#, so the load test shares DTOs and auth with the app |
| **Crank** | What the ASP.NET team uses; reproducible, dedicated hardware, counters alongside throughput |

Whatever you use: **ramp up, hold steady, and discard the first 60 seconds.** The warm-up contains
JIT, connection-pool fill, and cache population — real effects, but not the steady state your SLOs
describe. Measure those separately if you care about deploys and cold starts.

### 8.4 When the app is innocent

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
  exhaustion — see `time_in_queue` in §8.1.

## 9. Advanced

Two stubs, deliberately. Both are worth knowing exist and rarely worth reaching for.

### 9.1 Looking at the generated assembly

Ground truth for "did the JIT vectorize it?" and "did that object really get stack-allocated?":

```bash
dotnet run -c Release -f net10.0 --project samples/PerfLab -- bench \
  --filter '*SimdSum*' --disasm
```

The one thing worth grepping for: **`call CORINFO_HELP_NEWSFAST` / `NEWARR_1_VC` is a heap
allocation, and its absence proves stack allocation happened.** For SIMD, look for `ymm`/`zmm`
registers and `vpaddd` / `vaddps`. [SharpLab](https://sharplab.io) does the same thing with no setup —
set output to **JIT Asm**.

### 9.2 Hardware performance counters

When you've eliminated allocations and algorithmic problems and code is *still* slow, you're usually
fighting the memory hierarchy or the branch predictor.

```csharp
[HardwareCounters(HardwareCounter.CacheMisses, HardwareCounter.BranchMispredictions)]
```

**Windows + administrator only** (it uses ETW kernel providers). On Linux use
`perf stat -e cycles,instructions,cache-misses,branch-misses` with `DOTNET_PerfMapEnabled=1` so
managed frames resolve. Rough reading: **IPC < ~1** means memory- or branch-bound; high cache misses
with low branch misses means pointer chasing and poor locality — which is exactly the
`List<SomeClass>` vs `SomeStruct[]` difference, same big-O and an order of magnitude apart.

For deeper work than this, **PerfView**'s *Thread Time* view is the one that shows blocked time, and
`Microsoft.Diagnostics.Tracing.TraceEvent` lets you analyse traces programmatically.

## 10. Measurement mistakes that produce confident wrong answers

| Mistake | Why it's wrong | Do instead |
|---|---|---|
| Benchmarking a Debug build | No optimizations, no inlining — unrelated to production | `-c Release`. BDN enforces it |
| Timing with `Stopwatch` in a loop | No warm-up, no tiering settle, no statistics, no outlier handling | BenchmarkDotNet |
| Profiling under the debugger | Disables optimizations, adds per-call overhead | `Alt+F2`, or detached CLI tools |
| Reporting averages | Hides the tail users actually feel | p50/p95/p99 + the histogram shape |
| Measuring on a laptop on battery | Thermal/power throttling, 20%+ swing | Plugged in, or a dedicated runner |
| One run, one number | You can't separate signal from noise at n=1 | Mean ± Error, `--statisticalTest` |
| Optimizing what the profiler shows first | The top frame is often framework code called correctly | Your frames, and Amdahl's ceiling (§1.2) |
| Trusting a CPU profile for a blocked-thread problem | **Blocked threads generate no CPU samples** | Timeline profiling (§5.2, §6.1) |
| Not warming up before a load test | You measure JIT and cold caches | Discard the first 60 s |
| One implementation at a polymorphic call site | PGO devirtualizes perfectly in the benchmark, never in prod | A realistic mix of types |
| Comparing numbers across machines | Hardware, OS, power profile all differ | Ratios, within one runner class |
| Treating `time-in-gc` as pause time | Background GC does most gen2 work concurrently | `dotnet.gc.pause.time` |
| Changing two things at once | You learn nothing about either | One variable per measurement |
| Not re-measuring end-to-end after the fix | The benchmark improved; the SLO may not have | Close the loop (§1.1) |

## 11. Runbook

### 11.1 "Production is slow" — first 10 minutes

```bash
dotnet-counters ps
dotnet-counters monitor -p <pid> --refresh-interval 1 \
  System.Runtime Microsoft.AspNetCore.Hosting
```

Read the four numbers at the top of this document, then branch per the decision tree above.

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
# VS: open t1, compare to baseline t0, sort by size diff. Then:
dotnet-dump collect -p <pid> && dotnet-dump analyze <file>
#   > dumpheap -stat ; gcroot <addr>
```

### 11.4 "It's hung"

```bash
dotnet-stack report -p <pid> > s1.txt
sleep 5
dotnet-stack report -p <pid> > s2.txt
diff s1.txt s2.txt          # identical → deadlock / blocked pool
dotnet-dump collect -p <pid> && dotnet-dump analyze <file>
#   > dumpasync --stats ; syncblk ; threadpool
```

### 11.5 "Is B faster than A?" / "Should we upgrade?"

```bash
dotnet run -c Release -f net10.0 --project samples/PerfLab -- bench \
  --filter '*MyThing*' --memory --statisticalTest 5%

dotnet run -c Release -f net10.0 --project samples/PerfLab -- bench \
  --filter '*' --runtimes net8.0 net10.0 --memory
```

### 11.6 Minimum permanent instrumentation for any service

- OpenTelemetry metrics: ASP.NET Core + HttpClient + Runtime instrumentation (§8.1)
- Dashboards: p50/p95/p99 by route, `active_requests`, `threadpool.queue.length`, `gc.pause.time`,
  allocation rate, `working_set`
- Alerts: p99 SLO breach, `threadpool.queue.length > 0` sustained, gen2 rate, working-set trend
- `dotnet-monitor` sidecar with collection rules for auto-capture (§3.6)
- A BDN suite in CI gating on **allocations per op** (§4.5)

## Checklist

- [ ] Every performance claim has a ratio, from a Release build, on a quiet machine
- [ ] Two Means differing by less than the sum of their Errors are reported as **no difference**
- [ ] Amdahl's ceiling computed before the work started, not after
- [ ] The load test was re-run after the fix, and p99 actually moved
- [ ] `alloc-rate` vs rising `gc-heap-size` floor distinguished before "fixing memory"
- [ ] Blocked-thread problems diagnosed with timeline profiling, not a CPU profile
- [ ] Load tests discard the first 60 seconds
- [ ] OpenTelemetry metrics wired up, with alerts on `threadpool.queue.length`
- [ ] `ValidateScopes` / `ValidateOnBuild` enabled outside Development too
- [ ] CI gates on allocations per op, not on time
- [ ] One variable changed per measurement

## Where this goes next

| Step | Document | What it builds on from here |
|---|---|---|
| 2 | [AsyncVsSync.md](AsyncVsSync.md) | The thread-pool counters in §2.4 and §8.2 detect blocking and starvation — and prove you fixed them |
| 3 | [ParallelismVsConcurrency.md](ParallelismVsConcurrency.md) | `[ThreadingDiagnoser]`, contention counters, and dotTrace Timeline are the basis for parallel work |
| 4 | [MemoryOptimizationTricks.md](MemoryOptimizationTricks.md) | `[MemoryDiagnoser]`, allocation assertions, and §9.1's "no `CORINFO_HELP_NEW*`" check verify no-allocation code |

## References

- [Performance Improvements in .NET 10 — .NET Blog](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
- [What's new in the .NET 10 runtime — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime)
- [.NET diagnostic tools overview — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/tools-overview)
- [dotnet-counters — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)
- [Dynamic adaptation to application sizes (DATAS) — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas)
- [ASP.NET Core built-in metrics — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/log-mon/metrics/built-in)
- [BenchmarkDotNet documentation](https://benchmarkdotnet.org/)
