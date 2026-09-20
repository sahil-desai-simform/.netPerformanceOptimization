# .NET Performance Optimization

Application-side performance engineering for .NET — measuring it, then fixing it.

**Baseline: .NET 10 (LTS).** Features introduced in .NET 8 or 9 are tagged so readers on an older
LTS know what they'd gain; `[.NET 11]` items (GA Nov 2026) are forward-looking.

**Scope:** CPU, memory, and thread behaviour in the application process — with an ASP.NET Core
services bias. Query tuning and database server configuration are out of scope, though
[Step 1 §8.4](WaysToMeasure.md#84-when-the-app-is-innocent) covers how to *prove* the bottleneck is
or isn't the database before going looking.

---

## The four steps

| Step | Document | Question it answers |
|---|---|---|
| 1 | **[Ways to Measure](WaysToMeasure.md)** | How do I find out where CPU and memory actually go — in production, in a profiler, and in a benchmark? |
| 2 | **[Async vs Sync](AsyncVsSync.md)** | How do I use threads well? When does async help, when does it cost, and how does blocking take a service down? |
| 3 | **[Parallelism vs Concurrency](ParallelismVsConcurrency.md)** | What's the difference, which do I need, and what does each one cost me? |
| 4 | **[Memory Optimization Tricks](MemoryOptimizationTricks.md)** | Spans, pooling, and allocation-free code — and when the complexity is worth it. |

Read them in order. Step 1 is the foundation: every claim in Steps 2–4 is something you should be
able to verify with the tools it introduces, and each later document points back at the specific
measurement that proves its technique worked.

## Companion code

**[samples/](samples/)** — one project, two entry points, multi-targeted at net8.0/9.0/10.0:

```bash
dotnet build samples/PerfLab.sln -c Release
cd samples/PerfLab

dotnet run -c Release -f net10.0 -- api                   # the ASP.NET Core lab
dotnet run -c Release -f net10.0 -- bench --list flat     # the benchmark suite
```

- **`Api/`** — deliberately pathological endpoints, each producing a different, recognisable
  signature in `dotnet-counters`. Grouped by step.
- **`Benchmarks/`** — a BenchmarkDotNet suite, also grouped by step, including cross-runtime
  comparisons that build and run on .NET 8, 9, and 10.

See [samples/README.md](samples/README.md) for the guided walkthrough, including the three load
tests that demonstrate things a microbenchmark structurally cannot.

## Every benchmark table was measured

No illustrative figures. Every BenchmarkDotNet table is real output from a 13th Gen Core i5-13420H
running .NET 10.0.4, and each is labelled as such.

Two categories of claim are **not** measured here, and are flagged inline wherever they appear:
the `[.NET 11]` runtime-async section (cited from published sources — this machine has SDK 10), and
the concurrent load-test outcomes (no load generator installed; the endpoints are verified to run,
the throughput behaviour is described rather than observed).

Where a measurement contradicted the expected story, the measurement was kept and the prose
rewritten — which happened often enough to be worth stating:

- Step 1's lowest-allocation CSV formatter turned out to be **47% slower** — and Step 4's "obvious
  fix" for it **failed**, which turned out to be the more useful finding.
- Step 2's `ValueTask` is 2× faster and allocates 99% less at 100% synchronous completion — and is
  **slower, allocating more**, at 0%.
- Step 3's `Parallel.For` is **4× slower** than a plain loop at low work-per-item, and PLINQ loses
  to a plain `for` there too.
- Step 3's `FrozenDictionary` and Step 4's `SearchValues` both showed **no measurable advantage**
  over what they're meant to replace, on the shapes tested.
- Step 4's `ArrayPool` is **slower than `new byte[128]`** for small buffers, and **24× faster**
  above the LOH threshold.

The headline positive result: on .NET 10, iterating an array through `IEnumerable<T>` is now
**statistically identical to direct indexing** (41.4 vs 42.3 ns) and allocates nothing. On .NET 8
the same code cost 2.4× more and allocated 32 B.

## The one rule

Measure first, measure after, and compare ratios rather than absolute numbers. A benchmark that got
10× faster while the p99 didn't move means your model of the system was wrong — which is useful
information, but it is not a performance improvement.
