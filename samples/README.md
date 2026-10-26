# PerfLab — companion code

Runnable code for the four documents in this repo. Everything here exists to be *measured*, not to
be copied into production.

```
samples/
├── PerfLab.sln
├── Directory.Build.props
└── PerfLab/
    ├── PerfLab.csproj          net8.0;net9.0;net10.0
    ├── Program.cs              dispatcher: "api" | "bench"
    ├── Api/                    ASP.NET Core lab — net10.0 only (see below)
    │   ├── ApiHost.cs          host + OpenTelemetry wiring
    │   ├── AppMetrics.cs       custom Meter (Step 1 — permanent instrumentation)
    │   └── Step1..4Endpoints.cs
    └── Benchmarks/
        └── Step1/ Step2/ Step3/ Step4/
```

**One project, two entry points.** `Program.cs` dispatches on the first argument, so there is one
csproj to build and one command shape to remember.

**Prerequisites:** .NET 10 SDK. The .NET 8 and 9 *runtimes* are also needed for the cross-runtime
comparisons (`Step1.RuntimeComparison`, `Step4.StackAllocationPatterns`); everything else runs on
.NET 10 alone.

```bash
dotnet build samples/PerfLab.sln -c Release
```

> **Why the API is net10.0-only.** The benchmarks multi-target so the "should we upgrade?" tables
> have something to compare, but ASP.NET Core code that compiles on net8.0 can't use net10 APIs. So
> the project uses the plain console SDK with a `FrameworkReference` to ASP.NET Core conditioned on
> net10.0, and `<Compile Remove="Api/**" />` for the others. That keeps `#if` out of the shared
> benchmark code entirely.

---

## Benchmarks

Always `-c Release` (BenchmarkDotNet refuses Debug builds, and it should) and always host on
`net10.0`.

```bash
cd samples/PerfLab

dotnet run -c Release -f net10.0 -- bench --list flat        # what's available
dotnet run -c Release -f net10.0 -- bench --filter '*CsvFormatting*'
dotnet run -c Release -f net10.0 -- --filter '*Span*'        # 'bench' is optional
```

| Step | Class | Shows |
|---|---|---|
| 1 | `CsvFormatting` | How to read a BDN summary table. Includes a result where the low-allocation option is **slower**. |
| 1 | `MeasurementPitfalls` | Dead-code elimination, constant folding, lazy LINQ — fake numbers beside real ones. |
| 1 | `AllocationAccounting` | Boxing / closures / spans, plus a `GC.GetAllocatedBytesForCurrentThread()` readout. |
| 1 | `RuntimeComparison` | Same code on .NET 8 / 9 / 10. Three patterns go to **0 B** on .NET 10. |
| 2 | `AsyncOverhead` | Async that never suspends vs suspends vs deep chains. Three different cost profiles. |
| 2 | `ValueTaskVsTask` | Swept by synchronous-completion rate. At 100% `ValueTask` wins hugely; at 0% it **loses**. |
| 2 | `AsyncAllocation` | State-machine box, `Task` allocation, `ExecutionContext` capture — three separate sources. |
| 2 | `SequentialVsWhenAll` | Overlapping independent I/O. |
| 2 | `ElidedAsync` | `return Inner()` vs `return await Inner()` — and prints proof they aren't interchangeable. |
| 3 | `FanOutStrategies` | Sequential · `WhenAll` · `ForEachAsync` · `SemaphoreSlim` · `Channel`. |
| 3 | `ParallelCrossover` | `for` vs `Parallel.For` swept by work-per-item — find your crossover. |
| 3 | `LockPrimitives` | Six primitives, uncontended and contended, with `[ThreadingDiagnoser]`. |
| 3 | `FalseSharing` | Independent threads that serialise anyway. Padding vs not sharing at all. |
| 3 | `ConcurrentCollections` | Read-mostly lookup, plus printed proof that `GetOrAdd` is not atomic. |
| 3 | `PlinqVsLinq` | PLINQ, `AsOrdered` cost, and a hand-written loop as the control. |
| 4 | `CsvFormattingFixed` | **Closes the loop on Step 1's slow `StringCreate`.** |
| 4 | `BufferStrategies` | `new` vs `stackalloc` vs `ArrayPool`, swept across the LOH threshold. |
| 4 | `StringOperations` | Split, slice, `SearchValues`, `string.Create`. |
| 4 | `HiddenAllocations` | Boxing, closures, `Enum.ToString`, `params` arrays, LINQ. |
| 4 | `SimdSum` | Scalar · `Vector<T>` · `Vector256<T>` · `TensorPrimitives`. |
| 4 | `StackAllocationPatterns` | Shapes the .NET 10 JIT can stack-allocate, vs ones that escape. |

### Useful invocations

```bash
# Fast, low-precision pass while iterating
dotnet run -c Release -f net10.0 -- bench --filter '*' --job short

# Statistical verdict (Faster/Slower/Same) — what CI should gate on
dotnet run -c Release -f net10.0 -- bench --filter '*CsvFormatting*' --statisticalTest 5%

# Did the allocation actually disappear?  (Step 1 — generated assembly / Step 4 "What .NET 10 does for free")
dotnet run -c Release -f net10.0 -- bench --filter '*StackAllocationPatterns*' --disasm
#   → a stack-allocated shape has NO CORINFO_HELP_NEWSFAST / NEWARR_1_VC call

# A CPU trace per benchmark, openable in speedscope.app or PerfView
dotnet run -c Release -f net10.0 -- bench --filter '*CsvFormatting*' --profiler EP

# Machine-readable output for regression gating
dotnet run -c Release -f net10.0 -- bench --filter '*' --exporters json --artifacts ./artifacts
```

> **Two classes define their own runtime matrix in attributes** — `Step1.RuntimeComparison` and
> `Step4.StackAllocationPatterns`. Don't pass `--runtimes` or `--job` for those: you'll end up with
> two baseline jobs in one group and BenchmarkDotNet will refuse to run.
>
> **Multiple filters are space-separated, not `|`:**
> `--filter '*Step2*' '*Step3*'`

---

## The API

```bash
dotnet run -c Release -f net10.0 --project samples/PerfLab -- api
# → GET /          lists every endpoint, grouped by step
# → GET /metrics   Prometheus scrape endpoint
```

> The project pins **Workstation GC** because the benchmarks share its runtimeconfig, while
> ASP.NET Core would normally default to Server GC. The app prints a warning at startup and `GET /`
> always reports the mode actually in effect. For production-representative memory behaviour
> (and to see DATAS at work — [Step 1 — DATAS](../WaysToMeasure.md#gc-pause-time-and-datas)):
>
> ```powershell
> $env:DOTNET_gcServer=1; dotnet run -c Release -f net10.0 -- api
> ```

Each endpoint produces a **different, recognisable signature** in `dotnet-counters`. Learning to
tell them apart is the skill Step 1 is really teaching.

| Endpoint | Signature | Diagnosis |
|---|---|---|
| `/step1/healthy` | Everything flat | The control — run it first |
| `/step1/cpu` | `cpu-usage` saturated, queue ~0 | Genuinely CPU-bound |
| `/step1/alloc` | `alloc-rate` + gen0 high, heap floor flat | Allocation-rate problem |
| `/step1/loh` | `loh-size` grows, gen2 rises | LOH churn |
| `/step1/leak` | Heap floor rises after every gen2 | Retention — a different bug, different fix |
| `/step1/block` | **Queue climbs while CPU stays low** | Thread-pool starvation |
| `/step1/contend` | `monitor-lock-contention-count` spikes | Lock contention |
| `/step2/*` | sync vs async vs sync-over-async | Step 2's whole argument, under load |
| `/step3/*` | parallel-in-request vs sequential | [Step 3 — the ASP.NET Core reality](../ParallelismVsConcurrency.md#7-the-aspnet-core-reality): measure at `-c 1` **and** `-c 100` |
| `/step4/*` | naive vs pooled, same output | [Step 4 — proving it](../MemoryOptimizationTricks.md#8-proving-it-and-when-not-to-bother): does `alloc-rate` drop, and does p99 care? |

### Leak lab — practising snapshot diffs

`/step1/leak` retains plain `byte[]`, which is hard to spot in a heap diff because everything
allocates `byte[]`. `/leaks/*` retains **named** types, each with a `-fixed` twin:

| Endpoint | Leaked type | Paths to Root shows |
|---|---|---|
| `/leaks/event-subscriber` | `OrderNotifier` | `PriceFeed.PriceChanged` — subscribed, never unsubscribed |
| `/leaks/report-cache` | `CustomerReport` | the static cache — key includes a timestamp, never hits |
| `/leaks/heartbeat` | `SessionHeartbeat` | the runtime timer queue — timer never disposed |

Snapshot in the Visual Studio profiler (Memory Usage) → `bombardier -n 5000 -c 10
"http://localhost:5199/leaks/event-subscriber"` → snapshot again → sort the diff by **Count Diff**.
Repeat against the `-fixed` URL and the count stays flat. `GET /leaks/stats` shows what each
scenario is holding; `POST /leaks/reset` releases all but the timers.

### CPU lab — finding the hot line

`/step1/cpu` burns CPU in an anonymous lambda. `/cpu/*` puts real-world hotspots in **named**
methods (`OrderChecks.*Slow`), each with a `-fixed` twin returning identical results:

| Endpoint | Hot line | Slow → fixed (one request, this machine) |
|---|---|---|
| `/cpu/duplicate-orders` | `List.Contains` inside a loop | 18.7 → 1.5 ms |
| `/cpu/validate-emails` | `new Regex(..., Compiled)` per email | 156 → 0.6 ms |
| `/cpu/parse-quantities` | `int.Parse` + `catch` on invalid input | 14.7 → 0.8 ms |

Visual Studio profiler (CPU Usage) → `bombardier -c 8 -d 20s
"http://localhost:5199/cpu/duplicate-orders"` → stop → **Hot Path** → double-click the
`OrderChecks` method to get CPU % per source line.

### The three load tests worth running

Each demonstrates something a microbenchmark structurally **cannot** show.

> **These were not run when the documents were written** — no load generator or `dotnet-*`
> diagnostic tool is installed on that machine. Every endpoint is verified to start and return
> correct results, and every BenchmarkDotNet table in the documents is real output. The load-test
> *outcomes* described in the docs are flagged as unverified where they appear. Install the tools
> below and they become the most convincing material in the series:
>
> ```bash
> dotnet tool install -g dotnet-counters
> winget install bombardier          # or: go install github.com/codesenberg/bombardier@latest
> ```

```bash
# Terminal 1
dotnet run -c Release -f net10.0 --project samples/PerfLab -- api

# Terminal 2
dotnet-counters ps
dotnet-counters monitor -p <pid> --refresh-interval 1 \
  System.Runtime Microsoft.AspNetCore.Hosting PerfLab.Api
```

**1. Async scales; sync doesn't** ([Step 2 — measuring async](../AsyncVsSync.md#11-measuring-async)) — identical per-request latency in isolation:

```bash
bombardier -c 200 -d 30s "http://localhost:5199/step2/async-io?ms=200"
bombardier -c 200 -d 30s "http://localhost:5199/step2/sync-io?ms=200"
```

Watch `threadpool-queue-length` climb while `cpu-usage` stays near idle on the sync run.

**2. Parallelism inside a request is a trade, not a win** ([Step 3](../ParallelismVsConcurrency.md#7-the-aspnet-core-reality)) — run each twice:

```bash
bombardier -c 1   -d 20s "http://localhost:5199/step3/parallel-in-request?items=2000"
bombardier -c 1   -d 20s "http://localhost:5199/step3/sequential-in-request?items=2000"
#   → parallel wins on latency

bombardier -c 100 -d 30s "http://localhost:5199/step3/parallel-in-request?items=2000"
bombardier -c 100 -d 30s "http://localhost:5199/step3/sequential-in-request?items=2000"
#   → sequential usually wins on throughput
```

**3. Fewer allocations ≠ better p99** ([Step 4](../MemoryOptimizationTricks.md#8-proving-it-and-when-not-to-bother)):

```bash
curl "http://localhost:5199/step4/alloc-report?rows=500"     # bytes per call, side by side
bombardier -c 50 -d 30s "http://localhost:5199/step4/naive-report?rows=500"
bombardier -c 50 -d 30s "http://localhost:5199/step4/pooled-report?rows=500"
```

`alloc-rate` will drop. Whether p99 moves enough to justify the code is the actual question.

### Then capture and analyse

```bash
# CPU: 30 s sample → open the JSON at speedscope.app, use "Left Heavy"
dotnet-trace collect -p <pid> --profile cpu-sampling --duration 00:00:30 \
  --format speedscope -o cpu.speedscope.json

# Memory: two heap graphs, diffed in Visual Studio
curl "http://localhost:5199/step1/leak?n=100000"
dotnet-gcdump collect -p <pid> -o t0.gcdump
curl "http://localhost:5199/step1/leak?n=100000"
dotnet-gcdump collect -p <pid> -o t1.gcdump

# Blocked/hung: two stack snapshots, diffed
dotnet-stack report -p <pid> > s1.txt && dotnet-stack report -p <pid> > s2.txt

# Custom metrics
dotnet-counters monitor -p <pid> PerfLab.Api
```

---

## Notes on the configuration

- A repo-scoped [`nuget.config`](../nuget.config) clears inherited package sources. Without it,
  restore inherits machine-level feeds and any authenticated one fails the build with NU1301 for
  reasons unrelated to this repo. Every package used here is public.
- `ValidateScopes` / `ValidateOnBuild` are on unconditionally in the API, to catch a
  scoped-service-captured-by-a-singleton at startup — the most common ASP.NET Core leak
  ([Step 1 — detecting a leak](../WaysToMeasure.md#its-a-leak)). They default to on in Development only.
- `System.Numerics.Tensors` is referenced for `TensorPrimitives` ([Step 4 — SIMD](../MemoryOptimizationTricks.md#6-simd)); it ships
  out-of-band rather than in the shared framework.
- `OpenTelemetry.Exporter.Prometheus.AspNetCore` is at a `-beta` version because it has never
  shipped a stable release. Every other package is stable.
