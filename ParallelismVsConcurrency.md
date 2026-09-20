# Step 3 — Parallel vs Concurrent Code in .NET

> **Prerequisites from [Step 1](WaysToMeasure.md):** [§2.4 thread-pool health](WaysToMeasure.md#24-thread-pool-health),
> [§4.4 `[ThreadingDiagnoser]`](WaysToMeasure.md#44-diagnosers-worth-knowing),
> [§9.2 hardware counters](WaysToMeasure.md#92-hardware-performance-counters).
> From [Step 2](AsyncVsSync.md): [§9.3 unbounded fan-out](AsyncVsSync.md#93-unbounded-fan-out-is-a-different-bug) —
> this document is the answer to the problem Step 2 raises there.
>
> **Baseline:** .NET 10. `[.NET 9]` marks `System.Threading.Lock` and `Task.WhenEach`.
>
> **Companion code:** [samples/PerfLab/Benchmarks/Step3/](samples/PerfLab/Benchmarks/Step3/) and
> [samples/PerfLab/Api/Step3Endpoints.cs](samples/PerfLab/Api/Step3Endpoints.cs).

---

## The one-sentence version

**Concurrency is about structure; parallelism is about execution.** And in an ASP.NET Core service
the server is *already* parallel across requests — so parallelizing work *inside* a request does not
create capacity, it reallocates it away from your other requests.

---

## Table of contents

1. [The distinction, and why it decides your API](#1-the-distinction-and-why-it-decides-your-api)
2. [Choosing](#2-choosing)
3. [The API survey](#3-the-api-survey)
4. [What parallelism costs](#4-what-parallelism-costs)
5. [False sharing](#5-false-sharing)
6. [Synchronization primitives](#6-synchronization-primitives)
7. [Concurrent collections](#7-concurrent-collections)
8. [The ASP.NET Core reality](#8-the-aspnet-core-reality)
9. [Advanced](#9-advanced)
10. [Measuring](#10-measuring)
11. [Decision flow and anti-patterns](#11-decision-flow-and-anti-patterns)

---

## 1. The distinction, and why it decides your API

| | **Concurrency** | **Parallelism** |
|---|---|---|
| Definition | Dealing with many things at once | Doing many things at once |
| About | **Structure** — how work is composed | **Execution** — how work is run |
| Needs multiple cores? | **No.** Works on one. | **Yes**, or it's pointless |
| Typical driver | I/O waits | CPU work |
| Goal | Don't block while waiting | Finish sooner by using more cores |
| .NET tools | `async`/`await`, `Channel`, `Task.WhenAll` | `Parallel.For`, PLINQ, SIMD |
| Step | [Step 2](AsyncVsSync.md) | This one |

The canonical illustration: one cook alternating between a simmering pot, a chopping board, and the
oven is **concurrent** — one worker, several unfinished jobs. Three cooks each on their own dish are
**parallel**. Three cooks who each also juggle three dishes are both.

They are **orthogonal**, not opposites. That matters because almost every wrong API choice comes from
treating them as a single spectrum:

```
                    │ Single-threaded        │ Multi-threaded
────────────────────┼────────────────────────┼──────────────────────────
 One task at a time │ A plain console app    │ Parallel.For over a batch
                    │                        │ (parallel, not concurrent)
────────────────────┼────────────────────────┼──────────────────────────
 Many in flight     │ Node-style event loop; │ ASP.NET Core
                    │ async on one thread    │ (concurrent AND parallel)
                    │ (concurrent, not       │
                    │  parallel)             │
```

### 1.1 Why picking the wrong one is expensive

| Mistake | What happens |
|---|---|
| **Parallelism for an I/O problem** — `Parallel.For` over HTTP calls | Threads block on I/O; the pool throttles injection; you get [Step 2 §2.2's starvation spiral](AsyncVsSync.md#22-the-starvation-spiral) with extra scheduling overhead |
| **Concurrency for a CPU problem** — `await Task.WhenAll` over pure computation | Every task runs on the pool anyway, but you've paid for state machines and continuations to get there. Slower than a loop. |
| **Parallelism inside a request** | Faster for one caller, lower total throughput (§8) |
| **Neither, where one was needed** | You leave 8× on the table, or you block |

---

## 2. Choosing

Two questions settle it almost every time.

```
  What is the work waiting on?
  ├─ I/O (network, disk, DB)  →  CONCURRENCY
  │   How many items?
  │   ├─ A few, known, independent   →  Task.WhenAll                 (§3.1)
  │   ├─ Many                        →  Parallel.ForEachAsync        (§3.3)  ← default
  │   └─ A continuous stream         →  Channel<T>                   (§3.5)
  │
  └─ CPU (computation)       →  PARALLELISM
      How much work per item?
      ├─ Small (< ~10 µs)             →  a plain loop. Seriously.    (§4.1)
      ├─ Large, order-insensitive     →  Parallel.For / ForEach      (§3.2)
      ├─ Large, as a query            →  PLINQ                       (§3.4)
      └─ Uniform over numeric data    →  SIMD                        (§9.3, Step 4)
```

And one more, which overrides all of the above in a web service:

> **Is this running inside a request handler?** If yes, read §8 before parallelizing anything.

---

## 3. The API survey

Each option below gets an explicit advantages/disadvantages table, because the trade-offs are what
actually decide it — not the syntax.

Measured first — 100 independent I/O operations, five strategies (i5-13420H, .NET 10.0.4,
`--job short`):

```
| Method               | Mean        | Ratio | Allocated | Alloc Ratio |
|--------------------- |------------:|------:|----------:|------------:|
| WhenAllUnbounded     |    13.85 ms | 0.010 |  28.06 KB |        1.08 |
| ParallelForEachAsync |   184.60 ms | 0.129 |  37.69 KB |        1.45 |
| SemaphoreThrottled   |   184.63 ms | 0.129 |   45.9 KB |        1.77 |
| ChannelPipeline      |   199.80 ms | 0.140 |  30.98 KB |        1.19 |
| Sequential           | 1,436.49 ms | 1.000 |  25.96 KB |        1.00 |
```

**Read this table carefully, because the fastest row is the one you should usually not ship.**

`WhenAllUnbounded` is **13× faster than every bounded option** — it starts all 100 operations at
once, so total time is one operation's latency. The bounded options (DOP 8) take ~100/8 × latency.
The benchmark is measuring exactly what it claims to measure, and the unbounded version genuinely
wins it.

What the benchmark cannot see is everything the unbounded version consumes to get there: 100
simultaneous connections from the pool, 100 simultaneous requests at the downstream service, and
peak memory holding all 100 operations at once. Scale 100 to 100,000 — which is what happens when
the collection comes from user input — and it is a denial-of-service generator.

This is the clearest example in the series of a benchmark answering the wrong question. The bounded
options cost 13× wall-clock in exchange for **predictable, bounded pressure on every shared
resource**, and that trade is almost always correct in a service.

Note also that `SemaphoreThrottled` and `ParallelForEachAsync` are identical on time (184.6 ms both)
but the semaphore version allocates **22% more** — it materialises all N tasks up front, while
`ForEachAsync` runs DOP workers pulling from the source.

### 3.1 `Task.WhenAll`

```csharp
var tasks = urls.Select(u => _http.GetStringAsync(u));
string[] results = await Task.WhenAll(tasks);
```

| Advantages | Disadvantages |
|---|---|
| Simplest possible fan-out | **No concurrency limit** — the defining flaw |
| Preserves result order | Materializes all N tasks up front; memory scales with N |
| Fastest wall-clock (everything starts at once) | Only rethrows the first exception (see [Step 2 §9.2](AsyncVsSync.md#92-taskwhenall-and-exceptions)) |
| Natural for a fixed, small set | Waits for every task even after one fails |

**Use for:** a handful of known, independent operations.
**Don't use for:** a collection whose size you don't control. That's a self-inflicted DoS.

### 3.2 `Parallel.For` / `Parallel.ForEach`

```csharp
Parallel.For(0, items.Length, i => Process(items[i]));

Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 4 }, Process);
```

| Advantages | Disadvantages |
|---|---|
| Range partitioning, work stealing, load balancing for free | **Synchronous only** — an `async` lambda binds as `async void` (§11.2) |
| `MaxDegreeOfParallelism` to bound it | Real per-call overhead; loses below a work threshold (§4.1) |
| Thread-local accumulation overload avoids shared state | Uses the shared pool — competes with request handling |
| Exceptions collected into `AggregateException` | Default DOP is unbounded-ish; easy to starve the pool |

**Use for:** CPU-bound work over a collection, with enough work per item to amortize partitioning.

The overload worth knowing, because it eliminates the shared-state problem entirely:

```csharp
long total = 0;
Parallel.For(0, n,
    () => 0L,                                     // per-thread local
    (i, state, local) => local + Compute(i),      // accumulate locally (a register)
    local => Interlocked.Add(ref total, local));  // combine once per thread
```

That's one interlocked operation per *thread*, not per *item*. §5 and §6 both show it beating
everything else by a wide margin.

### 3.3 `Parallel.ForEachAsync` [.NET 6+]

```csharp
await Parallel.ForEachAsync(
    items,
    new ParallelOptions { MaxDegreeOfParallelism = 8 },
    async (item, ct) => await ProcessAsync(item, ct));
```

| Advantages | Disadvantages |
|---|---|
| **Bounded async concurrency** — the thing `Task.WhenAll` lacks | Results aren't returned; you collect them yourself |
| Doesn't create N tasks; runs DOP workers pulling from the source | No ordering guarantee |
| Cancellation token per item, plumbed properly | Needs a thread-safe accumulator for results |
| Works with `IAsyncEnumerable` | |

**This is the default answer for I/O fan-out over a collection.** If you're reaching for
`Task.WhenAll` on something with an unknown count, reach here instead.

Picking `MaxDegreeOfParallelism`: there is no universal number. It should be governed by what the
*downstream* can absorb — connection pool size, the target service's rate limit, DB `max_connections`
— not by `Environment.ProcessorCount`, which is about CPU and irrelevant to I/O. Make it
configuration, not a constant, so you can tune it without redeploying.

### 3.4 PLINQ

```csharp
var results = source.AsParallel()
                    .WithDegreeOfParallelism(4)
                    .Where(Expensive)
                    .Select(Transform)
                    .ToArray();
```

Measured — 100,000 items, swept by per-item cost (i5-13420H, .NET 10.0.4, `--job short`):

```
| Method          | WorkPerItem | Mean         | Ratio | Allocated | Alloc Ratio |
|---------------- |------------ |-------------:|------:|----------:|------------:|
| Linq            | 1           |    133.32 μs |  1.00 |     168 B |        1.00 |
| Plinq           | 1           |    121.16 μs |  0.91 |    9392 B |       55.90 |
| PlinqOrdered    | 1           |    121.10 μs |  0.91 |    9476 B |       56.40 |
| PlinqBoundedDop | 1           |    128.94 μs |  0.97 |    6214 B |       36.99 |
| ManualLoop      | 1           |     67.64 μs |  0.51 |         - |        0.00 |
| ManualParallel  | 1           |     28.39 μs |  0.21 |    3862 B |       22.99 |
|                 |             |              |       |           |             |
| Linq            | 1000        | 34,857.34 μs |  1.00 |     168 B |        1.00 |
| Plinq           | 1000        |  5,251.72 μs |  0.15 |    9392 B |       55.90 |
| PlinqOrdered    | 1000        |  5,501.61 μs |  0.16 |    9480 B |       56.43 |
| PlinqBoundedDop | 1000        |  8,668.23 μs |  0.25 |    6215 B |       36.99 |
| ManualParallel  | 1000        |  5,195.10 μs |  0.15 |    4102 B |       24.42 |
| ManualLoop      | 1000        | 38,117.61 μs |  1.09 |         - |        0.00 |
```

Four things, two of which contradict the usual advice:

1. **At cheap work, `.AsParallel()` bought 9%** — and a **plain loop beat PLINQ by 1.8×**
   (67.6 µs vs 121.2 µs). All eight cores lost to one core without the query machinery. If your
   per-item work is cheap, the win available from deleting LINQ is larger than the win available
   from parallelizing it.
2. **At expensive work, PLINQ is excellent** — 6.6× faster, and statistically tied with the
   hand-written parallel loop (5,252 vs 5,195 µs). When the work is genuinely expensive, PLINQ's
   overhead stops mattering and you get the parallelism for one word.
3. **`.AsOrdered()` was nearly free here** — 121.10 vs 121.16 µs at cheap work (indistinguishable),
   and ~5% at expensive work. *This corrects a claim I had written from received wisdom before
   measuring.* Ordering costs buffering and reordering, so it is not free in general — but on a
   `Where`/`Select`/`Sum` pipeline it is far cheaper than its reputation. Measure your shape.
4. **PLINQ allocates 56× more than LINQ** (9,392 B vs 168 B) regardless of work size — partitioners,
   merge buffers, and per-partition state. Constant, not per-element, so it amortizes on big
   queries and dominates on small ones.

| Advantages | Disadvantages |
|---|---|
| One-word change to an existing query | Deceptively easy to add where it doesn't pay (see row 1) |
| Excellent when per-item work is genuinely expensive | ~56× the allocations of sequential LINQ |
| Handles partitioning, merging, aggregation | Delegate indirection per element |
| `WithDegreeOfParallelism`, `WithCancellation` | `.AsOrdered()` costs *something* — measure it |
| Ties hand-written parallel code on expensive work | Exceptions wrapped in `AggregateException` |
| | Synchronous only, same `async` trap as `Parallel.For` |

**Use for:** an expensive, CPU-bound query over a large in-memory collection.
**Be aware:** on a cheap query, deleting the LINQ beats parallelizing it.

### 3.5 `System.Threading.Channels`

```csharp
var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(1000)
{
    FullMode = BoundedChannelFullMode.Wait,   // ← backpressure
    SingleReader = false,
    SingleWriter = true,                      // lets the impl skip writer synchronization
});

// producer
await channel.Writer.WriteAsync(item, ct);
channel.Writer.Complete();

// consumer(s)
await foreach (WorkItem item in channel.Reader.ReadAllAsync(ct))
    await ProcessAsync(item, ct);
```

| Advantages | Disadvantages |
|---|---|
| **Real backpressure** — a bounded channel makes the producer *wait* | More moving parts than a loop |
| Decouples producer and consumer rates | You own the consumer lifecycle |
| Multiple producers/consumers, independently scaled | Ordering only with `SingleReader` |
| Allocation-free fast paths; `SingleReader`/`SingleWriter` hints | Overkill for one-shot fan-out |
| Natural fit for `IHostedService` background processing | |

**Use for:** long-lived pipelines, background processing, and anywhere a fast producer can outrun a
slow consumer. That last case is where `FullMode = Wait` earns the whole abstraction: the alternative
to blocking the producer is an unbounded queue, and an unbounded queue is an OOM with extra steps.

`FullMode` options: `Wait` (backpressure), `DropOldest` / `DropNewest` / `DropWrite` (shed load).
Choosing to drop is a legitimate design decision — just make it deliberately.

### 3.6 `Task.WhenEach` [.NET 9]

```csharp
await foreach (Task<Result> completed in Task.WhenEach(tasks))
    Handle(await completed);     // completion order, not submission order
```

Process results as they arrive instead of waiting for the slowest. Before .NET 9 this meant a
`WhenAny`-and-remove loop that was O(n²). Still unbounded — pair it with §3.3 if N is large.

### 3.7 `SemaphoreSlim` throttling

```csharp
using var gate = new SemaphoreSlim(8);
await Task.WhenAll(items.Select(async item =>
{
    await gate.WaitAsync(ct);
    try { await ProcessAsync(item, ct); }
    finally { gate.Release(); }
}));
```

| Advantages | Disadvantages |
|---|---|
| Works anywhere; composes with anything | **Still creates all N tasks** — memory scales with N, not DOP |
| The semaphore can be shared across call sites | Easy to leak a permit (`finally` is mandatory) |
| Fine-grained control `ForEachAsync` doesn't give you | More code for what §3.3 does natively |

`SemaphoreSlim.WaitAsync` is the async-compatible gate — a `lock` cannot be held across an `await`.
That's often the real reason to reach for it.

### 3.8 TPL Dataflow

`ActionBlock`, `TransformBlock`, `BufferBlock` from `System.Threading.Tasks.Dataflow`. A full
pipeline framework with per-block parallelism, bounded capacity, and linking.

**Use it when** you have a genuine multi-stage pipeline with different parallelism per stage and want
the plumbing handled. **Otherwise** `Channel` is lighter, better maintained, and does most of what
people actually use Dataflow for.

---

## 4. What parallelism costs

### 4.1 The crossover point

Parallelism is not free, and below some amount of work per item it is a straight loss.

Measured — 4,096 items, swept by per-item cost (i5-13420H, 12 logical cores, .NET 10.0.4,
`--job short`):

```
| Method                | WorkPerItem | Mean          | Ratio | Allocated |
|---------------------- |------------ |--------------:|------:|----------:|
| SequentialFor         | 1           |      4.070 μs |  1.00 |         - |
| ParallelForChunked    | 1           |      7.247 μs |  1.78 |    3217 B |
| ParallelForBoundedDop | 1           |     10.013 μs |  2.46 |    2607 B |
| ParallelFor           | 1           |     16.984 μs |  4.17 |    3920 B |
|                       |             |               |       |           |
| SequentialFor         | 100         |    278.649 μs |  1.00 |         - |
| ParallelFor           | 100         |     49.744 μs |  0.18 |    3987 B |
| ParallelForBoundedDop | 100         |     72.622 μs |  0.26 |    2586 B |
| ParallelForChunked    | 100         |     92.740 μs |  0.33 |    3510 B |
|                       |             |               |       |           |
| SequentialFor         | 10000       | 28,695.100 μs |  1.00 |         - |
| ParallelFor           | 10000       |  3,906.504 μs |  0.14 |    4051 B |
| ParallelForChunked    | 10000       |  4,612.784 μs |  0.16 |    4087 B |
| ParallelForBoundedDop | 10000       |  6,407.810 μs |  0.22 |    2643 B |
```

**The crossover is real and it is not close.** At `WorkPerItem = 1` (total sequential time: 4 µs)
`Parallel.For` is **4.17× SLOWER** on 12 logical cores. At `WorkPerItem = 100` (total: 279 µs) it is
5.6× faster. The flip happens somewhere in between — call it a few tens of microseconds of total
work before parallelizing pays at all.

Two secondary findings worth carrying:

- **Chunking rescues the small-work case.** `ParallelForChunked` (one work item per core instead of
  per element) is 2.3× faster than plain `Parallel.For` at `WorkPerItem = 1` — the overhead being
  paid there is scheduling, and chunking amortizes it. It still loses to the sequential loop.
- **Chunking *hurts* in the middle.** At `WorkPerItem = 100` chunked (92.7 µs) is nearly 2× worse
  than the default (49.7 µs), because fixed chunks give up the dynamic load balancing that the
  default partitioner provides. There is no chunking strategy that wins everywhere — which is the
  argument for leaving the default alone unless you've measured your specific shape.
- **A parallel loop allocates ~2.6–4 KB regardless of size.** Task objects, partitioner state,
  per-thread locals. Constant, so it disappears on big workloads and is another reason it loses on
  small ones.

The costs, all of which are roughly constant per call while the benefit scales with total work:

| Cost | What it is |
|---|---|
| **Partitioning** | Splitting the range, creating work items |
| **Scheduling** | Queuing to the pool, possible thread injection |
| **Work stealing** | Idle workers scanning others' queues |
| **Joining** | The barrier at the end — you wait for the *slowest* partition |
| **Delegate invocation** | One call per item, usually not inlined |
| **Cache effects** | Threads on different cores, cold caches, possible false sharing (§5) |

### 4.2 Amdahl's law, again

[Step 1 §1.2](WaysToMeasure.md#12-amdahls-law-as-a-sanity-filter) applies directly. If 20% of an
operation is inherently sequential, infinite cores gets you 5×, not more. And the sequential fraction
is usually bigger than it looks: setup, the final merge, the lock everyone passes through, and the
allocation that triggers a GC everyone waits for.

### 4.3 Oversubscription

Nested parallelism is where this bites. A `Parallel.For` whose body calls a method that also uses
`Parallel.For` produces cores² work items competing for cores threads. The pool's hill-climbing
makes it worse before it makes it better.

**Rule:** parallelize at exactly one level, as high up as you can. If a library you call might
parallelize internally, don't parallelize the calls to it.

### 4.4 Exceptions and cancellation

```csharp
try
{
    Parallel.ForEach(items, options, Process);
}
catch (AggregateException ex)
{
    foreach (Exception inner in ex.Flatten().InnerExceptions) Log(inner);
}
```

`Parallel.*` collects exceptions from all partitions into an `AggregateException`. `Flatten()`
matters — nesting produces `AggregateException` inside `AggregateException`.

Note the asymmetry with cancellation: a `ParallelOptions.CancellationToken` that fires produces
`OperationCanceledException`, **not** wrapped in an `AggregateException`. Catch both.

---

## 5. False sharing

The most counter-intuitive cost in parallel code: threads that never touch the same variable, never
take a lock, and are algorithmically perfectly independent — and still serialize.

Measured — 12 threads × 2,000,000 increments, each to *its own* counter (i5-13420H, .NET 10.0.4,
`--job short`):

```
| Method                  | Mean      | Ratio | Allocated |
|------------------------ |----------:|------:|----------:|
| Packed                  | 30.589 ms |  1.00 |   4.16 KB |
| Padded                  | 11.191 ms |  0.37 |   3.89 KB |
| ThreadLocalAccumulation |  1.173 ms |  0.04 |   4.43 KB |
```

**Identical work, identical algorithm, no locks anywhere, 26× spread.** The only difference between
`Packed` and `Padded` is 56 bytes of unused space between the counters.

`ThreadLocalAccumulation` — accumulate into a local, combine once at the end — is another 9.5×
beyond padding, because the local lives in a register and never touches shared memory at all.

### 5.1 The mechanism

Cache coherency operates on **cache lines** (64 bytes on x64), not on variables.

```
  Cache line (64 bytes)
  ┌────────┬────────┬────────┬────────┬────────┬────────┬────────┬────────┐
  │ ctr[0] │ ctr[1] │ ctr[2] │ ctr[3] │ ctr[4] │ ctr[5] │ ctr[6] │ ctr[7] │
  └────────┴────────┴────────┴────────┴────────┴────────┴────────┴────────┘
       ▲        ▲
   thread 0  thread 1   ← different variables, SAME LINE
```

When thread 0 writes `ctr[0]`, every other core's copy of that entire line is invalidated. Thread 1's
next write to `ctr[1]` — its own variable, which nobody else reads — must re-fetch the line. The
hardware ping-pongs one line between cores for the duration of the loop.

**Nothing in the source says these threads interact.** Only the memory layout does. This is invisible
in code review and shows up only when you measure — which is why [Step 1 §9.2's cache-miss
counters](WaysToMeasure.md#92-hardware-performance-counters) exist.

### 5.2 Fixes, best first

1. **Don't share.** Accumulate into a local and combine once (§3.2's thread-local overload). No
   layout reasoning required, and it wins.
2. **Pad to a cache line**, when you genuinely need per-thread slots:
   ```csharp
   [StructLayout(LayoutKind.Explicit, Size = 64)]
   struct PaddedCounter { [FieldOffset(0)] public long Value; }
   ```
3. **Separate the arrays** so each thread's data is contiguous and far from its neighbours'.

---

## 6. Synchronization primitives

Measured — 100,000 increments (i5-13420H, .NET 10.0.4, `--job short`, `[ThreadingDiagnoser]`):

```
| Method                                         | Mean        | Ratio  | Lock Contentions |
|----------------------------------------------- |------------:|-------:|-----------------:|
| 'uncontended: no lock'                         |    21.99 μs |   1.00 |                - |
| 'uncontended: Interlocked'                     |   395.18 μs |  17.97 |                - |
| 'uncontended: System.Threading.Lock [.NET 9]'  | 1,425.67 μs |  64.83 |                - |
| 'uncontended: ReaderWriterLockSlim (write)'    | 1,427.65 μs |  64.92 |                - |
| 'uncontended: lock(object)'                    | 1,589.41 μs |  72.27 |                - |
| 'uncontended: SemaphoreSlim(1,1)'              | 3,488.77 μs | 158.64 |                - |
|                                                |             |        |                  |
| 'CONTENDED: no sharing (accumulate + combine)' |    14.13 μs |   0.64 |                - |
| 'CONTENDED: Interlocked'                       | 2,506.35 μs | 113.97 |                - |
| 'CONTENDED: lock(object)'                      | 5,412.70 μs | 246.13 |          14.6875 |
| 'CONTENDED: System.Threading.Lock [.NET 9]'    | 6,189.71 μs | 281.46 |          23.8516 |
```

### 6.0 What this table actually says

**The headline is the last row of the contended block versus the first.** Restructuring to remove
sharing entirely (14.13 µs) beats the best lock under contention (2,506 µs) by **177×** — and beats
the *single-threaded, no-lock* baseline (21.99 µs), because the work genuinely parallelises once
nothing is shared.

> **The fastest lock is the one you don't take.** Every entry in §6.2 is what you use when you
> cannot restructure. Try to restructure first.

Secondary readings:

- **`Interlocked` is ~4× cheaper than any lock**, uncontended or contended, when the operation is a
  single-variable update. Use it where it fits.
- **`SemaphoreSlim` is the most expensive** uncontended option (2.2× a `lock`). You use it for the
  async capability (§6.3), not for speed.
- **`System.Threading.Lock` beat `lock(object)` by 10% uncontended** — a modest, real improvement.
- **Under contention, the two locks are indistinguishable.** `Lock` measured *slower* (6,190 vs
  5,413 µs), but the errors were ±2,643 and ±2,888 µs — the intervals overlap almost completely.
  **This benchmark did not show a difference under contention**, in either direction, and reporting
  one would be reading noise. The `--job short` setting (3 iterations) makes contended measurements
  particularly unstable; a full run would narrow this.

### 6.1 `System.Threading.Lock` [.NET 9]

```csharp
// Before
private readonly object _gate = new();

// .NET 9 / C# 13 — preferred for new code
private readonly Lock _gate = new();

lock (_gate) { /* ... */ }     // the compiler calls Lock.EnterScope(), not Monitor.Enter()
```

| Advantages | Disadvantages |
|---|---|
| ~10% faster than `Monitor` **uncontended** (measured above); no measurable difference contended | .NET 9+ only |
| The compiler **warns** if a `Lock` is cast to `object` and locked (which would silently use `Monitor` instead) | Not usable with `Monitor.Wait`/`Pulse` |
| Self-documenting: the field's type says what it's for | |
| `EnterScope()` returns a `ref struct` — can't escape or be captured | |

For new code on .NET 9+, prefer it. The compiler warning alone justifies the change: locking a
`Lock` that's been cast to `object` compiles fine and silently does the wrong thing, and now you
get told.

### 6.2 The full set

| Primitive | Use for | Notes |
|---|---|---|
| `Interlocked` | Single-variable atomics | Lock-free; `Increment`, `Add`, `Exchange`, `CompareExchange` |
| `Lock` [.NET 9] | General mutual exclusion | The default for new code |
| `lock (object)` / `Monitor` | Same, pre-.NET 9 | Supports `Wait`/`Pulse` |
| `SemaphoreSlim` | **Async-compatible** gating, or N-at-a-time | The only one you can hold across an `await` |
| `ReaderWriterLockSlim` | Many readers, rare writers | Only pays off when reads dominate *and* the critical section is long |
| `SpinLock` | Extremely short critical sections, high core count | Rarely the right answer; burns CPU while waiting |
| `Barrier` | Phased algorithms | Niche |
| `ManualResetEventSlim` | Signalling | `.Wait()` blocks — see [Step 2 §3](AsyncVsSync.md#3-sync-over-async) |

### 6.3 You cannot `lock` across an `await`

```csharp
lock (_gate)
{
    await DoAsync();     // ← does not compile, and that is a kindness
}
```

A monitor is owned by a *thread*; a continuation may resume on a different one, so releasing would be
undefined. Use `SemaphoreSlim`:

```csharp
await _semaphore.WaitAsync(ct);
try { await DoAsync(ct); }
finally { _semaphore.Release(); }
```

### 6.4 Lock-free with immutable snapshots

When reads vastly outnumber writes and the data is small, replace the whole thing atomically and let
readers proceed with no synchronization at all:

```csharp
private volatile Dictionary<string, Config> _config = new();

public Config? Get(string key) =>
    _config.TryGetValue(key, out var c) ? c : null;    // no lock, ever

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

Readers never block. Writers copy — fine when writes are rare and the map is small, wrong when either
isn't true. The `volatile` and the CAS loop are both load-bearing; this is a pattern to copy exactly
rather than adapt casually.

### 6.5 Enough memory model to not write a bug

- **Reads and writes can be reordered** by the compiler, JIT, and CPU.
- `volatile` prevents *some* reorderings. It is not a lock and does not make compound operations
  (`x++`) atomic.
- `Interlocked` operations are atomic **and** act as full memory barriers.
- Reads and writes of reference types and of ≤ 32-bit primitives are atomic; **`long`/`double` on
  32-bit platforms are not** — use `Interlocked.Read`.
- `lock` gives you a full barrier at both ends. If everything mutable is inside a lock, you can stop
  thinking about the rest of this list — which is the strongest argument for using one.

---

## 7. Concurrent collections

Measured — 1,000 string keys of the form `key-NNN` (i5-13420H, .NET 10.0.4, `--job short`):

```
| Method                                    | Mean       | Error       | Ratio | Allocated |
|------------------------------------------ |-----------:|------------:|------:|----------:|
| 'Dictionary (not thread-safe)'            |   5.135 μs |   0.7383 μs |  1.00 |         - |
| 'ConcurrentDictionary'                    |   4.835 μs |   4.8583 μs |  0.94 |         - |
| 'FrozenDictionary [.NET 8]'               |   5.774 μs |  13.5306 μs |  1.12 |         - |
|                                           |            |             |       |           |
| 'ConcurrentDictionary: parallel GetOrAdd' |  82.237 μs |  17.3437 μs | 16.01 |  147780 B |
| 'Dictionary + lock: parallel add'         | 208.165 μs | 131.1058 μs | 40.54 |   77688 B |
```

**Read the Error column on the first three rows before concluding anything.** The means span
4.8–5.8 µs while the errors are 0.7, 4.9, and 13.5 µs. These three lookups are
**indistinguishable at this measurement precision** — including `FrozenDictionary`, which the
conventional advice says should win. See §7.3.

The write comparison *is* decisive: `ConcurrentDictionary` is **2.5× faster** than a locked
`Dictionary` for parallel adds, because it shards its internal locks rather than serialising
everything through one. It allocates more (147 KB vs 78 KB) to do it.

### 7.1 The lineup

| Type | Use for | Watch out for |
|---|---|---|
| `ConcurrentDictionary<K,V>` | Shared mutable map | §7.2 — `GetOrAdd` is not atomic |
| `ConcurrentQueue<T>` | FIFO handoff | Consider `Channel` instead — it has backpressure and async |
| `ConcurrentBag<T>` | Unordered, same-thread add/remove | Thread-local storage; slow when producers ≠ consumers |
| `BlockingCollection<T>` | Classic producer/consumer | **Blocks.** Use `Channel` in async code |
| `ImmutableArray/Dictionary` | Snapshot semantics | O(n) copy per write |
| `FrozenDictionary/Set` [.NET 8] | **Read-only after build** | Expensive to construct, fastest to read |

### 7.2 `GetOrAdd` is not atomic

```csharp
// Looks atomic. Is not.
var conn = _cache.GetOrAdd(key, k => new SqlConnection(k));
```

The value factory **is not run under a lock**. Under contention it can run multiple times for the
same key; only one result is stored and **the rest are silently discarded**. If the factory is a pure
computation, that's wasted CPU. If it opens a connection, starts a timer, or costs money, it's a
resource leak that only appears under load.

The fix, when the factory has side effects:

```csharp
var lazy = _cache.GetOrAdd(key, k => new Lazy<SqlConnection>(
    () => new SqlConnection(k), LazyThreadSafetyMode.ExecutionAndPublication));
SqlConnection conn = lazy.Value;    // Lazy guarantees the factory runs exactly once
```

`Lazy<T>` objects may be created more than once; the expensive value inside is created exactly once.

### 7.3 `FrozenDictionary` [.NET 8]

```csharp
private static readonly FrozenDictionary<string, Handler> Handlers =
    BuildHandlers().ToFrozenDictionary();
```

Spends extra time at construction analysing the keys to pick an optimal lookup strategy. Intended
for maps built once at startup and read on every request — route tables, feature flags, config.
Never for anything that changes.

**But read the measured table above before assuming it's a win.** With 1,000 string keys of the
shape `key-NNN`, all three lookups landed within each other's error bars on this machine —
`FrozenDictionary` showed no advantage at all. Its edge depends heavily on key type, key shape, and
set size (it does best on small sets and on integer or short, distinctive string keys). It is a
*safe* default for immutable maps because construction-time cost is paid once and lookup is never
worse — but "faster than `Dictionary`" is a claim to verify on your keys, not to assume.

---

## 8. The ASP.NET Core reality

This section is the one that changes the most code.

### 8.1 The server is already parallel

Kestrel serves requests concurrently across all cores. At any moment your service is already using
every core it has, as long as there is load. Therefore:

> **Parallelizing inside a request does not create capacity. It reallocates capacity from your
> other requests to this one.**

The effect goes in opposite directions depending on what you measure:

| Load | `Parallel.For` inside a request |
|---|---|
| 1 concurrent request | **Faster** — provided the work is above §4.1's crossover |
| 100 concurrent requests | **Slower overall.** Scheduling overhead added, nothing gained. |

`/step3/sequential-in-request` vs `/step3/parallel-in-request` exist to make you watch this happen
rather than take it on faith. Benchmark at `-c 1`, then at `-c 100`, and note that a microbenchmark
would only ever have shown you the first one.

A single-call measurement from those endpoints, worth having before you start:

```
GET /step3/sequential-in-request?items=500  → 0.59 ms
GET /step3/parallel-in-request?items=500    → 2.97 ms     ← 5× SLOWER, with one caller
```

That is §4.1's crossover showing up in a live service: at 500 items the work is far too cheap to
justify partitioning, so parallelism loses **even in the best case for it**. Raise `items` to
~50,000 and the parallel version starts winning single-request — and only then does the throughput
question in the table above become interesting.

> **Not measured here.** That single-call comparison is real output from this machine. The
> **concurrent** rows are not: no load generator is installed where these documents were written, so
> the endpoints are verified to run and return correct results, but the throughput reversal at
> `-c 100` is described from established behaviour rather than observed. It is the most valuable
> thing in Step 3 to confirm for yourself.

### 8.2 When it *is* right

| Scenario | Why |
|---|---|
| **Background jobs** (`IHostedService`) | Not on the request path; no requests to steal from |
| **A genuinely batch endpoint** — "process these 10,000 rows" | The work is inherently big and callers are few |
| **Low-traffic internal tools** | There are no other requests to starve |
| **Fan-out to other services** (I/O, not CPU) | Not parallelism at all; it's §3.3 concurrency |

In the first two, **bound it**. `MaxDegreeOfParallelism = ProcessorCount / 2` leaves room for the
rest of the service to keep serving.

### 8.3 Per-request state is not thread-safe

The moment you parallelize inside a request, everything the request touches becomes shared state:

- **`DbContext` is not thread-safe.** Concurrent use throws, or corrupts its change tracker. One
  context per parallel branch, from a scoped factory.
- **`HttpContext` is not thread-safe.** Don't touch it from parallel branches.
- **Scoped services** are shared across the branches of one request. Whether that's safe depends on
  the service.
- **`IHttpContextAccessor`** flows via `AsyncLocal`, so it works — at the cost
  [Step 2 §11](AsyncVsSync.md#11-where-async-allocations-come-from) measured.

---

## 9. Advanced

### 9.1 Custom partitioning

`Parallel.For`'s default range partitioner assumes roughly uniform per-item cost. When cost varies
wildly, one partition finishes last and everyone waits at the join.

```csharp
// Small chunks → better balance for uneven work, more scheduling overhead
Parallel.ForEach(Partitioner.Create(0, items.Length, rangeSize: 64), range =>
{
    for (int i = range.Item1; i < range.Item2; i++) Process(items[i]);
});
```

The `ParallelCrossover.ParallelForChunked` benchmark shows the other direction: **larger** chunks
amortize scheduling and are what rescue the small-work case.

### 9.2 PLINQ merge options

```csharp
source.AsParallel()
      .WithMergeOptions(ParallelMergeOptions.NotBuffered)   // stream results as produced
      .Select(Transform);
```

`NotBuffered` lowers latency-to-first-result; `FullyBuffered` maximizes throughput; `AutoBuffered`
(the default) compromises. Relevant when you're streaming results to a caller rather than
materializing them.

### 9.3 SIMD — the other parallelism

Data parallelism: one instruction, many values, **one core**. Orthogonal to everything above, and it
composes — a `Parallel.For` whose body is vectorized uses both.

```csharp
var acc = Vector<float>.Zero;
for (int i = 0; i <= data.Length - Vector<float>.Count; i += Vector<float>.Count)
    acc += new Vector<float>(data.Slice(i, Vector<float>.Count));
float sum = Vector.Sum(acc);
// ... plus the tail
```

[Step 4 §12](MemoryOptimizationTricks.md) covers this properly, including `TensorPrimitives`
[.NET 9], which is where you should start.

### 9.4 `Parallel.For` with an `async` lambda — the trap

```csharp
Parallel.ForEach(items, async item => await ProcessAsync(item));   // ← async void
```

`Parallel.ForEach` takes `Action<T>`, so the `async` lambda binds as **`async void`**. The loop
returns immediately, having started work it does not track; exceptions crash the process; nothing is
awaited. See [Step 2 §10.1](AsyncVsSync.md#101-async-void).

Use `Parallel.ForEachAsync` (§3.3). The compiler will not stop you, so this belongs on the review
checklist.

---

## 10. Measuring

| Question | Tool | Where |
|---|---|---|
| Are threads contending? | `dotnet-counters` → `monitor-lock-contention-count` | [Step 1 §3.2](WaysToMeasure.md#32-dotnet-counters--the-first-thing-you-run) |
| Which lock? | `dotnet-dump` → `!syncblk` | [Step 1 §3.5](WaysToMeasure.md#35-dotnet-dump--sos--the-last-resort-that-always-works) |
| Did my change reduce contention? | BDN `[ThreadingDiagnoser]` → "Lock Contentions" column | [Step 1 §4.4](WaysToMeasure.md#44-diagnosers-worth-knowing) |
| Are threads running or waiting? | dotTrace **Timeline**, PerfView **Thread Time** | [Step 1 §5.2](WaysToMeasure.md#52-jetbrains-rider--dottrace--dotmemory) |
| Is it false sharing? | `[HardwareCounters(CacheMisses)]`, or `perf stat -e cache-misses` | [Step 1 §9.2](WaysToMeasure.md#92-hardware-performance-counters) |
| Does parallelism help *the service*? | Load test at `-c 1` **and** `-c 100`. A benchmark shows only the first. | [Step 1 §8.3](WaysToMeasure.md#83-load-generation) |

`[ThreadingDiagnoser]`'s "Lock Contentions" column is the one to watch in this document's
benchmarks — a change that improves Mean while raising contention has moved the cost, not removed it.

---

## 11. Decision flow and anti-patterns

### 11.1 Flow

```
Is the work I/O-bound?
├─ YES → concurrency, not parallelism
│   ├─ Few, fixed, independent?   → Task.WhenAll
│   ├─ Many?                      → Parallel.ForEachAsync (bounded)   ← default
│   ├─ Continuous stream?         → Channel<T>
│   └─ Need results as they land? → Task.WhenEach [.NET 9]
│
└─ NO (CPU-bound)
    ├─ In an ASP.NET Core request handler?
    │   ├─ YES → Probably don't (§8). If you must: MaxDegreeOfParallelism ≤ cores/2
    │   └─ NO  → continue
    ├─ Per-item work < ~10 µs?    → a plain loop (§4.1)
    ├─ Expensive, independent?    → Parallel.For/ForEach with thread-local accumulation
    ├─ Expressible as a query?    → PLINQ (measure against the loop first)
    └─ Uniform numeric data?      → SIMD (Step 4 §12)
```

### 11.2 Anti-patterns

| Anti-pattern | Why | Instead |
|---|---|---|
| `Parallel.ForEach` with an `async` lambda | Binds as `async void`; nothing awaited | `Parallel.ForEachAsync` |
| `Task.WhenAll` over an unbounded collection | No limit — exhausts pools, DoSes downstream | `Parallel.ForEachAsync` with a DOP |
| `Parallel.For` over HTTP calls | Blocks pool threads on I/O | `Parallel.ForEachAsync` |
| `.AsParallel()` "because it's faster" | Reliably slower for cheap queries | Measure; usually leave it sequential |
| Nested parallelism | Oversubscription, cores² work items | Parallelize at one level only |
| `lock` inside a parallel loop body | Serializes the loop you just parallelized | Thread-local accumulate + combine |
| Shared counter incremented per item | False sharing, or interlocked contention | The thread-local overload (§3.2) |
| `ConcurrentDictionary.GetOrAdd` with a side-effecting factory | Factory can run repeatedly | `Lazy<T>` as the value |
| `ConcurrentDictionary` for a read-only map | Pays for thread safety it doesn't need | `FrozenDictionary` [.NET 8] |
| Parallelizing inside a request "for latency" | Throughput drops under real load | Measure at `-c 100`, not `-c 1` |
| `DbContext` shared across parallel branches | Not thread-safe | One context per branch |

### 11.3 Review checklist

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
- [ ] No `lock` held across an `await` (won't compile) and no `SemaphoreSlim` permit leaked
- [ ] `DbContext`/`HttpContext` not shared across parallel branches

---

## Where this goes next

| Step | Document | Connection |
|---|---|---|
| 1 | [WaysToMeasure.md](WaysToMeasure.md) | Contention counters, `[ThreadingDiagnoser]`, cache-miss counters |
| 2 | [AsyncVsSync.md](AsyncVsSync.md) | §9.3's unbounded fan-out — §3.3 here is the fix |
| 4 | [MemoryOptimizationTricks.md](MemoryOptimizationTricks.md) | §9.3's SIMD in full, plus why per-item closures in parallel loops are a GC problem |

---

## References

- [Parallel Programming in .NET — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/)
- [`Parallel.ForEachAsync` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.parallel.foreachasync)
- [System.Threading.Channels — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
- [`System.Threading.Lock` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock)
- [FrozenDictionary — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.collections.frozen.frozendictionary)
- [The .NET Memory Model](https://github.com/dotnet/runtime/blob/main/docs/design/specs/Memory-model.md)
