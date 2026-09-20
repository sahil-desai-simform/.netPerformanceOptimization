# Step 3 — Parallel vs Concurrent Code in .NET

> **Baseline:** .NET 10. `[.NET 9]` marks `System.Threading.Lock` and `Task.WhenEach`.
> **Before this:** [Step 1 — thread-pool health](WaysToMeasure.md#24-thread-pool-health), and
> [Step 2](AsyncVsSync.md), whose unbounded fan-out problem this document answers.
> **Companion code:** [samples/PerfLab/Benchmarks/Step3/](samples/PerfLab/Benchmarks/Step3/), [samples/PerfLab/Api/Step3Endpoints.cs](samples/PerfLab/Api/Step3Endpoints.cs).
> **All numbers here are measured** on an i5-13420H (12 logical cores), .NET 10.0.4, `--job short`.
> Raw output: [samples/PerfLab/BenchmarkDotNet.Artifacts/results/](samples/PerfLab/BenchmarkDotNet.Artifacts/results/).

## The one-sentence version

**Concurrency is about structure; parallelism is about execution.** And in an ASP.NET Core service
the server is *already* parallel across requests — so parallelizing work *inside* a request does not
create capacity, it reallocates it away from your other requests.

## The rules

1. **I/O → concurrency. CPU → parallelism.** Getting this backwards is the expensive mistake.
2. **`Parallel.ForEachAsync` is the default for I/O fan-out.** Bounded, and it doesn't materialize
   N tasks.
3. **Never `Task.WhenAll` a collection whose size you don't control.** That is a self-inflicted DoS.
4. **Set the I/O degree-of-parallelism from downstream capacity**, not `ProcessorCount`. Make it
   configuration, not a constant.
5. **Below ~10 µs of work per item, use a plain loop.** `Parallel.For` measured 4.17× *slower*.
6. **Never pass an `async` lambda to `Parallel.For`/`ForEach`.** It binds as `async void`.
7. **Parallelize at exactly one level**, as high up as you can. Nested parallelism gives you cores²
   work items competing for cores threads.
8. **Accumulate into a thread-local and combine once.** Never a shared counter per item.
9. **Restructure before you optimize the lock.** Removing sharing beat the best contended lock by
   177×.
10. **`SemaphoreSlim` is the only gate you can hold across an `await`.** That's why you use it — not
    for speed; it's the slowest primitive measured.
11. **Inside a request handler, probably don't.** If you must, bound it at `ProcessorCount / 2`.
12. **Measure at `-c 100`, not `-c 1`.** A microbenchmark structurally cannot show you the reversal.

## Symptom → fix

| What you observe | Likely cause | What to do |
|---|---|---|
| `threadpool-queue-length` climbs, CPU ~20% | Pool threads blocked on I/O | You used parallelism for an I/O problem — go async |
| Parallel version slower than the loop | Below the crossover | Per-item work is too cheap. Delete the parallelism |
| Fast for one caller, throughput drops under load | Parallelism inside a request | Reallocating, not creating, capacity |
| `monitor-lock-contention-count` is high | A lock in the loop body | Thread-local accumulate + combine once |
| No locks, no sharing, still doesn't scale | False sharing | Counters on one cache line — accumulate locally |
| Downstream service returns 429 / connection timeouts | Unbounded fan-out | `Parallel.ForEachAsync` with a DOP |
| Memory spikes with the size of the input collection | `Task.WhenAll` or `SemaphoreSlim` over N items | `ForEachAsync` runs DOP workers instead |
| A cache factory ran twice for one key | `GetOrAdd` is not atomic | `Lazy<T>` as the value |
| Process exits silently mid-loop | `async` lambda bound as `async void` | `Parallel.ForEachAsync` |

## 1. Which one do you need?

| | **Concurrency** | **Parallelism** |
|---|---|---|
| Definition | Dealing with many things at once | Doing many things at once |
| About | **Structure** — how work is composed | **Execution** — how work is run |
| Needs multiple cores? | **No.** Works on one | **Yes**, or it's pointless |
| Typical driver | I/O waits | CPU work |
| .NET tools | `async`/`await`, `Channel`, `Task.WhenAll` | `Parallel.For`, PLINQ, SIMD |
| Step | [Step 2](AsyncVsSync.md) | This one |

They are **orthogonal**, not opposites — ASP.NET Core is both. Almost every wrong API choice comes
from treating them as one spectrum:

| Mistake | What happens |
|---|---|
| **Parallelism for an I/O problem** — `Parallel.For` over HTTP calls | Threads block on I/O, the pool throttles injection, and you get [Step 2's starvation spiral](AsyncVsSync.md) with extra scheduling overhead |
| **Concurrency for a CPU problem** — `WhenAll` over pure computation | Every task runs on the pool anyway; you paid for state machines to get there. Slower than a loop |
| **Parallelism inside a request** | Faster for one caller, lower total throughput |
| **Neither, where one was needed** | You leave 8× on the table, or you block |

Two questions settle it almost every time:

```
  What is the work waiting on?
  ├─ I/O (network, disk, DB)  →  CONCURRENCY
  │   ├─ A few, known, independent   →  Task.WhenAll
  │   ├─ Many                        →  Parallel.ForEachAsync    ← default
  │   └─ A continuous stream         →  Channel<T>
  │
  └─ CPU (computation)       →  PARALLELISM
      ├─ Small (< ~10 µs/item)        →  a plain loop. Seriously.
      ├─ Large, order-insensitive     →  Parallel.For / ForEach
      ├─ Large, as a query            →  PLINQ
      └─ Uniform over numeric data    →  SIMD (Step 4)
```

And one that overrides all of them: **is this inside a request handler?** If so, read "The ASP.NET
Core reality" below before parallelizing anything.

## 2. The API survey

| API | Use when | Don't | Gotcha |
|---|---|---|---|
| `Task.WhenAll` | A few known, independent I/O ops | The count comes from user input | **No concurrency limit.** Materializes all N tasks; rethrows only the first exception |
| `Parallel.ForEachAsync` `[.NET 6]` | **Default for I/O fan-out** | You need results in order | Results aren't returned — you collect them yourself |
| `Parallel.For` / `ForEach` | CPU work, enough per item to amortize | Per-item work is cheap; the body is async | **Synchronous only** — an `async` lambda binds as `async void` |
| PLINQ (`.AsParallel()`) | Expensive CPU query over a big collection | The query is cheap | 56× the allocations of sequential LINQ, constant regardless of size |
| `Channel<T>` | Long-lived pipelines, producer/consumer | One-shot fan-out | You own the consumer lifecycle |
| `Task.WhenEach` `[.NET 9]` | Handle results as they land | N is large | Still unbounded — pair it with `ForEachAsync` |
| `SemaphoreSlim` | Gating across an `await`; sharing a limit between call sites | `ForEachAsync` would do | **Still creates all N tasks.** A leaked permit is a deadlock |
| TPL Dataflow | A real multi-stage pipeline, different DOP per stage | Anything simpler | `Channel` is lighter and better maintained |

**The fastest row in a fan-out benchmark is the one you should usually not ship.** 100 independent
I/O operations:

| Strategy | Time | Allocated |
|---|---|---|
| `Task.WhenAll`, unbounded | **13.9 ms** | 28.1 KB |
| `Parallel.ForEachAsync` (DOP 8) | 184.6 ms | 37.7 KB |
| `SemaphoreSlim` throttled (DOP 8) | 184.6 ms | 45.9 KB |
| `Channel` pipeline | 199.8 ms | 31.0 KB |
| Sequential | 1,436.5 ms | 26.0 KB |

Unbounded is **13× faster than every bounded option** — it starts all 100 at once, so total time is
one operation's latency. The benchmark measures exactly what it claims to. What it *cannot* see is
what the unbounded version consumes to get there: 100 simultaneous connections, 100 simultaneous
requests at the downstream service, peak memory holding all 100. Scale 100 to 100,000 — which is what
happens when the collection comes from user input — and it is a denial-of-service generator. The
bounded options buy **predictable, bounded pressure on every shared resource** for 13× wall-clock,
and in a service that trade is almost always correct.

Note that `SemaphoreSlim` and `ForEachAsync` tie on time but the semaphore allocates **22% more** —
it materializes all N tasks up front, while `ForEachAsync` runs DOP workers pulling from the source.

```csharp
// The default for I/O fan-out: bounded, no N tasks, token per item
await Parallel.ForEachAsync(items,
    new ParallelOptions { MaxDegreeOfParallelism = 8 },
    async (item, ct) => await ProcessAsync(item, ct));
```

**Pick the DOP from what the downstream can absorb** — connection pool size, the target's rate limit,
DB `max_connections` — not from `Environment.ProcessorCount`, which is about CPU and irrelevant to
I/O. Make it configuration so you can tune it without redeploying.

```csharp
// Backpressure: a bounded channel makes a fast producer WAIT
var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(1000)
{
    FullMode = BoundedChannelFullMode.Wait,   // the alternative is an OOM with extra steps
    SingleWriter = true,                      // lets the impl skip writer synchronization
});

await channel.Writer.WriteAsync(item, ct);
await foreach (WorkItem w in channel.Reader.ReadAllAsync(ct)) await ProcessAsync(w, ct);
```

`FullMode` also offers `DropOldest` / `DropNewest` / `DropWrite`. Shedding load is a legitimate
design decision — just make it deliberately.

### PLINQ: a plain loop beat it by 1.8×

100,000 items, swept by per-item cost:

| Per-item work | LINQ | PLINQ | Plain loop | Manual parallel |
|---|---|---|---|---|
| Cheap (1) | 133.3 µs | 121.2 µs | **67.6 µs** | 28.4 µs |
| Expensive (1000) | 34,857 µs | **5,252 µs** | 38,118 µs | 5,195 µs |

At cheap work `.AsParallel()` bought 9%, and **a plain loop beat PLINQ by 1.8×** — all eight cores
lost to one core without the query machinery. If per-item work is cheap, the win from *deleting*
LINQ is larger than the win from parallelizing it. At expensive work PLINQ is excellent: 6.6× faster
and statistically tied with a hand-written parallel loop, for one word.

Two corrections to received wisdom: PLINQ allocates **56× more than LINQ** (9,392 B vs 168 B)
regardless of work size — constant, so it amortizes on big queries and dominates on small ones. And
`.AsOrdered()` was **nearly free here** (121.10 vs 121.16 µs at cheap work, ~5% at expensive), which
is cheaper than its reputation on a `Where`/`Select`/`Sum` pipeline. Measure your shape.

## 3. What parallelism costs

**The crossover is real and it is not close.** 4,096 items:

| Per-item work | Sequential loop | `Parallel.For` | Chunked |
|---|---|---|---|
| 1 (total: 4 µs) | **4.07 µs** | 16.98 µs — **4.17× slower** | 7.25 µs |
| 100 (total: 279 µs) | 278.6 µs | **49.7 µs** — 5.6× faster | 92.7 µs |
| 10,000 | 28,695 µs | **3,907 µs** | 4,613 µs |

The flip happens somewhere between — call it a few tens of microseconds of *total* work before
parallelizing pays at all.

- **Chunking rescues the small case and hurts the middle.** One work item per core instead of per
  element is 2.3× faster than plain `Parallel.For` at the cheapest setting (it still loses to the
  sequential loop), but nearly 2× *worse* at 100, because fixed chunks give up the dynamic load
  balancing the default partitioner provides. There is no chunking strategy that wins everywhere —
  which is the argument for leaving the default alone unless you've measured your shape.
- **A parallel loop allocates ~2.6–4 KB regardless of size** — task objects, partitioner state,
  per-thread locals. Constant, so it vanishes on big workloads and is another reason it loses on
  small ones.
- **Amdahl still applies.** If 20% is inherently sequential, infinite cores gets you 5× — and the
  sequential fraction is usually bigger than it looks: setup, the final merge, the lock everyone
  passes through, the allocation that triggers a GC everyone waits for.
- **`Parallel.*` collects exceptions into an `AggregateException`** — call `Flatten()`, since nesting
  produces `AggregateException` inside `AggregateException`. Cancellation is the asymmetry: a firing
  `ParallelOptions.CancellationToken` throws `OperationCanceledException` **unwrapped**. Catch both.

## 4. False sharing

The most counter-intuitive cost in parallel code: threads that never touch the same variable, never
take a lock, and are algorithmically independent — and still serialize.

**Identical work, identical algorithm, no locks anywhere, 26× spread.** 12 threads × 2,000,000
increments, each to *its own* counter:

| Layout | Time |
|---|---|
| Counters packed in an array | 30.6 ms |
| Padded to 64 bytes each | 11.2 ms |
| **Thread-local, combined at the end** | **1.17 ms** |

The only difference between the first two is 56 bytes of unused space. Cache coherency operates on
**cache lines** (64 bytes on x64), not variables: when one thread writes its counter, every other
core's copy of that whole line is invalidated, and a neighbour's next write to *its own* variable
must re-fetch the line. The hardware ping-pongs one line between cores for the duration of the loop.

**Nothing in the source says these threads interact.** Only the memory layout does — invisible in
review, visible only in [cache-miss counters](WaysToMeasure.md#92-hardware-performance-counters).

Fixes, best first:

1. **Don't share.** Accumulate into a local, combine once. No layout reasoning required, and it wins
   by another 9.5× beyond padding, because the local lives in a register.
2. **Pad to a cache line** when you genuinely need per-thread slots:
   `[StructLayout(LayoutKind.Explicit, Size = 64)] struct Padded { [FieldOffset(0)] public long V; }`
3. **Separate the arrays** so each thread's data is contiguous and far from its neighbours'.

```csharp
// The Parallel.For overload that eliminates shared state entirely
long total = 0;
Parallel.For(0, n,
    () => 0L,                                     // per-thread local
    (i, state, local) => local + Compute(i),      // accumulate in a register
    local => Interlocked.Add(ref total, local));  // combine once per THREAD, not per item
```

## 5. Locks and shared state

**The fastest lock is the one you don't take.** 100,000 increments:

| | Uncontended | Contended |
|---|---|---|
| No lock / no sharing | 22.0 µs | **14.1 µs** |
| `Interlocked` | 395 µs | 2,506 µs |
| `System.Threading.Lock` `[.NET 9]` | 1,426 µs | 6,190 µs |
| `ReaderWriterLockSlim` (write) | 1,428 µs | — |
| `lock (object)` | 1,589 µs | 5,413 µs |
| `SemaphoreSlim(1,1)` | 3,489 µs | — |

Restructuring to remove sharing (14.1 µs) beats the best contended lock by **177×**, and beats the
*single-threaded, no-lock* baseline too, because the work genuinely parallelizes once nothing is
shared. Everything below that row is what you use when you cannot restructure.

- **`Interlocked` is ~4× cheaper than any lock** for a single-variable update.
- **`SemaphoreSlim` is the most expensive** uncontended option — 2.2× a `lock`. You use it for the
  async capability, not for speed.
- **`System.Threading.Lock` beat `lock(object)` by 10% uncontended.** Prefer it on .NET 9+: the
  compiler *warns* if a `Lock` is cast to `object` and locked, which would silently use `Monitor`
  instead. That warning alone justifies the change.
- **Under contention the two locks are indistinguishable.** `Lock` measured slower, but the errors
  were ±2,643 and ±2,888 µs — the intervals overlap almost completely. **This benchmark did not show
  a difference under contention**, in either direction, and reporting one would be reading noise.

| Primitive | Use for | Notes |
|---|---|---|
| `Interlocked` | Single-variable atomics | Lock-free; also a full memory barrier |
| `Lock` `[.NET 9]` | General mutual exclusion | The default for new code |
| `lock (object)` / `Monitor` | Same, pre-.NET 9 | Supports `Wait`/`Pulse` |
| `SemaphoreSlim` | **Async-compatible** gating, or N-at-a-time | The only one you can hold across an `await` |
| `ReaderWriterLockSlim` | Many readers, rare writers | Only pays off when reads dominate *and* the critical section is long |
| `SpinLock` | Extremely short sections, high core count | Rarely right; burns CPU while waiting |
| `ManualResetEventSlim` | Signalling | `.Wait()` blocks — see [Step 2](AsyncVsSync.md) |

**You cannot `lock` across an `await`** — it doesn't compile, and that is a kindness: a monitor is
owned by a *thread*, and a continuation may resume on a different one.

```csharp
await _semaphore.WaitAsync(ct);
try { await DoAsync(ct); }
finally { _semaphore.Release(); }     // finally is mandatory; a leaked permit is a deadlock
```

When reads vastly outnumber writes and the data is small, skip locking entirely — swap the whole
thing atomically and let readers proceed with no synchronization. The `volatile` and the CAS loop are
both load-bearing; copy this exactly rather than adapting it casually:

```csharp
private volatile Dictionary<string, Config> _config = new();

public Config? Get(string key) =>                            // no lock, ever
    _config.TryGetValue(key, out var c) ? c : null;

public void Update(string key, Config value)
{
    Dictionary<string, Config> current, updated;
    do
    {
        current = _config;
        updated = new Dictionary<string, Config>(current) { [key] = value };
    }
    while (Interlocked.CompareExchange(ref _config, updated, current) != current);
}
```

Enough memory model to not write a bug: **`lock` and `Interlocked` are full barriers**, `volatile`
prevents only *some* reorderings and does **not** make `x++` atomic, and `long`/`double` are not
atomic on 32-bit platforms. If everything mutable is inside a lock you can stop thinking about the
rest of it — which is the strongest argument for using one.

## 6. Concurrent collections

| Type | Use for | Watch out for |
|---|---|---|
| `ConcurrentDictionary<K,V>` | Shared mutable map | `GetOrAdd` is not atomic — see below |
| `ConcurrentQueue<T>` | FIFO handoff | Prefer `Channel` — it has backpressure and async |
| `ConcurrentBag<T>` | Unordered, same-thread add/remove | Thread-local storage; slow when producers ≠ consumers |
| `BlockingCollection<T>` | Classic producer/consumer | **Blocks.** Use `Channel` in async code |
| `ImmutableArray` / `ImmutableDictionary` | Snapshot semantics | O(n) copy per write |
| `FrozenDictionary` / `FrozenSet` `[.NET 8]` | Read-only after build | Expensive to construct, fastest to read |

`ConcurrentDictionary` is **2.5× faster than a locked `Dictionary`** for parallel adds (82.2 vs
208.2 µs) because it shards its internal locks instead of serializing everything through one. It
allocates more to do it — 148 KB vs 78 KB.

**`GetOrAdd` is not atomic**, and this is the bug in this section that costs real money:

```csharp
var conn = _cache.GetOrAdd(key, k => new SqlConnection(k));   // looks atomic. is not.
```

The value factory **is not run under a lock**. Under contention it can run several times for one key;
only one result is stored and **the rest are silently discarded**. For a pure computation that's
wasted CPU. If it opens a connection, starts a timer, or costs money, it's a resource leak that only
appears under load. When the factory has side effects, wrap it:

```csharp
var lazy = _cache.GetOrAdd(key, k => new Lazy<SqlConnection>(
    () => new SqlConnection(k), LazyThreadSafetyMode.ExecutionAndPublication));
SqlConnection conn = lazy.Value;     // the Lazy may be built twice; the connection once
```

### FrozenDictionary showed no advantage

With 1,000 string keys of the shape `key-NNN`, three lookups measured 5.14 µs (`Dictionary`),
4.84 µs (`ConcurrentDictionary`), and 5.77 µs (`FrozenDictionary`) — with errors of **0.74, 4.86 and
13.53 µs**. Read the Error column before concluding anything: these are **indistinguishable at this
precision**, including the one conventional advice says should win.

`FrozenDictionary`'s edge depends heavily on key type, key shape, and set size — it does best on
small sets and on integer or short, distinctive string keys. It remains a *safe* default for maps
built once at startup, because construction cost is paid once and lookup is never worse. But "faster
than `Dictionary`" is a claim to verify on your keys, not to assume.

## 7. The ASP.NET Core reality

This section changes the most code. Kestrel serves requests concurrently across all cores, so at any
moment under load your service is already using every core it has. Therefore:

> **Parallelizing inside a request does not create capacity. It reallocates capacity from your other
> requests to this one.**

| Load | `Parallel.For` inside a request |
|---|---|
| 1 concurrent request | **Faster** — provided the work is above the crossover |
| 100 concurrent requests | **Slower overall.** Scheduling overhead added, nothing gained |

A single-call measurement from the sample endpoints, worth having before you start:

```
GET /step3/sequential-in-request?items=500  → 0.59 ms
GET /step3/parallel-in-request?items=500    → 2.97 ms     ← 5× SLOWER, with one caller
```

That is the crossover showing up in a live service: at 500 items the work is far too cheap to justify
partitioning, so parallelism loses **even in the best case for it**. Raise `items` to ~50,000 and the
parallel version starts winning single-request — and only then does the throughput question become
interesting.

> **Not measured here.** The single-call comparison is real output from this machine. The
> **concurrent** rows are not — no load generator was installed where these documents were written,
> so the endpoints are verified to run and return correct results, but the throughput reversal at
> `-c 100` is described from established behaviour rather than observed. See
> [samples/README.md](samples/README.md). It is the most valuable thing in Step 3 to confirm yourself.

**When it *is* right:** background jobs (`IHostedService` — not on the request path), a genuinely
batch endpoint where callers are few, low-traffic internal tools, and fan-out to other services
(which is I/O concurrency, not parallelism). In the first two, **bound it** at
`MaxDegreeOfParallelism = ProcessorCount / 2` so the rest of the service keeps serving.

**And the moment you parallelize inside a request, everything it touches becomes shared state.**
`DbContext` is not thread-safe — concurrent use throws or corrupts its change tracker; use one per
branch from a scoped factory. `HttpContext` is not thread-safe either. Scoped services are shared
across the branches of one request, and whether that's safe depends on the service.

## 8. Measuring

| Question | Tool |
|---|---|
| Are threads contending? | `dotnet-counters` → `monitor-lock-contention-count` |
| Which lock? | `dotnet-dump` → `!syncblk` |
| Did my change reduce contention? | BDN [`[ThreadingDiagnoser]`](WaysToMeasure.md#44-diagnosers-worth-knowing) → "Lock Contentions" |
| Are threads running or waiting? | dotTrace **Timeline**, PerfView **Thread Time** |
| Is it false sharing? | [`[HardwareCounters(CacheMisses)]`](WaysToMeasure.md#92-hardware-performance-counters), or `perf stat -e cache-misses` |
| Does parallelism help *the service*? | [Load test](WaysToMeasure.md#83-load-generation) at `-c 1` **and** `-c 100` |

A change that improves Mean while raising the Lock Contentions column has **moved** the cost, not
removed it.

## Anti-patterns

| Anti-pattern | Why | Instead |
|---|---|---|
| `Parallel.ForEach` with an `async` lambda | Binds as `async void`; nothing awaited | `Parallel.ForEachAsync` |
| `Task.WhenAll` over an unbounded collection | No limit — exhausts pools, DoSes downstream | `Parallel.ForEachAsync` with a DOP |
| `Parallel.For` over HTTP calls | Blocks pool threads on I/O | `Parallel.ForEachAsync` |
| `.AsParallel()` "because it's faster" | Reliably slower for cheap queries | Measure; usually leave it sequential |
| Nested parallelism | Oversubscription, cores² work items | Parallelize at one level only |
| `lock` inside a parallel loop body | Serializes the loop you just parallelized | Thread-local accumulate + combine |
| Shared counter incremented per item | False sharing, or interlocked contention | The thread-local `Parallel.For` overload |
| `GetOrAdd` with a side-effecting factory | Factory can run repeatedly | `Lazy<T>` as the value |
| `ConcurrentDictionary` for a read-only map | Pays for thread safety it doesn't need | `FrozenDictionary` `[.NET 8]` |
| Parallelizing inside a request "for latency" | Throughput drops under real load | Measure at `-c 100`, not `-c 1` |
| `DbContext` shared across parallel branches | Not thread-safe | One context per branch |

## Measured surprises

| Result | What it overturns |
|---|---|
| `Parallel.For` **4.17× slower** than a plain loop at cheap work | "More cores is faster" |
| A plain loop **beat PLINQ by 1.8×** at cheap work | "`.AsParallel()` is a free win" |
| The **fastest** fan-out strategy is the one you shouldn't ship | "Pick the fastest row" |
| Removing sharing beat the best contended lock by **177×** | "Optimize the lock" |
| 26× spread from **56 bytes of padding**, no locks involved | "Independent threads scale" |
| `FrozenDictionary` showed **no advantage** over `Dictionary` | "Freeze every startup map" |
| `.AsOrdered()` was **nearly free** on this pipeline | "Ordering costs a lot" |
| Chunking helped at 1 µs/item and **hurt** at 100 | "Tune the partitioner" |

## Checklist

- [ ] Every `Parallel.*` / `.AsParallel()` has a justification that mentions per-item cost
- [ ] No `async` lambda passed to `Parallel.For`/`ForEach`/`List.ForEach`
- [ ] Every fan-out over an unbounded collection has a `MaxDegreeOfParallelism`
- [ ] DOP for I/O comes from downstream capacity, not `ProcessorCount`, and is configurable
- [ ] Parallel work inside a request handler is bounded, and was measured under concurrent load
- [ ] No nested parallelism
- [ ] Shared counters use thread-local accumulation, not per-item interlocked/locking
- [ ] `ConcurrentDictionary.GetOrAdd` factories are side-effect free, or wrapped in `Lazy<T>`
- [ ] Read-only maps built at startup use `FrozenDictionary`
- [ ] New locks on .NET 9+ use `System.Threading.Lock`
- [ ] No `SemaphoreSlim` permit released outside a `finally`
- [ ] `DbContext` / `HttpContext` not shared across parallel branches

## Where this goes next

| Step | Document | Connection |
|---|---|---|
| 1 | [WaysToMeasure.md](WaysToMeasure.md) | Contention counters, `[ThreadingDiagnoser]`, cache-miss counters |
| 2 | [AsyncVsSync.md](AsyncVsSync.md) | Unbounded fan-out — `Parallel.ForEachAsync` here is the fix |
| 4 | [MemoryOptimizationTricks.md](MemoryOptimizationTricks.md) | SIMD in full, plus why per-item closures in parallel loops are a GC problem |

## References

- [Parallel Programming in .NET — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/)
- [`Parallel.ForEachAsync` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.parallel.foreachasync)
- [System.Threading.Channels — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
- [`System.Threading.Lock` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock)
- [FrozenDictionary — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.collections.frozen.frozendictionary)
- [The .NET Memory Model](https://github.com/dotnet/runtime/blob/main/docs/design/specs/Memory-model.md)
