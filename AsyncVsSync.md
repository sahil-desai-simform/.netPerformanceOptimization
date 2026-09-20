# Step 2 — Optimizing Thread Usage: Async vs Sync in .NET

> **Prerequisites from [Step 1](WaysToMeasure.md):** [§2.4 thread-pool health](WaysToMeasure.md#24-thread-pool-health),
> [§6.1 the CPU-low/latency-high branch](WaysToMeasure.md#61-first-classify-the-problem),
> [§8.2 detecting starvation in production](WaysToMeasure.md#82-detecting-thread-pool-starvation-in-production).
> This document assumes you can already read `threadpool-queue-length` and `cpu-usage` off a live
> process; it won't re-explain them.
>
> **Baseline:** .NET 10. `[.NET 8]` / `[.NET 9]` tags mark when something arrived.
> `[.NET 11]` marks the forward-looking runtime-async section (§12) — GA November 2026.
>
> **Companion code:** [samples/PerfLab/Benchmarks/Step2/](samples/PerfLab/Benchmarks/Step2/) and
> [samples/PerfLab/Api/Step2Endpoints.cs](samples/PerfLab/Api/Step2Endpoints.cs).

---

## The one-sentence version

**Async is not a speed optimization.** It makes an individual request marginally *slower* and the
service dramatically more *scalable*. Nearly every async bug in the wild comes from someone
optimizing for the first thing while their production incident was about the second.

---

## Table of contents

1. [What async actually is](#1-what-async-actually-is)
2. [The thread pool, and why starvation is not self-healing](#2-the-thread-pool-and-why-starvation-is-not-self-healing)
3. [Sync-over-async](#3-sync-over-async)
4. [Async-over-sync](#4-async-over-sync)
5. [What async costs when nothing goes wrong](#5-what-async-costs-when-nothing-goes-wrong)
6. [`ValueTask` vs `Task`](#6-valuetask-vs-task)
7. [`ConfigureAwait`](#7-configureawait)
8. [Cancellation](#8-cancellation)
9. [Overlapping independent work](#9-overlapping-independent-work)
10. [Pitfalls](#10-pitfalls)
11. [Where async allocations come from](#11-where-async-allocations-come-from)
12. [`[.NET 11]` Runtime async](#12-net-11-runtime-async)
13. [Measuring async](#13-measuring-async)
14. [Decision tables](#14-decision-tables)

---

## 1. What async actually is

### 1.1 There is no thread

The single most useful mental correction: **`await` does not start a thread, and an in-flight async
I/O operation does not occupy one.**

When you call `await stream.ReadAsync(...)`, the runtime hands the operation to the OS (an overlapped
I/O request on Windows, `epoll`/`io_uring` on Linux) and *returns*. The thread goes back to the pool
and picks up other work. When the OS signals completion, a thread — probably a different one — picks
up the continuation and resumes your method from where it left off.

Between those two moments, **your operation exists as a heap object, not a thread.** That is the
entire value proposition. A machine can hold 50,000 pending I/O operations; it cannot hold 50,000
threads (each reserves 1 MB of stack by default, and the scheduler drowns long before that).

### 1.2 The state machine

The compiler rewrites an `async` method into a struct implementing `IAsyncStateMachine`:

```csharp
// what you write
public async Task<int> GetLengthAsync(string url)
{
    string body = await _http.GetStringAsync(url);
    return body.Length;
}

// what the compiler generates, in essence
private struct GetLengthAsyncStateMachine : IAsyncStateMachine
{
    public int State;                          // where to resume
    public AsyncTaskMethodBuilder<int> Builder; // owns the returned Task
    public string Url;                          // hoisted parameter
    private TaskAwaiter<string> _awaiter;       // what we're waiting on

    public void MoveNext()
    {
        switch (State)
        {
            case -1:
                _awaiter = _http.GetStringAsync(Url).GetAwaiter();
                if (!_awaiter.IsCompleted)      // ← the fast path check
                {
                    State = 0;
                    Builder.AwaitUnsafeOnCompleted(ref _awaiter, ref this); // ← BOXES here
                    return;
                }
                goto case 0;
            case 0:
                string body = _awaiter.GetResult();
                Builder.SetResult(body.Length);
                return;
        }
    }
}
```

Three consequences you can act on:

1. **The state machine is a struct and lives on the stack** until the method actually suspends. If
   every `await` completes synchronously, nothing is ever boxed.
2. **`AwaitUnsafeOnCompleted` is where the heap allocation happens.** Suspension costs; completing
   synchronously does not.
3. **Every local that lives across an `await` becomes a field** on that struct, and therefore part of
   the boxed object. A large local kept alive across a suspension is a per-operation heap cost.

### 1.3 The fast path is the common path

`if (!_awaiter.IsCompleted)` matters more than it looks. In real services a large fraction of awaits
complete synchronously — a cache hit, a buffered `Stream.ReadAsync` that already has the bytes, a
`Channel` with a queued item. Those paths never suspend, never box, and never schedule a
continuation.

This is why §6's `ValueTask` exists, and why §5's measurements separate "async that suspends" from
"async that doesn't" rather than quoting one number for "async overhead".

---

## 2. The thread pool, and why starvation is not self-healing

### 2.1 Shape of the pool

.NET has one process-wide thread pool, used by ASP.NET Core request handling, `Task.Run`,
timer callbacks, `Parallel.*`, and I/O completions.

| | |
|---|---|
| **Minimum threads** | Defaults to `Environment.ProcessorCount`. Threads up to this count are created **immediately** on demand. |
| **Above the minimum** | The pool **throttles** creation, adding threads on the order of **one per second** while work sits queued. |
| **Maximum threads** | Very large (thousands). Effectively never the binding constraint. |
| **Hill climbing** | The pool continuously probes whether more threads improve throughput, and backs off when they don't. |

That throttle is deliberate and usually correct: for CPU-bound work, more threads than cores makes
things slower, and unbounded creation would be a footgun. It becomes catastrophic only when threads
are **blocked** rather than busy — because then the pool's "throughput isn't improving" signal is
being generated by threads doing nothing at all.

### 2.2 The starvation spiral

```
    A blocking call holds a pool thread
                 ↓
    Requests queue up (queue-length > 0)
                 ↓
    Pool injects ~1 thread/second       ← far slower than requests arrive
                 ↓
    New threads immediately block too
                 ↓
    Queue grows; latency climbs; CPU stays LOW
                 ↓
    Health checks time out → orchestrator kills the pod
                 ↓
    Load shifts to the remaining pods → they starve faster
```

Two properties make this the signature .NET production incident:

- **It is not self-healing.** Injection is roughly linear in time while queue growth is linear in
  *load*. Under real traffic, load wins.
- **CPU looks fine.** Every CPU-based autoscaler will decline to scale you out, and every dashboard
  glance will say the servers are healthy. See [Step 1 §6.1](WaysToMeasure.md#61-first-classify-the-problem).

### 2.3 `SetMinThreads` is a tourniquet, not a cure

```csharp
ThreadPool.SetMinThreads(200, 200);   // create up to 200 threads with no throttle
```

This genuinely helps — it removes the injection delay — and it is a legitimate **emergency mitigation**
when you have a blocking dependency you cannot remove today. It is not a fix:

- Every one of those threads reserves stack (1 MB committed-on-demand on Windows).
- Context-switching between hundreds of blocked threads costs real CPU.
- It hides the problem from your metrics, so the underlying blocking call never gets removed.
- It scales linearly with concurrency, and concurrency is the thing you don't control.

Treat a `SetMinThreads` call in a codebase as a **TODO with a comment explaining which blocking call
it is compensating for**. If nobody can name the call, that's the finding.

---

## 3. Sync-over-async

Calling an async API synchronously. The most damaging pattern in .NET, and the most common.

```csharp
// All four are the same mistake
var user = _api.GetUserAsync(id).Result;
var user = _api.GetUserAsync(id).GetAwaiter().GetResult();
_api.SaveAsync(user).Wait();
var user = Task.Run(() => _api.GetUserAsync(id)).Result;   // the "fix" that isn't
```

### 3.1 Two distinct failure modes

**Deadlock** — in any context with a `SynchronizationContext` that runs continuations on a specific
thread (WinForms, WPF, legacy ASP.NET):

```
Thread A: calls .Result → blocks, holding the context
Continuation: needs the context to resume → waits for Thread A
                    ↓
              Deadlock, forever
```

**ASP.NET Core has no `SynchronizationContext`**, so this exact deadlock does not occur there. This
is why "we removed `ConfigureAwait(false)` and nothing broke" is true and misleading at the same
time — you dodged the deadlock and kept the starvation.

**Starvation** — everywhere, including ASP.NET Core. Each blocked call holds a pool thread hostage
for the full duration of the I/O, and §2.2 does the rest.

### 3.2 It is strictly worse than being synchronous

`/step2/sync-over-async` in the sample exists to make this concrete. Compared to plain blocking I/O,
sync-over-async:

- pays the full async machinery cost (state machine, `Task` allocation, continuation scheduling),
- **and** blocks a thread anyway,
- **and** wraps any exception in `AggregateException` (with `.Result`/`.Wait()`, though not with
  `GetAwaiter().GetResult()`),
- **and** loses the stack trace quality you'd get from a real `await`.

There is no scenario where it is the right answer in request-handling code.

### 3.3 Where it is genuinely acceptable

- `Main` in a console app, before any host is running — and even there, `async Task Main` is better.
- A one-off in a unit test or a design-time factory (EF Core migrations), where there's no pool to
  starve.
- Code you can prove runs on a dedicated non-pool thread.

Everything else: make the caller async. Async is viral by design — that's the feature, not a defect.

---

## 4. Async-over-sync

The mirror-image mistake: wrapping blocking work in `Task.Run` and calling the result async.

```csharp
// A library method pretending to be async
public Task<string> ReadConfigAsync() => Task.Run(() => File.ReadAllText(_path));
```

This frees the *caller's* thread and occupies a *different* pool thread. **Net thread consumption is
unchanged.** You have relocated the problem into a place where it no longer shows up in the obvious
metric.

| Where | Verdict |
|---|---|
| In a **library**, exposed as an async API | **Wrong.** You've lied to the caller about the cost, and taken away their ability to decide. Expose the sync method and let callers choose. |
| In a **UI app**, to keep the message loop responsive | **Right.** There is one thread that must not block, and offloading is exactly the goal. |
| In **ASP.NET Core**, to offload a CPU-bound chunk of a request | **Usually wrong.** The request is already on a pool thread; moving the work to another pool thread adds scheduling overhead and no capacity. See [Step 3](ParallelismVsConcurrency.md). |
| To call genuinely blocking code you cannot change, from an async path | **Pragmatic.** Bound it with a `SemaphoreSlim` so it cannot consume the whole pool, and write down why. |

Related: if a `Task`-returning method has no `await` in it, don't fake one. Return
`Task.CompletedTask` / `Task.FromResult(x)` / `ValueTask.FromResult(x)` and skip the state machine
entirely.

---

## 5. What async costs when nothing goes wrong

Measured (i5-13420H, .NET 10.0.4, `--job short`). The baseline is a non-inlined synchronous method
call — the honest comparand for an async method call:

```
| Method                                          | Mean          | Ratio    | Allocated |
|------------------------------------------------ |--------------:|---------:|----------:|
| 'sync call'                                     |     0.6776 ns |     1.00 |         - |
| 'async ValueTask, never suspends'               |     5.0989 ns |     7.60 |         - |
| 'async, never suspends'                         |    12.3689 ns |    18.43 |     144 B |
| 'sync chain, depth 10'                          |     3.5610 ns |     5.31 |         - |
| 'async chain, depth 10, never suspends'         |   106.2650 ns |   158.33 |     864 B |
| 'async, suspends once'                          |   730.4685 ns | 1,088.34 |     104 B |
| 'async chain, depth 10, suspends at the bottom' | 1,381.6823 ns | 2,058.59 |    1220 B |
```

### 5.1 Three different costs, not one

The spread from 5 ns to 1,382 ns is the reason "async overhead" as a single number is meaningless:

| Regime | Cost | What dominates |
|---|---|---|
| **Never suspends** | 5–12 ns | State machine bookkeeping. Real, but nanoseconds. |
| **Suspends once** | ~730 ns | Scheduling a continuation onto the thread pool |
| **Deep chain, suspends** | ~1,382 ns | Every frame in the chain resumes separately |

**Suspension costs ~60× more than the machinery.** Optimizing the machinery when your awaits
actually suspend is optimizing the wrong 2%.

### 5.2 The allocation numbers are not what you'd guess

Look carefully: *"async, never suspends"* allocates **144 B** while *"async, suspends once"*
allocates only **104 B**. Suspension is supposed to be the expensive one. What's going on?

Allocation here tracks **how many `Task` objects get created**, not whether a suspension happened:

- `AsyncNoSuspension` awaits `Task.FromResult(v)` — that's one `Task<int>` (72 B), plus the outer
  async method's own `Task<int>` (72 B) = **144 B**. No state machine box, because it never
  suspends.
- `AsyncSuspendsOnce` awaits `Task.Yield()`, which allocates no `Task` at all. It pays for the
  boxed state machine plus its own `Task` = **104 B**.

So "avoid suspension to avoid allocation" is the wrong lesson. The right one: **avoid creating
`Task` objects you immediately await**. Which is what §6 is about.

### 5.3 Chain depth compounds

`sync chain, depth 10` costs 3.6 ns — the JIT inlines most of it away. `async chain, depth 10`
costs 106 ns and 864 B even with **no suspension anywhere**, because each frame gets its own state
machine and its own `Task`.

This is the single strongest argument against gratuitously deep async layering: a five-layer
pass-through wrapper stack costs five state machines per call, every call. It's also precisely what
`[.NET 11]` runtime async targets (§12), where this shape reportedly improves ~7×.

---

## 6. `ValueTask` vs `Task`

### 6.1 The problem it solves

Every `Task`-returning async call that isn't served from a cache allocates a `Task` object. For a
method called millions of times that *usually completes synchronously* — a cache lookup, a buffered
read, a channel with items queued — that is a per-call allocation for a value you already had.

`ValueTask<T>` is a struct that holds **either** the result directly **or** a reference to a pending
operation. Synchronous completion means no allocation at all.

Measured — 100 lookups per operation, swept by how often the lookup hits the cache
(i5-13420H, .NET 10.0.4, `--job short`):

```
| Method        | SynchronousPercent | Mean        | Ratio | Allocated | Alloc Ratio |
|-------------- |------------------- |------------:|------:|----------:|------------:|
| WithTask      | 0                  | 76,145.8 ns |  1.00 |   10576 B |        1.00 |
| WithValueTask | 0                  | 79,052.8 ns |  1.04 |   11384 B |        1.08 |
|               |                    |             |       |           |             |
| WithTask      | 50                 | 44,806.1 ns |  1.00 |    8328 B |        1.00 |
| WithValueTask | 50                 | 45,406.3 ns |  1.01 |    5783 B |        0.69 |
|               |                    |             |       |           |             |
| WithTask      | 100                |  1,038.0 ns |  1.00 |    6624 B |        1.00 |
| WithValueTask | 100                |    492.0 ns |  0.47 |      72 B |        0.01 |
```

This is the whole argument in one table, and it runs in both directions:

- **At 100% synchronous** — `ValueTask` is **2.1× faster** and allocates **99% less** (72 B vs
  6,624 B). This is the case it was designed for, and the win is not marginal.
- **At 50%** — allocations drop 31%; the times are indistinguishable (600 ns apart, with errors of
  6,800 and 5,400 ns).
- **At 0% synchronous** — `ValueTask` is **4% slower and allocates 8% more**. Every await suspends,
  so there is no allocation to avoid, and you're paying for the struct to be copied around on top
  of the `Task` you allocate anyway.

That last row is why "just use `ValueTask` everywhere" is wrong. On a method that always does real
I/O you take on every constraint in §6.2 and get *negative* return.

### 6.2 The rules, and why they are not negotiable

A `ValueTask` may wrap a pooled, reused `IValueTaskSource`. That object can be handed to a *different*
operation as soon as yours completes. Therefore:

| Rule | What breaks if you don't |
|---|---|
| **Await it exactly once** | The second await may observe a different operation's result, or throw |
| **Never `.Result` / `.GetAwaiter().GetResult()` unless `IsCompleted`** | Undefined behaviour; no blocking-wait support |
| **Never store or cache it** | The underlying source gets recycled out from under you |
| **Never pass it to `Task.WhenAll`/`WhenAny`** | Those need `Task`. Call `.AsTask()` first — which allocates, so if you're always doing that, just return `Task` |

If you need any of those, call `.AsTask()` once and work with the result.

These are **silent, timing-dependent, load-dependent** failures. They pass tests and fail in
production. That risk is the price, and §6.3 is about whether you're getting anything for it.

### 6.3 When to actually use it

**Use `ValueTask<T>` when all of these hold:**

1. The method is genuinely hot (you measured — [Step 1 §1.2](WaysToMeasure.md#12-amdahls-law-as-a-sanity-filter)).
2. It completes synchronously most of the time.
3. Callers `await` it directly, exactly once.

**Use `Task` otherwise** — which is most of the time. Public APIs, anything callers might compose,
anything that always does real I/O. A `Task` from a method that always suspends allocates no more
than the `ValueTask` equivalent would.

### 6.4 Pooling the async machinery

For a method that *does* suspend often but is extremely hot, the state-machine box itself can be
pooled:

```csharp
[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
public async ValueTask<int> ReadAsync() { ... }
```

This reuses the boxed state machine across calls instead of allocating one per suspension. It trades
allocation rate for pool-management CPU and for objects that survive into gen2 — the exact trade
[Step 1 §2.3](WaysToMeasure.md#23-memory-two-independent-problems) tells you to keep straight. Measure
both sides; this frequently loses.

---

## 7. `ConfigureAwait`

### 7.1 What it does

`await task` captures the current `SynchronizationContext` (or `TaskScheduler`) and resumes the
continuation on it. `ConfigureAwait(false)` says "resume anywhere".

### 7.2 Where it matters

| Context | Has a SynchronizationContext? | Need `ConfigureAwait(false)`? |
|---|---|---|
| **ASP.NET Core** | No | **No.** It's a no-op — a tiny amount of noise for nothing. |
| **Console / worker service** | No | No |
| **WinForms / WPF / MAUI** | Yes | **Yes**, in any non-UI code, or you serialize everything through the UI thread |
| **Legacy ASP.NET (System.Web)** | Yes | **Yes** — and omitting it is the classic deadlock |
| **A library** | *Unknown* | **Yes.** You don't control where you run. This is the whole reason the rule exists. |

The rule reduces to: **libraries always; ASP.NET Core apps never need to.** If you own an
application and it will never be consumed as a library, the noise isn't buying you anything.

### 7.3 `ConfigureAwaitOptions` `[.NET 8]`

```csharp
await task.ConfigureAwait(ConfigureAwaitOptions.None);                     // == ConfigureAwait(false)
await task.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext); // == ConfigureAwait(true)
await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);          // await completion, ignore faults
await task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);             // never resume synchronously
```

`SuppressThrowing` is the genuinely new capability: awaiting a task purely to know it *finished*,
without caring whether it failed — useful in cleanup paths where the alternative is
`try { await t; } catch { }`, which also swallows cancellation and is harder to read.

`ForceYielding` guarantees the continuation is scheduled rather than inlined. That matters when a
synchronous completion would otherwise run your continuation on a thread holding a lock, or deepen
the stack unboundedly in a loop.

---

## 8. Cancellation

### 8.1 Plumb the token, always

`CancellationToken` is a parameter, not a global. Every async method that can take a while should
accept one and pass it down. In ASP.NET Core, `HttpContext.RequestAborted` fires when the client
disconnects — and minimal APIs / MVC will bind a `CancellationToken` parameter to it automatically:

```csharp
app.MapGet("/report", async (ReportService svc, CancellationToken ct) =>
{
    // ct is RequestAborted. If the client gives up, so do we.
    return await svc.BuildAsync(ct);
});
```

Without this, a client that times out and retries leaves the original work running. Under retry
storms that is exactly backwards: load increases as clients give up.

### 8.2 Timeouts

```csharp
// .NET 6+: the clean way. No CTS to dispose, no linked-token bookkeeping.
string body = await _http.GetStringAsync(url).WaitAsync(TimeSpan.FromSeconds(5), ct);
```

`Task.WaitAsync(TimeSpan, CancellationToken)` replaces the old `Task.WhenAny(task, Task.Delay(...))`
dance, which leaked a timer per call and left the losing task unobserved.

When you do need a linked source, **dispose it** — an undisposed `CancellationTokenSource` with a
timer is a real leak, and registrations against a long-lived token keep their targets alive. This is
one of the entries in [Step 1 §7.4's leak table](WaysToMeasure.md#74-detecting-a-leak):

```csharp
using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownToken);
await DoWorkAsync(linked.Token);
```

### 8.3 Catching it correctly

```csharp
try
{
    await DoWorkAsync(ct);
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    // Expected: we asked for this. Not an error; don't log it as one.
    return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
}
```

The `when` filter matters. Without it you also swallow cancellations that came from somewhere else —
an internal timeout, a dependency's own token — and silently turn a real failure into a clean exit.

Cancellation exceptions logged as errors are also a genuine cost, not just noise: they inflate
`exception-count` and, at scale, the cost of throwing shows up in CPU profiles.

---

## 9. Overlapping independent work

Async's one genuine *latency* win: independent operations can be in flight simultaneously.

Measured — N independent 5 ms operations (i5-13420H, .NET 10.0.4, `--job short`):

```
| Method         | Operations | Mean      | Ratio | Allocated | Alloc Ratio |
|--------------- |----------- |----------:|------:|----------:|------------:|
| Sequential     | 2          |  29.79 ms |  1.00 |     704 B |        1.00 |
| WhenAll        | 2          |  13.51 ms |  0.45 |     896 B |        1.27 |
| WhenAllViaLinq | 2          |  14.17 ms |  0.48 |     984 B |        1.40 |
|                |            |           |       |           |             |
| Sequential     | 10         | 132.10 ms |  1.00 |    2816 B |        1.00 |
| WhenAll        | 10         |  14.83 ms |  0.11 |    3168 B |        1.12 |
| WhenAllViaLinq | 10         |  14.62 ms |  0.11 |    3256 B |        1.16 |
```

The effect is arithmetic, not subtle: sequential scales with N (29.8 ms → 132 ms), overlapped
stays flat (13.5 ms → 14.8 ms). At 10 operations that's **8.9×**.

Two caveats on reading this table:

1. **`Task.Delay(5)` actually waits ~13–15 ms** here — the default Windows timer resolution is
   ~15.6 ms. The *ratios* are what matter; the absolute numbers are quantised by the platform.
2. **Overlapping is not free.** `WhenAll` allocates the `Task[]` and the combined task — 12–27%
   more than sequential. That is a very cheap price for removing 89% of the latency, but it's not
   zero, and at N=2 the ratio is worse than at N=10.

### 9.1 The rules

```csharp
// Sequential: each await completes before the next starts. 3 × latency.
var a = await GetAAsync();
var b = await GetBAsync();
var c = await GetCAsync();

// Overlapped: all three start, then we wait once. ~1 × latency.
Task<A> ta = GetAAsync();
Task<B> tb = GetBAsync();
Task<C> tc = GetCAsync();
await Task.WhenAll(ta, tb, tc);
var (a, b, c) = (ta.Result, tb.Result, tc.Result);   // safe: WhenAll guarantees completion
```

`.Result` after `await Task.WhenAll` is one of the few safe uses — the tasks are provably complete,
so nothing blocks.

**Only overlap genuinely independent work.** If B needs A's result, sequential is not a bug, it's the
data dependency.

### 9.2 `Task.WhenAll` and exceptions

`await Task.WhenAll(tasks)` rethrows only the **first** exception. To see all of them:

```csharp
Task all = Task.WhenAll(tasks);
try { await all; }
catch { throw all.Exception!; }   // AggregateException with every failure
```

Also: `WhenAll` waits for *every* task even after one fails. If you want to abandon the rest, you need
a linked `CancellationTokenSource` that you cancel on first failure.

### 9.3 Unbounded fan-out is a different bug

```csharp
// 10,000 concurrent HTTP calls. This is not "fast", it is a self-inflicted denial of service.
await Task.WhenAll(items.Select(i => _http.GetAsync(i.Url)));
```

`Task.WhenAll` has **no concurrency limit**. With a large collection you will exhaust the
`HttpClient` connection pool, overwhelm the downstream service, and — per
[Step 1 §2.2's Little's Law](WaysToMeasure.md#22-throughput-and-littles-law) — hold every one of
those operations' memory simultaneously. Bounded concurrency is [Step 3](ParallelismVsConcurrency.md)'s
subject; the short answer is `Parallel.ForEachAsync` with a `MaxDegreeOfParallelism`.

### 9.4 `Task.WhenEach` `[.NET 9]`

Process results as they arrive rather than waiting for the slowest:

```csharp
await foreach (Task<Result> completed in Task.WhenEach(tasks))
{
    Handle(await completed);     // handled in completion order, not submission order
}
```

Before .NET 9 this required a `WhenAny`-and-remove loop that was O(n²) and easy to get wrong.

---

## 10. Pitfalls

### 10.1 `async void`

```csharp
public async void ProcessAsync() { ... }   // ← almost always a bug
```

- The caller **cannot await it** — there is no task to await.
- An exception is **rethrown on the thread pool** and, with no handler, **crashes the process**.
- It is invisible to testing: nothing to assert completion on.

The only legitimate use is an event handler whose signature you don't control. Everywhere else,
return `Task`. Enable the analyzer and treat it as an error.

### 10.2 Fire-and-forget

```csharp
_ = SendEmailAsync(user);   // "it's fine, we don't need the result"
```

You have discarded the failure, not just the result. If it throws, nothing observes it. There is no
retry, no log, no metric, and the work may be killed mid-flight when the host shuts down.

If work truly should outlive the request, give it somewhere to live: an `IHostedService`, a
`Channel<T>` with a consumer, or a real queue. All three give you error handling, backpressure, and
graceful-shutdown participation. `/step2/fire-and-forget` in the sample demonstrates the
disappearance.

### 10.3 `TaskCompletionSource` without `RunContinuationsAsynchronously`

```csharp
// Default: SetResult runs the awaiting continuation INLINE, on the completing thread
var tcs = new TaskCompletionSource<int>();

// Correct for almost all uses
var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
```

With the default, `tcs.SetResult(x)` synchronously executes whatever the awaiter does next — on
*your* thread, possibly while you hold a lock, possibly recursing. This produces deadlocks and
stack overflows that are extremely hard to attribute. Use the option unless you have a specific
reason not to.

### 10.4 Eliding `async`/`await`

```csharp
public Task<int> GetAsync() => _inner.GetAsync();          // elided
public async Task<int> GetAsync() => await _inner.GetAsync();  // not
```

Measured (i5-13420H, .NET 10.0.4, `--job short`):

```
| Method                              | Mean      | Ratio | Allocated | Alloc Ratio |
|------------------------------------ |----------:|------:|----------:|------------:|
| 'return await Inner()'              | 11.944 ns |  1.00 |     144 B |        1.00 |
| 'return Inner()  (elided)'          |  3.675 ns |  0.31 |      72 B |        0.50 |
| 'return await Inner(), 3 frames'    | 29.282 ns |  2.45 |     288 B |        2.00 |
| 'return Inner(), 3 frames (elided)' |  3.705 ns |  0.31 |      72 B |        0.50 |
```

The saving is real and it **compounds per frame**: the awaited version grows 11.9 → 29.3 ns and
144 → 288 B across three wrappers, while the elided version stays flat at 3.7 ns / 72 B, because
elided frames add literally nothing — they just pass the same `Task` along.

**And you should still mostly not do it**, because the behavioural changes are not small:

| | `return await Inner()` | `return Inner()` |
|---|---|---|
| Argument-validation exception | Captured in the returned `Task` | **Thrown synchronously**, before the caller has a Task |
| `using` around the call | Disposes after the work completes | **Disposes immediately**, while the work is still running |
| Stack trace on failure | Includes this frame | This frame is missing |
| `try`/`catch` in this method | Catches | **Never runs** |

The `using` one is the dangerous one — it silently operates on a disposed resource, and it is
timing-dependent, so it passes tests. `ElidedAsync` in the sample prints a live demonstration of
both when you run it.

**Rule:** elide only in a thin pass-through with no `try`/`catch`, no `using`, and no `lock`. Keep
`await` everywhere else. The nanoseconds in that table are not worth a correctness risk — but in a
genuinely hot, genuinely trivial wrapper, they're free.

### 10.5 `async` lambdas in the wrong place

```csharp
items.ForEach(async item => await ProcessAsync(item));   // async void! Nothing is awaited.
Parallel.ForEach(items, async item => await ProcessAsync(item));  // same bug, worse
```

`List<T>.ForEach` and `Parallel.ForEach` take `Action<T>`, so an `async` lambda binds as `async void`.
The loop completes immediately, the work runs unobserved, and exceptions crash the process. Use
`await Task.WhenAll(items.Select(ProcessAsync))` or `Parallel.ForEachAsync` ([Step 3](ParallelismVsConcurrency.md)).

### 10.6 `IAsyncEnumerable` cancellation

```csharp
public async IAsyncEnumerable<Row> StreamAsync(
    [EnumeratorCancellation] CancellationToken ct = default)
{
    await foreach (Row row in _db.ReadAsync(ct))
        yield return row;
}

// caller
await foreach (Row row in svc.StreamAsync().WithCancellation(ct))
    Process(row);
```

Without `[EnumeratorCancellation]`, the token the caller supplies via `WithCancellation` never
reaches your method body, and cancellation silently does nothing.

---

## 11. Where async allocations come from

Three independent sources, routinely blamed on each other. Measured (i5-13420H, .NET 10.0.4,
`--job short`):

```
| Method                                      | Mean          | Allocated |
|-------------------------------------------- |--------------:|----------:|
| 'no await at all'                           |     0.4778 ns |         - |
| 'async, completes synchronously'            |     7.8738 ns |      72 B |
| 'Task.Run wrapping sync work'               |   983.4172 ns |      72 B |
| 'suspends, empty ExecutionContext'          |   996.3603 ns |      96 B |
| 'async, suspends (boxes the state machine)' | 1,027.1812 ns |      96 B |
| 'suspends, flow suppressed'                 | 1,075.0816 ns |     343 B |
| 'suspends, AsyncLocal populated'            | 1,084.4773 ns |     168 B |
```

### 11.1 The three sources

| Source | When you pay | Cost here |
|---|---|---|
| **The returned `Task`** | Every async call not served from a cache | 72 B |
| **The state-machine box** | Only on suspension | +24 B (72 → 96) |
| **`ExecutionContext` capture** | Only when the context isn't empty | +72 B (96 → 168) |

The state machine is a **struct on the stack** until the method actually suspends — which is why
"completes synchronously" costs 72 B (just the `Task`) and "suspends" costs 96 B.

### 11.2 `AsyncLocal` is not "a bit of overhead" — it defeats an optimization

With an empty `ExecutionContext` the runtime skips the capture entirely: 96 B. Put **one value** in
it and every await in the whole call graph starts capturing and restoring: 168 B, a **75% increase**.

This matters in ASP.NET Core because you may be using `AsyncLocal` without having written any:
`IHttpContextAccessor` and `Activity` (distributed tracing) both flow through it. That's a real
feature with a real price, and this is the price.

### 11.3 Suppressing flow did not help — read this one carefully

`ExecutionContext.SuppressFlow()` opts out of the capture, so it should be *cheaper* than the
`AsyncLocal` row. It measured **343 B — twice as much**.

Two reasons, and the honest answer is that this row does not isolate what it appears to:

1. **The shapes differ.** `SuppressFlow` can't span an `await` — `AsyncFlowControl.Undo()` must run
   on the thread that called it, and after a suspension the continuation is usually elsewhere
   (writing it the obvious way throws at runtime). So the benchmark splits the work into a second
   async method, which adds a whole extra frame and `Task`.
2. `AsyncFlowControl` and the suppression bookkeeping have their own cost.

**The takeaway is the conclusion, not the number:** `SuppressFlow` is not an optimization you reach
for. It silently breaks ambient state that other things depend on, and it did not even win the
measurement it was supposed to win. It's here to be ruled out.

### 11.4 `Task.Run` costs the same as suspending

`Task.Run` wrapping trivial sync work: 983 ns. Actually suspending: 1,027 ns. They're the same
thing — both schedule a continuation onto the pool. `Task.Run` is not a cheap way to "make something
async"; it's the full price of asynchrony for work that didn't need it. See §4.

---

## 12. `[.NET 11]` Runtime async

> **Not verified locally.** This machine has SDK 10.0.100; the figures below are from Microsoft's
> and the community's published benchmarks, cited at the end. Treat them as direction and order of
> magnitude, not as numbers measured here — which is the standard the rest of this repo holds itself
> to, so it's worth flagging where it isn't met.

.NET 11 replaces compiler-generated state machines with **runtime-managed suspension**. The runtime
saves and restores the method's state at suspension points itself, and only hoists locals that
actually live across a suspension. The .NET 11 runtime libraries themselves are compiled with
runtime-async enabled.

Reported effects:

| Scenario | Reported change |
|---|---|
| Async method that never suspends | **~20× less overhead**; lands within rounding distance of the sync baseline |
| Allocation, no-suspension path | **0 B/op, no Gen0 collections** |
| ThreadPool / `TaskCompletionSource` continuations | **3–4× faster** |
| Deep async chain (the §5 `AsyncChain` shape) | **~7.4× faster** |
| Stack traces | State-machine frames gone; traces read like synchronous ones |

### What it changes for the advice in this document

| Section | Impact |
|---|---|
| §5 (overhead) | The "async that never suspends" row collapses toward the sync baseline. The gap this document measures is a .NET 10 artifact. |
| §6 (`ValueTask`) | **Weakens the case.** `ValueTask`'s main win is avoiding allocation on synchronous completion — which runtime async gives you on `Task` too. Its sharp edges remain. Prefer `Task` in new code unless you have a .NET 10 measurement that says otherwise. |
| §6.4 (pooled builders) | Largely obsoleted. |
| §11 (allocations) | The state-machine box mostly disappears; `Task` allocation and `ExecutionContext` capture remain. |

### What it does **not** change

**Everything in §2, §3, and §4.** Blocking a thread-pool thread is still fatal; the injection rate is
unchanged; sync-over-async still starves. Runtime async makes correct async cheaper — it does not make
incorrect async safe. If you take one thing from this section, take that.

---

## 13. Measuring async

Everything here is [Step 1](WaysToMeasure.md) applied to this problem.

| Question | Tool | Section |
|---|---|---|
| Is the service starving *right now*? | `dotnet-counters` → `threadpool-queue-length` high, `cpu-usage` low | [§8.2](WaysToMeasure.md#82-detecting-thread-pool-starvation-in-production) |
| Which code is blocking? | `dotnet-stack report` twice, 5 s apart, diff. Look for `Monitor.Wait`, `Task.Result`, `Thread.Sleep` | [§3.6](WaysToMeasure.md#36-dotnet-stack--the-2-second-triage) |
| What's pending in a hung process? | `dotnet-dump` → `dumpasync --stats` | [§3.5](WaysToMeasure.md#35-dotnet-dump--sos--the-last-resort-that-always-works) |
| Where is the *waiting* (not the CPU)? | dotTrace **Timeline**, PerfView **Thread Time**. A CPU profile shows nothing — blocked threads emit no samples | [§5.2](WaysToMeasure.md#52-jetbrains-rider--dottrace--dotmemory), [§6.2](WaysToMeasure.md#62-sampling-vs-instrumentation) |
| Does my async change help? | BDN `[ThreadingDiagnoser]` + `[MemoryDiagnoser]` | [§4.4](WaysToMeasure.md#44-diagnosers-worth-knowing) |
| Does it help *under load*? | Load test + counters. **Microbenchmarks cannot show scalability** | [§8.3](WaysToMeasure.md#83-load-generation) |

That last row is the important one. A microbenchmark runs one operation at a time, so it measures
async's *cost* and never its *benefit*. The `/step2/sync-io` vs `/step2/async-io` load test is the
demonstration:

```bash
dotnet run -c Release -f net10.0 -- api            # terminal 1
dotnet-counters monitor -p <pid> System.Runtime Microsoft.AspNetCore.Hosting   # terminal 2

bombardier -c 200 -d 30s "http://localhost:5199/step2/async-io?ms=200"   # terminal 3
bombardier -c 200 -d 30s "http://localhost:5199/step2/sync-io?ms=200"
```

Same per-request latency in isolation; under 200 concurrent connections the async version sustains
throughput while the sync version collapses toward the pool size, with `threadpool-queue-length`
climbing and `cpu-usage` flat. That divergence *is* the lesson, and no benchmark table can contain it.

> **Not measured here.** Every BenchmarkDotNet table in this document is real output from this
> machine. This load comparison is **not** — no load generator or `dotnet-counters` is installed on
> the machine these documents were written on, so the endpoints are verified to run and return
> correct results, but the throughput divergence above is described from established behaviour
> rather than observed. Run it yourself; it is the single most convincing thing in Step 2, and it
> takes about five minutes.

---

## 14. Decision tables

### 14.1 Should this method be async?

| Situation | Answer |
|---|---|
| It performs I/O (network, disk, DB) | **Async.** Always. |
| It awaits something else that's async | **Async.** Don't break the chain. |
| Pure CPU work, short | **Sync.** Async buys nothing and costs a state machine. |
| Pure CPU work, long, in a UI app | Sync method, called via `Task.Run` **by the UI layer** |
| Pure CPU work, long, in ASP.NET Core | **Sync.** You're already on a pool thread. See [Step 3](ParallelismVsConcurrency.md). |
| Library API that might do I/O in some implementations | **Async**, returning `Task`/`ValueTask` |

### 14.2 `Task` or `ValueTask`?

| | `Task` | `ValueTask<T>` |
|---|---|---|
| Public API | ✅ default | ⚠️ only if hot and measured |
| Usually completes synchronously | ⚠️ allocates every call | ✅ this is its purpose |
| Always does real I/O | ✅ | ❌ no benefit, all the constraints |
| Caller may await twice, cache, or compose | ✅ | ❌ **broken** |
| Passed to `WhenAll`/`WhenAny` | ✅ | ❌ needs `.AsTask()` |
| On .NET 11 | ✅ preferred | ⚠️ the case weakens further (§12) |

### 14.3 Review checklist

- [ ] No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` in request-handling code
- [ ] No `async void` outside event handlers
- [ ] No `Task.Run` wrapping I/O in a library
- [ ] `CancellationToken` accepted and plumbed through every async path
- [ ] Every `CancellationTokenSource` / linked source disposed
- [ ] `ConfigureAwait(false)` in libraries; not required in ASP.NET Core apps
- [ ] `TaskCompletionSource` created with `RunContinuationsAsynchronously`
- [ ] No `async` lambdas passed to `Action<T>` parameters (`List.ForEach`, `Parallel.ForEach`)
- [ ] `[EnumeratorCancellation]` on `IAsyncEnumerable` methods taking a token
- [ ] `Task.WhenAll` over a large collection is bounded ([Step 3](ParallelismVsConcurrency.md))
- [ ] Fire-and-forget work moved to `IHostedService` or a `Channel`
- [ ] Any `SetMinThreads` call names the blocking dependency it compensates for

---

## Where this goes next

| Step | Document | Connection |
|---|---|---|
| 1 | [WaysToMeasure.md](WaysToMeasure.md) | The counters and profilers every claim here is verified with |
| 3 | [ParallelismVsConcurrency.md](ParallelismVsConcurrency.md) | §9.3's unbounded fan-out, and what to do about it: `Parallel.ForEachAsync`, `Channel`, `SemaphoreSlim` |
| 4 | [MemoryOptimizationTricks.md](MemoryOptimizationTricks.md) | §11's allocations — and why `Span<T>` cannot cross an `await`, which is the constraint that sends you to `Memory<T>` |

---

## References

- [Async/await best practices — Stephen Cleary](https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)
- [ValueTask — Understanding the Whys, Whats, and Whens](https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/)
- [ConfigureAwait FAQ — .NET Blog](https://devblogs.microsoft.com/dotnet/configureawait-faq/)
- [`ConfigureAwaitOptions` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.configureawaitoptions)
- [`Task.WhenEach` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.wheneach)
- [What's new in .NET 11 — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)
- [Exploring .NET 11 Preview 1 Runtime Async — Laurent Kempé](https://laurentkempe.com/2026/02/14/exploring-net-11-preview-1-runtime-async-a-dive-into-the-future-of-async-in-net/)
- [.NET 11 Runtime Async Performance: What Actually Changes](https://www.c-sharpcorner.com/article/net-11-runtime-async-performance-what-actually-changes/)
