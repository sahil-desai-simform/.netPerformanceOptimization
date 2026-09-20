# Step 2 — Async vs Sync: Using Threads Well

> **Baseline:** .NET 10. `[.NET 8]` / `[.NET 9]` / `[.NET 11]` tags mark arrivals.
> **Before this:** [Step 1 — thread-pool health](WaysToMeasure.md#24-thread-pool-health).
> **Companion code:** [samples/PerfLab/Benchmarks/Step2/](samples/PerfLab/Benchmarks/Step2/), [samples/PerfLab/Api/Step2Endpoints.cs](samples/PerfLab/Api/Step2Endpoints.cs).
> **All numbers here are measured** on an i5-13420H, .NET 10.0.4, `--job short`, except the
> `[.NET 11]` section and the load test, both flagged where they appear.
> Raw output: [samples/PerfLab/BenchmarkDotNet.Artifacts/results/](samples/PerfLab/BenchmarkDotNet.Artifacts/results/).

## The one-sentence version

**Async is about not holding a thread while you wait — it is not about speed.** One blocking call in
a hot path can take down a service whose CPU graph looks perfectly healthy, and no amount of async
machinery tuning matters next to that.

## The rules

1. **Never block on async code.** No `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` in anything
   that serves a request. This is the one that causes outages.
2. **Async is viral, and that's the feature.** Make the caller async rather than bridging.
3. **Never `async void`** outside an event handler whose signature you don't control.
4. **Don't wrap sync work in `Task.Run` in a library.** It frees your thread and occupies another —
   net consumption unchanged, and you've lied to the caller.
5. **Plumb `CancellationToken` through every async path.** In ASP.NET Core it binds to
   `RequestAborted` for free.
6. **`Task` by default; `ValueTask` only when hot, usually-synchronous, and awaited once.** At 0%
   synchronous completion `ValueTask` measured *slower*.
7. **`ConfigureAwait(false)` in libraries; it's a no-op in ASP.NET Core.**
8. **Create every `TaskCompletionSource` with `RunContinuationsAsynchronously`.**
9. **Overlap only genuinely independent work** — but do overlap it. 8.9× at ten operations.
10. **Bound every fan-out.** `Task.WhenAll` over a collection you don't control is a DoS.
11. **Don't elide `await`** in anything with a `try`/`catch`, `using`, or `lock`.
12. **Any `SetMinThreads` call must name the blocking dependency it compensates for.**

## Symptom → fix

| What you observe | Likely cause | What to do |
|---|---|---|
| Latency climbing, **CPU low**, queue growing | Thread-pool starvation | Find the blocking call. This is the signature .NET incident |
| Autoscaler won't scale out while the service is dying | Same — starvation doesn't raise CPU | Scale on `threadpool-queue-length`, not CPU |
| Pods get killed by failing health checks under load | Starvation; health endpoint can't get a thread | Same root cause |
| Deadlock in WinForms/WPF/legacy ASP.NET | `.Result` with a `SynchronizationContext` | `await`, or `ConfigureAwait(false)` throughout |
| "We removed `ConfigureAwait(false)` and nothing broke" | True on ASP.NET Core, and misleading | You dodged the deadlock and kept the starvation |
| Process exits with no logged exception | `async void`, or a fire-and-forget that threw | Return `Task`; move background work to `IHostedService` |
| Allocation profile full of `Task` / state machines | Deep async layering, or `AsyncLocal` | Flatten pass-throughs; see the allocations section |
| Latency scales with N over independent calls | Sequential awaits | `Task.WhenAll` — but bound it |
| A `using` block disposed too early | An elided `await` | Put the `await` back |
| Cancellation logged as errors, `exception-count` inflated | Catching `OperationCanceledException` without a filter | `when (ct.IsCancellationRequested)` |

## 1. What async actually is

**There is no thread.** While an async I/O operation is in flight, no thread is waiting on it. The OS
holds the request; when it completes, a continuation is scheduled onto the pool. Async doesn't make
I/O faster — it frees the thread that would have been standing there.

The compiler rewrites an `async` method into a state machine, and three facts about it are worth
knowing because they explain everything below:

- It is a **struct on the stack** until the method actually suspends.
- It **boxes onto the heap only on real suspension** — that's `+24 B`.
- Every local that lives across an `await` becomes a field on it, so deep chains cost more.

**The fast path is the common path.** In real services a large fraction of awaits complete
synchronously — a cache hit, a buffered `Stream.ReadAsync` that already has the bytes, a `Channel`
with a queued item. Those never suspend, never box, and never schedule a continuation, which is why
"async overhead" as a single number is meaningless.

## 2. Thread-pool starvation, and why it isn't self-healing

.NET has one process-wide pool, shared by ASP.NET Core request handling, `Task.Run`, timers,
`Parallel.*`, and I/O completions.

| | |
|---|---|
| **Minimum threads** | Defaults to `ProcessorCount`. Created immediately on demand |
| **Above the minimum** | The pool **throttles** creation — roughly **one thread per second** while work sits queued |
| **Maximum** | Thousands. Effectively never the binding constraint |

That throttle is usually correct: for CPU-bound work, more threads than cores is slower. It becomes
catastrophic only when threads are **blocked** rather than busy, because then the pool's "throughput
isn't improving" signal is generated by threads doing nothing at all.

```
A blocking call holds a pool thread  →  requests queue  →  pool adds ~1 thread/sec
  →  new threads block too  →  queue grows, latency climbs, CPU stays LOW
  →  health checks time out, the pod is killed  →  load shifts to the survivors, which starve faster
```

Two properties make this the signature .NET production incident:

- **It is not self-healing.** Injection is linear in *time*; queue growth is linear in *load*. Under
  real traffic, load wins.
- **CPU looks fine.** Every CPU-based autoscaler declines to help, and every dashboard says the
  servers are healthy. [Step 1 tells you how to see it](WaysToMeasure.md#82-detecting-thread-pool-starvation-in-production).

`ThreadPool.SetMinThreads(200, 200)` genuinely helps — it removes the injection delay — and is a
legitimate **emergency mitigation** for a blocking dependency you cannot remove today. It is not a
fix: every thread reserves stack, context-switching hundreds of blocked threads costs real CPU, it
hides the problem from your metrics, and it scales with concurrency, which is the thing you don't
control. **Treat a `SetMinThreads` call as a TODO naming the blocking call it compensates for.** If
nobody can name the call, that's the finding.

## 3. Sync-over-async, and its mirror image

Calling an async API synchronously — the most damaging pattern in .NET, and the most common.

```csharp
// All four are the same mistake
var user = _api.GetUserAsync(id).Result;
var user = _api.GetUserAsync(id).GetAwaiter().GetResult();
_api.SaveAsync(user).Wait();
var user = Task.Run(() => _api.GetUserAsync(id)).Result;   // the "fix" that isn't
```

Two distinct failure modes. **Deadlock**, wherever a `SynchronizationContext` runs continuations on a
specific thread (WinForms, WPF, legacy ASP.NET): the caller blocks holding the context, the
continuation needs the context to resume, and they wait for each other forever. **ASP.NET Core has no
`SynchronizationContext`**, so that exact deadlock doesn't happen there — which is why "we removed
`ConfigureAwait(false)` and nothing broke" is true and misleading at once.

**Starvation** happens everywhere, ASP.NET Core included. Each blocked call holds a pool thread
hostage for the full duration of the I/O, and the spiral above does the rest.

It is **strictly worse than being synchronous**: you pay the full async machinery, *and* block a
thread anyway, *and* wrap exceptions in `AggregateException` (with `.Result`/`.Wait()`), *and* lose
stack-trace quality. There is no scenario where it's right in request-handling code. It is acceptable
in `Main` before a host is running, in a unit test or design-time factory, and on a proven
non-pool thread.

**Async-over-sync** is the mirror-image mistake — wrapping blocking work in `Task.Run` and calling it
async. This frees the *caller's* thread and occupies a *different* pool thread. **Net thread
consumption is unchanged**; you've relocated the problem somewhere it no longer shows up.

| Where | Verdict |
|---|---|
| In a **library**, exposed as an async API | **Wrong.** You've lied about the cost and taken away the caller's choice. Expose the sync method |
| In a **UI app**, to keep the message loop responsive | **Right.** One thread must not block; offloading is the goal |
| In **ASP.NET Core**, to offload CPU work in a request | **Usually wrong.** Already on a pool thread — see [Step 3](ParallelismVsConcurrency.md) |
| To call blocking code you cannot change, from an async path | **Pragmatic.** Bound it with a `SemaphoreSlim` and write down why |

If a `Task`-returning method has no `await` in it, don't fake one — return `Task.CompletedTask` /
`Task.FromResult(x)` and skip the state machine.

## 4. What async costs when nothing goes wrong

**Suspension costs ~60× more than the machinery.** Against a non-inlined synchronous call:

| Shape | Time | Allocated |
|---|---|---|
| Sync call (baseline) | 0.68 ns | 0 B |
| `async ValueTask`, never suspends | 5.10 ns | 0 B |
| `async Task`, never suspends | 12.4 ns | 144 B |
| Async chain, depth 10, never suspends | 106 ns | 864 B |
| **Async, suspends once** | **730 ns** | 104 B |
| Async chain, depth 10, suspends at the bottom | 1,382 ns | 1,220 B |

Three regimes, not one cost: never suspending is 5–12 ns of bookkeeping; suspending once is ~730 ns
of scheduling a continuation onto the pool; a deep chain that suspends is ~1,382 ns because every
frame resumes separately. **Optimizing the machinery when your awaits actually suspend is optimizing
the wrong 2%.**

**Chain depth compounds even with no suspension anywhere.** Ten sync frames cost 3.6 ns — the JIT
inlines most of it away. Ten async frames cost 106 ns and 864 B, because each gets its own state
machine and its own `Task`. That is the strongest argument against gratuitously deep async layering.

**And the allocation numbers are not what you'd guess:** "never suspends" allocates **144 B** while
"suspends once" allocates only **104 B**. Allocation tracks **how many `Task` objects get created**,
not whether a suspension happened — the first awaits `Task.FromResult(v)` (one `Task`, 72 B) plus its
own `Task` (72 B); the second awaits `Task.Yield()`, which allocates no `Task` at all, and pays 24 B
for the boxed state machine instead. So "avoid suspension to avoid allocation" is the wrong lesson.
The right one: **avoid creating `Task` objects you immediately await.**

## 5. `ValueTask` vs `Task`

`ValueTask<T>` is a struct holding **either** the result directly **or** a reference to a pending
operation, so synchronous completion allocates nothing. **The whole argument is one table, and it
runs in both directions** — 100 lookups per operation, swept by cache hit rate:

| Synchronous completion | `Task` | `ValueTask` | Verdict |
|---|---|---|---|
| **100%** | 1,038 ns · 6,624 B | **492 ns · 72 B** | **2.1× faster, 99% less** |
| 50% | 44,806 ns · 8,328 B | 45,406 ns · 5,783 B | Times indistinguishable, 31% less allocated |
| **0%** | **76,146 ns · 10,576 B** | 79,053 ns · 11,384 B | **4% slower, 8% more** |

That last row is why "just use `ValueTask` everywhere" is wrong. When every await suspends there is
no allocation to avoid, and you pay for the struct on top of the `Task` you allocate anyway — while
taking on every constraint below.

| Rule | What breaks if you don't |
|---|---|
| **Await it exactly once** | The second await may observe a different operation's result, or throw |
| **No `.Result` / `.GetAwaiter().GetResult()` unless `IsCompleted`** | Undefined behaviour; no blocking-wait support |
| **Never store or cache it** | The underlying source gets recycled out from under you |
| **Never pass it to `WhenAll`/`WhenAny`** | Those need `Task`. `.AsTask()` allocates — if you always do that, just return `Task` |

These exist because a `ValueTask` may wrap a **pooled, reused** `IValueTaskSource` that gets handed
to another operation the moment yours completes. The failures are **silent, timing-dependent, and
load-dependent** — they pass tests and fail in production.

**Use `ValueTask<T>` only when all three hold:** the method is measurably hot, it completes
synchronously most of the time, and callers await it directly exactly once. **Otherwise `Task`** —
public APIs, anything composable, anything that always does real I/O.

## 6. `ConfigureAwait` and cancellation

`await` captures the current `SynchronizationContext` and resumes there; `ConfigureAwait(false)` says
"resume anywhere."

| Context | Has a SynchronizationContext? | Need `ConfigureAwait(false)`? |
|---|---|---|
| **ASP.NET Core** / console / worker | No | **No.** A no-op — noise for nothing |
| **WinForms / WPF / MAUI** | Yes | **Yes** in non-UI code, or you serialize everything through the UI thread |
| **Legacy ASP.NET (System.Web)** | Yes | **Yes** — omitting it is the classic deadlock |
| **A library** | *Unknown* | **Yes.** You don't control where you run. This is why the rule exists |

The rule reduces to: **libraries always; ASP.NET Core apps never need to.**
`[.NET 8]` adds two genuinely new options — `ConfigureAwaitOptions.SuppressThrowing` awaits
completion without caring whether it failed (better than `try { await t; } catch { }`, which also
swallows cancellation), and `ForceYielding` guarantees the continuation is scheduled rather than
inlined, which matters when a synchronous completion would otherwise run it while you hold a lock.

**`CancellationToken` is a parameter, not a global.** In ASP.NET Core it binds to
`HttpContext.RequestAborted` automatically:

```csharp
app.MapGet("/report", async (ReportService svc, CancellationToken ct) =>
    await svc.BuildAsync(ct));      // client gives up → so do we
```

Without it, a client that times out and retries leaves the original work running — under retry storms
load *increases* as clients give up.

```csharp
// .NET 6+: no CTS to dispose, no linked-token bookkeeping, no leaked timer
string body = await _http.GetStringAsync(url).WaitAsync(TimeSpan.FromSeconds(5), ct);

// When you do need a linked source, dispose it — an undisposed CTS with a timer is a real leak
using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownToken);
```

**Catch cancellation with a filter.** Without the `when`, you also swallow cancellations from
somewhere else — an internal timeout, a dependency's own token — turning a real failure into a clean
exit:

```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
}
```

## 7. Overlapping independent work

Async's one genuine *latency* win. N independent 5 ms operations:

| Operations | Sequential | `Task.WhenAll` |
|---|---|---|
| 2 | 29.8 ms · 704 B | 13.5 ms · 896 B |
| 10 | 132.1 ms · 2,816 B | **14.8 ms** · 3,168 B |

Arithmetic, not subtle: sequential scales with N, overlapped stays flat. At ten operations that's
**8.9×**. Overlapping is not free — `WhenAll` allocates the `Task[]` and the combined task, 12–27%
more — but that is a very cheap price for removing 89% of the latency. (`Task.Delay(5)` actually
waits ~13–15 ms here; Windows timer resolution is ~15.6 ms, so read the ratios, not the absolutes.)

```csharp
// Sequential: each await completes before the next starts. 3 × latency.
var a = await GetAAsync();  var b = await GetBAsync();  var c = await GetCAsync();

// Overlapped: all three start, then we wait once. ~1 × latency.
Task<A> ta = GetAAsync();  Task<B> tb = GetBAsync();  Task<C> tc = GetCAsync();
await Task.WhenAll(ta, tb, tc);
var (a, b, c) = (ta.Result, tb.Result, tc.Result);   // safe: WhenAll guarantees completion
```

**Only overlap genuinely independent work.** If B needs A's result, sequential isn't a bug, it's the
data dependency.

Two things `WhenAll` does that surprise people: it rethrows only the **first** exception (get them
all via `catch { throw all.Exception!; }` on the task itself), and it waits for *every* task even
after one fails.

**Unbounded fan-out is a different bug.** `Task.WhenAll` has **no concurrency limit**:

```csharp
// 10,000 concurrent HTTP calls. Not "fast" — a self-inflicted denial of service.
await Task.WhenAll(items.Select(i => _http.GetAsync(i.Url)));
```

You will exhaust the `HttpClient` connection pool, overwhelm the downstream service, and hold every
operation's memory simultaneously. Bounded concurrency is [Step 3](ParallelismVsConcurrency.md)'s
subject; the short answer is `Parallel.ForEachAsync` with a `MaxDegreeOfParallelism`.
`Task.WhenEach` `[.NET 9]` handles results in completion order rather than waiting for the slowest,
but is still unbounded — pair it.

## 8. Pitfalls

**`async void`** — the caller cannot await it, an exception is rethrown on the pool and **crashes the
process**, and it's invisible to testing. The only legitimate use is an event handler whose signature
you don't control. Enable the analyzer and treat it as an error.

**Fire-and-forget** — `_ = SendEmailAsync(user);` discards the *failure*, not just the result. No
retry, no log, no metric, and the work may be killed mid-flight at shutdown. If it should outlive the
request, give it somewhere to live: an `IHostedService`, a `Channel<T>` with a consumer, or a real
queue — all three give you error handling, backpressure, and graceful shutdown.

**`TaskCompletionSource` without `RunContinuationsAsynchronously`** — by default `SetResult(x)`
synchronously executes whatever the awaiter does next, on *your* thread, possibly while you hold a
lock, possibly recursing. Deadlocks and stack overflows that are very hard to attribute:

```csharp
var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
```

**`async` lambdas where an `Action<T>` is expected** — `List<T>.ForEach` and `Parallel.ForEach` take
`Action<T>`, so an `async` lambda binds as **`async void`**. The loop completes immediately, the work
runs unobserved, exceptions crash the process. Use `Task.WhenAll(items.Select(ProcessAsync))` or
`Parallel.ForEachAsync`.

**`IAsyncEnumerable` without `[EnumeratorCancellation]`** — the token the caller supplies via
`.WithCancellation(ct)` never reaches your method body, and cancellation silently does nothing.

### Eliding `await`: the saving is real, and mostly not worth it

| Shape | Time | Allocated |
|---|---|---|
| `return await Inner()` | 11.9 ns | 144 B |
| `return Inner()` (elided) | **3.7 ns** | **72 B** |
| `return await Inner()`, 3 frames | 29.3 ns | 288 B |
| `return Inner()`, 3 frames (elided) | **3.7 ns** | **72 B** |

It **compounds per frame** — awaited grows to 29.3 ns / 288 B across three wrappers while elided
stays flat, because elided frames add literally nothing; they pass the same `Task` along. **And you
should still mostly not do it**, because the behavioural changes are not small:

| | `return await Inner()` | `return Inner()` |
|---|---|---|
| Argument-validation exception | Captured in the returned `Task` | **Thrown synchronously**, before the caller has a Task |
| `using` around the call | Disposes after the work completes | **Disposes immediately**, while work is still running |
| `try`/`catch` in this method | Catches | **Never runs** |
| Stack trace on failure | Includes this frame | This frame is missing |

The `using` row is the dangerous one — it silently operates on a disposed resource, and because it's
timing-dependent it passes tests. **Elide only in a thin pass-through with no `try`/`catch`, no
`using`, and no `lock`.**

## 9. Where async allocations come from

Three independent sources, routinely blamed on each other:

| Source | When you pay | Cost |
|---|---|---|
| **The returned `Task`** | Every async call not served from a cache | 72 B |
| **The state-machine box** | Only on real suspension | +24 B (72 → 96) |
| **`ExecutionContext` capture** | Only when the context isn't empty | +72 B (96 → 168) |

The state machine is a **struct on the stack** until the method suspends, which is why "completes
synchronously" costs 72 B and "suspends" costs 96 B.

**`AsyncLocal` is not "a bit of overhead" — it defeats an optimization.** With an empty
`ExecutionContext` the runtime skips the capture entirely (96 B). Put **one value** in it and every
await in the whole call graph starts capturing and restoring: 168 B, a **75% increase**. This matters
in ASP.NET Core because you may be using `AsyncLocal` without having written any —
`IHttpContextAccessor` and `Activity` (distributed tracing) both flow through it. A real feature with
a real price, and this is the price.

**`Task.Run` costs the same as suspending** — 983 ns wrapping trivial sync work, against 1,027 ns for
actually suspending. They're the same thing: both schedule a continuation onto the pool. `Task.Run`
is not a cheap way to "make something async"; it is the full price of asynchrony for work that didn't
need it.

(`ExecutionContext.SuppressFlow()` is not an optimization to reach for. It silently breaks ambient
state other things depend on, and it measured *worse* than the `AsyncLocal` case it was meant to
beat. Ruled out.)

## 10. `[.NET 11]` Runtime async

> **Not verified locally.** This machine has SDK 10; the figures below are from published benchmarks,
> cited at the end. Direction and order of magnitude, not numbers measured here.

.NET 11 replaces compiler-generated state machines with **runtime-managed suspension**, hoisting only
locals that actually live across a suspension. Reported: an async method that never suspends drops to
**~20× less overhead** and **0 B/op**; ThreadPool and `TaskCompletionSource` continuations get
**3–4× faster**; the deep-chain shape above improves **~7.4×**; and stack traces lose their
state-machine frames.

It **weakens the case for `ValueTask`** — its main win is avoiding allocation on synchronous
completion, which runtime async gives you on `Task` too, while `ValueTask`'s sharp edges remain.
Prefer `Task` in new code unless you have a .NET 10 measurement saying otherwise.

**What it does not change: everything in sections 2, 3 and 4.** Blocking a pool thread is still
fatal, the injection rate is unchanged, sync-over-async still starves. Runtime async makes correct
async cheaper — it does not make incorrect async safe. If you take one thing from this section, take
that.

## 11. Measuring async

| Question | Tool |
|---|---|
| Is the service starving *right now*? | `dotnet-counters` → `threadpool-queue-length` high, `cpu-usage` low ([Step 1](WaysToMeasure.md#82-detecting-thread-pool-starvation-in-production)) |
| Which code is blocking? | `dotnet-stack report` twice, 5 s apart, diff. Look for `Monitor.Wait`, `Task.Result`, `Thread.Sleep` |
| What's pending in a hung process? | [`dotnet-dump`](WaysToMeasure.md#35-dotnet-dump--sos--the-last-resort-that-always-works) → `dumpasync --stats` |
| Where is the *waiting* (not the CPU)? | [dotTrace **Timeline**, PerfView **Thread Time**](WaysToMeasure.md#52-jetbrains-rider--dottrace--dotmemory). A CPU profile shows nothing — blocked threads emit no samples |
| Does my async change help? | BDN [`[ThreadingDiagnoser]` + `[MemoryDiagnoser]`](WaysToMeasure.md#44-diagnosers-worth-knowing) |
| Does it help *under load*? | [Load test](WaysToMeasure.md#83-load-generation) + counters. **Microbenchmarks cannot show scalability** |

That last row is the important one. A microbenchmark runs one operation at a time, so it measures
async's *cost* and never its *benefit*:

```bash
dotnet run -c Release -f net10.0 --project samples/PerfLab -- api     # terminal 1
dotnet-counters monitor -p <pid> System.Runtime Microsoft.AspNetCore.Hosting

bombardier -c 200 -d 30s "http://localhost:5199/step2/async-io?ms=200"
bombardier -c 200 -d 30s "http://localhost:5199/step2/sync-io?ms=200"
```

Same per-request latency in isolation; under 200 concurrent connections the async version sustains
throughput while the sync version collapses toward the pool size, with `threadpool-queue-length`
climbing and `cpu-usage` flat. That divergence *is* the lesson, and no benchmark table can contain it.

> **Not measured here.** No load generator was installed where these documents were written, so the
> endpoints are verified to run and return correct results, but the throughput divergence is
> described from established behaviour rather than observed. See [samples/README.md](samples/README.md).
> Run it yourself — it is the most convincing thing in Step 2, and it takes about five minutes.

## Measured surprises

| Result | What it overturns |
|---|---|
| `ValueTask` **4% slower, 8% more allocated** at 0% synchronous | "Use `ValueTask` everywhere" |
| "Never suspends" allocates **more** (144 B) than "suspends once" (104 B) | "Suspension is the expensive one" |
| `Task.Run` over trivial sync work costs **the same as suspending** | "`Task.Run` makes it async cheaply" |
| One `AsyncLocal` value raises every await's cost **75%** | "`IHttpContextAccessor` is free" |
| Ten async frames cost **30×** ten sync frames, with no suspension | "Layers are free if nothing awaits" |
| Eliding `await` is **3× faster and flat across frames** | …and still usually the wrong call |
| `ExecutionContext.SuppressFlow()` measured **worse**, not better | "Suppressing flow is an optimization" |

## Checklist

- [ ] No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` in request-handling code
- [ ] No `async void` outside event handlers
- [ ] No `Task.Run` wrapping I/O in a library
- [ ] `CancellationToken` accepted and plumbed through every async path
- [ ] Every `CancellationTokenSource` / linked source disposed
- [ ] `OperationCanceledException` caught with a `when (ct.IsCancellationRequested)` filter
- [ ] `ConfigureAwait(false)` in libraries; not required in ASP.NET Core apps
- [ ] `TaskCompletionSource` created with `RunContinuationsAsynchronously`
- [ ] No `async` lambdas passed to `Action<T>` parameters (`List.ForEach`, `Parallel.ForEach`)
- [ ] `[EnumeratorCancellation]` on `IAsyncEnumerable` methods taking a token
- [ ] `Task.WhenAll` over a large collection is bounded ([Step 3](ParallelismVsConcurrency.md))
- [ ] `await` not elided in any method with `try`/`catch`, `using`, or `lock`
- [ ] Fire-and-forget work moved to `IHostedService` or a `Channel`
- [ ] Any `SetMinThreads` call names the blocking dependency it compensates for

## Where this goes next

| Step | Document | Connection |
|---|---|---|
| 1 | [WaysToMeasure.md](WaysToMeasure.md) | The counters and profilers every claim here is verified with |
| 3 | [ParallelismVsConcurrency.md](ParallelismVsConcurrency.md) | Unbounded fan-out, and what to do about it |
| 4 | [MemoryOptimizationTricks.md](MemoryOptimizationTricks.md) | Why `Span<T>` cannot cross an `await`, which is what sends you to `Memory<T>` |

## References

- [Async/await best practices — Stephen Cleary](https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming)
- [ValueTask — Understanding the Whys, Whats, and Whens](https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/)
- [ConfigureAwait FAQ — .NET Blog](https://devblogs.microsoft.com/dotnet/configureawait-faq/)
- [`ConfigureAwaitOptions` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.configureawaitoptions)
- [`Task.WhenEach` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.wheneach)
- [What's new in .NET 11 — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)
- [Exploring .NET 11 Preview 1 Runtime Async — Laurent Kempé](https://laurentkempe.com/2026/02/14/exploring-net-11-preview-1-runtime-async-a-dive-into-the-future-of-async-in-net/)
