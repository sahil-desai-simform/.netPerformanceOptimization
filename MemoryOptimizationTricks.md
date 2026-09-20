# Step 4 — Memory: Spans, Pooling, and No-Allocation Code

> **Baseline:** .NET 10. `[.NET 8]` / `[.NET 9]` / `[C# 13]` tags mark arrivals.
> **Before this:** [Step 1 — allocation rate vs retained set](WaysToMeasure.md#23-memory-two-independent-problems).
> **Companion code:** [samples/PerfLab/Benchmarks/Step4/](samples/PerfLab/Benchmarks/Step4/), [samples/PerfLab/Api/Step4Endpoints.cs](samples/PerfLab/Api/Step4Endpoints.cs).
> **All numbers here are measured** on an i5-13420H, .NET 10.0.4, `--job short`.
> Raw output: [samples/PerfLab/BenchmarkDotNet.Artifacts/results/](samples/PerfLab/BenchmarkDotNet.Artifacts/results/).

## The one-sentence version

**Allocation is a pointer bump; *collection* is what costs.** The goal is not fewer objects — it's
fewer *surviving* objects. For most code the right amount of effort here is none, which the last
section is about.

## The rules

1. **Delete the work first.** A cached result or a fixed N+1 beats every technique below.
2. **Only the hot path.** If it isn't in a profile, you are adding risk for nothing.
3. **Take `ReadOnlySpan<char>`, not `string`, in parsing helpers.** One habit, more allocations
   removed than any other single change. `string` converts implicitly, so callers don't change.
4. **`Span<T>` for sync code, `Memory<T>` at async boundaries.** A span cannot cross an `await`.
5. **Pool above ~85 KB, never below ~1 KB.** Under the LOH threshold you lose to a gen0 bump.
6. **Always `try/finally` around a rent, and never return twice.** A double-return is a silent,
   load-dependent data-corruption bug.
7. **`stackalloc` only with a constant size.** A size from input is an uncatchable process kill.
8. **Presize every collection whose count you know.** `List<T>` doubling costs ~2N and log₂(N) copies.
9. **Mark every lambda `static`.** Not for speed — so the compiler *errors* when one captures.
10. **Make every immutable struct `readonly`.** Otherwise you pay a defensive copy per member access.
11. **Prefer `TensorPrimitives` over hand-written SIMD.** It won every size tested, including 2× over
    hand-written at the small one.
12. **Assert identical output as part of the measurement.** An optimization that changes behaviour
    isn't one.

## Symptom → fix

| What you observe | Likely cause | What to do |
|---|---|---|
| High `alloc-rate`, `time-in-gc` fine, p99 fine | Gen0 churn that costs nothing | **Leave it alone.** Gen0 garbage is nearly free |
| `gc-heap-size` floor rises after every gen2 | Retention, not allocation | Find what survives — a cache or a pool, not a `Substring` |
| Allocation profile is all `string` | `Substring` / `Split` / interpolation in a loop | Spans and `TryFormat` |
| Allocation profile is all `byte[]`, sizes large | Un-pooled buffers over the LOH threshold | `ArrayPool` |
| Profile shows types you never wrote (`<>c__DisplayClass`, boxes) | Hidden allocations | The pattern table |
| Pooling made it *slower* | Buffers too small, or measured cold | "When pooling hurts" |
| Allocations fell but time didn't | You optimized the wrong column | Normal. Read the rubric |
| Gen2 grew after you added a pool | Traded churn for retention | Also normal — and often the worse side of the trade |

## 1. Spans and stack buffers

A `Span<T>` is a **view** — a pointer and a length — over memory you already have. Slicing copies
nothing.

```csharp
string part = csv.Substring(0, 5);            // allocates a new 5-char string
ReadOnlySpan<char> view = csv.AsSpan(0, 5);   // allocates nothing at all
```

It's a `ref struct`, so it is **stack only** — no class fields, no boxing, no crossing an `await`, no
`yield`, no lambda capture. All of those are compile-time errors, never runtime bugs; the
restrictions are the price of pointer performance with bounds checking and no `unsafe`.

**Take `ReadOnlySpan<char>` rather than `string` in parsing helpers.** A `string` converts implicitly,
so callers are unaffected — but a caller who already has a span no longer has to allocate one to
call you.

```csharp
public static int SumCsv(ReadOnlySpan<char> line)    // not (string line)
{
    int sum = 0, comma;
    while (!line.IsEmpty)
    {
        comma = line.IndexOf(',');
        sum += int.Parse(comma < 0 ? line : line[..comma]);   // span overload
        line = comma < 0 ? default : line[(comma + 1)..];     // a view, not a copy
    }
    return sum;
}
```

### `stackalloc` — constant sizes only

Stacks are ~1 MB. A `stackalloc` sized from input is a stack overflow waiting to happen, and a stack
overflow **cannot be caught** — it kills the process.

```csharp
const int StackLimit = 256;

Span<char> buffer = length <= StackLimit
    ? stackalloc char[StackLimit]      // constant, NOT stackalloc char[length]
    : new char[length];                // or ArrayPool for larger/hotter cases
```

`[SkipLocalsInit]` skips the zeroing of stack locals — worth it for a large `stackalloc` in a hot
method, but **only** when you write every byte before reading it. Otherwise you are reading whatever
was on the stack, which is an information-disclosure bug.

### `Memory<T>` at async boundaries

A span can't cross an `await`, and async I/O is exactly where you want a buffer abstraction:

```csharp
public async Task ProcessAsync(Memory<byte> buffer, CancellationToken ct)
{
    int read = await _stream.ReadAsync(buffer, ct);   // Memory survives the await
    Parse(buffer.Span[..read]);                       // take .Span at the point of use
}
```

**Rule of thumb:** `Memory<T>` in fields and async signatures; `.Span` inside the synchronous method
that actually touches the bytes. `IMemoryOwner<T>` from `MemoryPool<T>.Shared` when a buffer's
lifetime spans several async calls and `try/finally` around a rent is awkward.

## 2. Strings, parsing, and formatting

Strings are immutable, so every `Substring`, `Split`, `Trim`, and `+` produces a new one. In a
parsing hot path this is usually the single largest source of gen0 pressure.

**Every allocation-free string pair won, and three went to literal zero.** Over a 93-character CSV line:

| Pair | Speedup | Allocation |
|---|---|---|
| `string.Split` → manual span scan | **2.7×** (79.8 → 29.2 ns) | 392 B → **0** |
| `string.Split` → `MemoryExtensions.Split` into ranges | 1.8× (79.8 → 45.0 ns) | 392 B → **0** |
| `Substring` + `int.Parse` → `AsSpan` slice + `int.Parse` | 1.5× (22.5 → 15.1 ns) | 32 B → **0** |
| interpolation → `string.Create` with a stack buffer | 2.6× (24.2 → 9.5 ns) | 96 B → 56 B |
| `IndexOfAny(char[])` → `SearchValues` | **none** (54.1 vs 54.8 ns) | both 0 |

```csharp
string s = value.ToString();              // allocates a string just to read it
Span<char> buf = stackalloc char[32];     // doesn't
value.TryFormat(buf, out int written);

int a = int.Parse(text.Substring(0, 5));  // allocates the substring
int b = int.Parse(text.AsSpan(0, 5));     // doesn't
```

`ISpanFormattable` / `ISpanParsable<T>` are implemented by every BCL primitive, `DateTime`,
`TimeSpan`, `Guid`, and `decimal`. Implement them on your own types and they compose with all of it.

Three specifics worth knowing:

- **`SearchValues` must be `static readonly`.** `SearchValues.Create(",;=|")` precomputes a vectorized
  lookup table; building one per call costs far more than the search it accelerates.
- **`string.Create` needs an *exact* length.** Anything you leave unwritten stays `'\0'` in the
  finished string — silent corruption, not an exception.
- **UTF-8 end to end skips transcoding.** `"name"u8` is a compile-time `ReadOnlySpan<byte>` over
  static data. .NET strings are UTF-16; JSON and HTTP are not.

### When "fewer allocations" made it slower

A `string.Create` CSV formatter allocated 30% less than `StringBuilder` and ran 47% *slower*. The
obvious fix — `TryFormat` into a stack buffer to size it without allocating — did exactly what it
promised and **still lost**:

| Approach | Time | Allocated |
|---|---|---|
| **ArrayPool buffer, single pass** | **32.0 µs** | **37.2 KB** |
| `StringBuilder` (baseline) | 40.6 µs | 99.8 KB |
| `string.Create`, `TryFormat` sizing | 55.4 µs | 37.2 KB |
| `string.Create`, `ToString` sizing | 59.7 µs | 71.1 KB |

The `TryFormat` fix cut allocations 48% and time only 7%, because the allocation was never the
problem: **`string.Create` needs an exact length, so it formats every value twice** — once to
measure, once to write. The shape that wins has no sizing pass at all: **format once into an
over-allocated pooled buffer, then copy out the used portion.** Reach for that before `string.Create`
whenever the exact length is awkward to compute.

## 3. Hidden allocations

The allocations you didn't write. None of these *look* like allocations, which is the point.

| Pattern | Allocates | Instead |
|---|---|---|
| `object o = 42;` | Box, 24 B | Generics, or stay in the value type |
| `n => n > threshold` (captures) | Display class + delegate | `static n => n > 500`, or a loop |
| `Enum.ToString()` | Reflection-backed string | `switch` returning literals |
| `params int[]` | Array per call | `params ReadOnlySpan<int>` `[C# 13]` |
| `string.Format` / interpolation | Boxes args, allocates result | Interpolated string handlers, `TryFormat` |
| LINQ chains in hot loops | Enumerator + delegate per operator | A single pass |
| `IEnumerable<T>` parameter | Boxes struct enumerators | `List<T>`, `T[]`, `Span<T>` |
| `yield return` | State machine object | Return a collection or span |
| `Tuple<T1,T2>` | Class | `ValueTuple` — `(a, b)` |
| `new EventHandler(M)` in a loop | Delegate per call | Cache it |
| `async` state machine on suspension | Box | [Step 2 — where async allocations come from](AsyncVsSync.md#11-where-async-allocations-come-from) |

Measured as pairs — the idiomatic version against its allocation-free equivalent:

| Pair | Speedup | Allocation |
|---|---|---|
| Boxing → value type | **7.1×** (2,078 → 292 ns) | 24,000 B → **0** |
| `Enum.ToString()` → `switch` | **6.3×** (750 → 120 ns) | 2,400 B → **0** |
| `params int[]` → `params ReadOnlySpan` | **3.7×** (392 → 107 ns) | 4,000 B → **0** |
| LINQ chain → single pass | **2.1×** (971 → 459 ns) | 104 B → **0** |
| Capturing → `static` lambda | *no measurable difference* | 24 B → **0** |

**`static` lambdas are not a speed optimization** — 314.7 vs 305.3 ns, with errors of ±157 and ±56.
The saving is 24 bytes. Do it anyway, for a different reason than usually given: the compiler
*errors* if the lambda accidentally captures, turning a silent per-call allocation into a build
failure. Apply it everywhere it compiles; the places it doesn't compile are the ones worth reading.

`params ReadOnlySpan<T>` `[C# 13]` is worth calling out because overload resolution prefers it when
both exist — adding it to an existing API removes allocations at every call site without touching a
single caller.

## 4. Pooling

```csharp
byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
try
{
    // Rent returns an array AT LEAST this long — usually longer.
    // Always slice. Using buffer.Length is a classic bug.
    Process(buffer.AsSpan(0, size));
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```

| Rule | Why |
|---|---|
| **Always `try/finally`** | An exception between rent and return permanently shrinks the pool |
| **Never return twice** | Two renters get the same array. Silent, catastrophic, load-dependent |
| **Never keep a reference after returning** | Same failure, from the other side |
| **Slice to the requested length** | The array is longer than you asked for |
| **`Return(buffer, clearArray: true)` for sensitive data** | The next renter sees what you left |

**The LOH threshold is a cliff, not a slope — and below it, pooling is a pessimisation.**

| Size | `new byte[n]` | `ArrayPool` rent/return |
|---|---|---|
| 128 B | **6.96 ns** · 152 B | 7.66 ns · 0 B |
| 1 KB | 39.77 ns · 1,048 B | **13.70 ns** · 0 B |
| 128 KB | 48,023 ns · 131 KB | **1,987 ns** · 0 B |

At 128 bytes `ArrayPool` is 10% *slower* than `new byte[128]` (and `clearArray: true` makes it 48%
slower) — you will not beat a gen0 pointer bump for small short-lived buffers. Between 64 KB and
128 KB, `new` goes from 3.05 µs to 48.0 µs: 16× the cost for 2× the bytes, because past 85,000 bytes
allocations land on the LOH and are collected with gen2. `stackalloc` wins outright at 128 B
(3.80 ns), which is what the `stackalloc`-with-a-heap-fallback pattern above is for.

### When pooling hurts

- **Pooled objects survive.** They're promoted to gen2 and stay. You have traded an allocation-rate
  problem for a retained-set problem — and not always for the better side of it.
- **Rent/return is real work.** See the 128 B row above.
- **The bugs are severe.** A double-return corrupts data under load and reproduces nowhere else.

**Pool when** buffers are large (approaching or past 85,000 bytes), rented frequently, and scoped
clearly. **Don't pool a 64-byte buffer.**

Other pools worth knowing: `ObjectPool<T>` (`Microsoft.Extensions.ObjectPool`) for expensive-to-build
objects like `StringBuilder`; `RecyclableMemoryStream` as a `MemoryStream` replacement that avoids
LOH churn and the double-buffer on `ToArray`.

## 5. Structs, collections, serialization

- **`readonly struct`, or pay a defensive copy** at every member access through a `readonly` field or
  an `in` parameter. **`in` on a non-`readonly` struct is actively harmful** — the copy *and* the
  indirection. Use `in` for structs over ~16 bytes.
- **Struct enumerators box the moment you go through `IEnumerable<T>`.** `List<T>`, arrays,
  `Dictionary<K,V>`, and `Span<T>` all `foreach` without allocating. This is why a parameter typed
  `IEnumerable<T>` allocates where `List<T>` doesn't.
- **`[InlineArray(8)]`** `[.NET 8]` — a fixed-size array inline in a struct, no heap, no `unsafe`.
  It's the mechanism behind `params ReadOnlySpan<T>`.
- **Presize:** `new List<T>(n)`, `new Dictionary<K,V>(n)`, `new StringBuilder(len)`.
- **`CollectionsMarshal`** gives a reference into the collection's internals — `AsSpan(list)` to
  iterate without an enumerator, `GetValueRefOrAddDefault` for one hash lookup instead of two.
  **Never add or remove while holding one**; the backing array may be replaced and it dangles.
- **`FrozenDictionary` / `FrozenSet`** `[.NET 8]` for read-only maps built once at startup — lookup
  is never worse, construction is paid once. But
  [Step 3 measured no advantage at all](ParallelismVsConcurrency.md#73-frozendictionary-net-8) on the
  shape tested. Verify on your keys.
- **Source-generate JSON** (`[JsonSerializable]` + `JsonSerializerContext`) — no reflection,
  trimmable, AOT-safe. On the hot path, `Utf8JsonWriter` with `"id"u8` literals, and `Utf8JsonReader`
  rather than materializing a DTO you immediately discard.

## 6. SIMD

One instruction, many values, one core. Orthogonal to [Step 3](ParallelismVsConcurrency.md)'s task
parallelism, and they compose.

**Start at `TensorPrimitives` and stop there if it has your operation.** Summing a `float[]`:

| Approach | 1 K elements | 1 M elements |
|---|---|---|
| Scalar loop | 459 ns | 513 µs |
| `Vector<T>` (portable width) | 70.7 ns | 101 µs |
| `Vector256<T>` (fixed + fallback) | 71.9 ns | 98.9 µs |
| **`TensorPrimitives.Sum`** `[.NET 9]` | **35.6 ns** | **85.6 µs** |

It won by **2× over hand-written SIMD at the small size** — it is better at exactly the parts that
are tedious and easy to get wrong: tail handling, per-iteration slice overhead, instruction-set
selection. `Vector<T>` and `Vector256<T>` were indistinguishable because `Vector<float>.Count` is
already 8 here, so the fixed-width version bought nothing and cost an `IsSupported` fallback path.

If you do write it by hand: **always handle the tail** (`length % width` elements remain — a
correctness bug, not a performance one), **always provide a fallback** for fixed-width paths, and
don't assume `Vector512` is better, since some CPUs downclock under AVX-512.

## 7. What .NET 10 does for free

Every other technique here is you doing work. This one is **getting out of the way**: .NET 10's
escape analysis stack-allocates objects it can prove don't outlive the method, with no change to how
the code reads. Same source, both runtimes:

| Shape | .NET 8 → .NET 10 | Speedup |
|---|---|---|
| Small array, stays local | 40 B → **0 B** | 2.2× |
| Object via non-escaping struct field | 40 B → **0 B** | 4.6× |
| **Array via `IEnumerable`** | 32 B → **0 B** | **2.3×** |
| Delegate, stays local | 88 B → 24 B | 1.1× |
| *Small array, escapes into a field* | *40 B → 40 B* | *none* |
| *Delegate, escapes via a field* | *88 B → 88 B* | *none* |

The escaping twins are the control: if .NET 10 had simply gotten faster at allocating, both halves of
each pair would have moved.

**The `IEnumerable` row is the one that changes what you write.** On .NET 8, iterating an array
through `IEnumerable<int>` cost 2.4× more than indexing it directly (94.7 vs 39.8 ns) — the
abstraction penalty people write awkward code to avoid. On .NET 10 it is **41.4 vs 42.3 ns:
statistically identical to direct indexing, and 0 B.** Stop contorting code to dodge it. (The
delegate row reached 24 B, not 0: the `Func` is stack-allocated, its closure display class isn't.)

Four rules follow, and the second reverses advice you have probably been given:

1. Keep short-lived arrays **small and fixed-size** (`[1, 2, 3, 4]`, not `new int[n]`).
2. **Don't cache a temporary in a field** "to avoid re-creating it" — on .NET 10 that defeats the
   analysis and is now actively counterproductive.
3. Keep helper methods small enough to inline; a big helper blocks stack allocation in its *caller*.
4. Use `static` lambdas — a capture is a reference from a heap object.

A `0 B` row tells you it happened; the disassembly tells you why (no `CORINFO_HELP_NEWSFAST` call),
which is what you need when it stops happening after an innocuous refactor —
[Step 1 — reading the generated assembly](WaysToMeasure.md#91-looking-at-the-generated-assembly).

## 8. Proving it, and when not to bother

**The allocation regression test is the single most valuable thing in this document.** It turns
"don't regress this hot path" into a CI failure, and it is far more stable than any timing assertion:

```csharp
[Fact]
public void Parse_Does_Not_Allocate()
{
    Parse(Sample);      // warm up — measuring cold code measures the JIT

    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 100; i++) Parse(Sample);
    Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
}
```

`GC.GetAllocatedBytesForCurrentThread()` is cheap, precise, and deterministic — no statistical
machinery, no flakiness, no dedicated runner.

| Question | Tool |
|---|---|
| Bytes per operation? | BDN `[MemoryDiagnoser]` → `Allocated` |
| Did it actually reach zero? | A `0 B` row, confirmed with `--disasm` |
| Which call site allocates? | VS **.NET Object Allocation Tracking** ([Step 1](WaysToMeasure.md#51-visual-studio)) |
| Rate or retention? | `gc-heap-size` floor across gen2s |
| Did the service improve? | Load test + `alloc-rate`, `time-in-gc`, p99 |

`/step4/alloc-report?rows=500` measures the naive and pooled endpoints in-process: **74% fewer bytes
with byte-identical output** (82,976 → 21,632 B), and the JSON pair does better still — 107,376 B via
reflection-based `JsonSerializer` against 13,032 B via `Utf8JsonWriter` over a pooled buffer, an
**88% reduction**. Two things about how those were obtained are the point: `identicalOutput` is
**asserted, not assumed**, and the endpoint **warms up first** — called cold, the pooled version
reports *more* allocation, because the first `Rent` has nothing to hand you.

And then the question none of that answers: **did p99 move under load?** Often it doesn't. Gen0
collection of short-lived garbage is very cheap — a real result worth having *before* you ship the
complexity.

### The rubric

Every technique here costs something that doesn't show up in a benchmark table:

| Technique | Real cost |
|---|---|
| `Span<T>` everywhere | `ref struct` lifetime rules; can't cross `await`, `yield`, or a lambda |
| `ArrayPool` | Double-return and use-after-return bugs — load-dependent, silent, severe |
| `stackalloc` | Stack overflow is **uncatchable**; it kills the process |
| `[SkipLocalsInit]` | Reading uninitialized stack memory; information disclosure |
| `MemoryMarshal` / `Unsafe` | You have left the safety guarantees. Memory corruption is possible |
| Hand-written SIMD | Per-architecture paths, tail bugs, hard to review |
| Pooling everything | Trades gen0 churn for gen2 growth — often the worse problem |

**Do it when** the code is on a measured hot path, the allocation is in a profile, and the technique
is one of the cheap ones (`static` lambdas, presizing, `AsSpan` over `Substring`, `FrozenDictionary`).
These have essentially no readability cost.

**Think hard when** it needs pooling, `stackalloc`, or restructuring for `ref struct` lifetimes. Write
the justifying measurement in a comment next to the code, or the next person will "simplify" it back.

**Don't when** the code isn't hot, the allocation isn't in the profile, or you haven't measured. The
gen0 allocator is very good. Most code should allocate freely and be readable.

> The fastest code in your service is the code you deleted. The second fastest is the code that never
> ran because you cached the result. Everything in this document is a distant third.

## Measured surprises

| Result | What it overturns |
|---|---|
| `ArrayPool` is **10% slower than `new byte[128]`** | "Pooling is always a win" |
| `SearchValues` showed **no advantage** over `IndexOfAny` at 3 delimiters | "Always use `SearchValues`" |
| `FrozenDictionary` showed **no advantage** in [Step 3's measurement](ParallelismVsConcurrency.md#73-frozendictionary-net-8) | "Freeze every startup map" |
| `static` lambdas showed **no time difference** | "Static lambdas are faster" — they're safer |
| A hand-written loop **lost** to `Enumerable.Count(predicate)` | "Rewrite LINQ out of hot code" |
| `string.Create` allocated less and ran **slower**, twice | "Fewer allocations means faster" |
| `IEnumerable` iteration now **equals direct indexing** on .NET 10 | The abstraction penalty you code around |
| `TensorPrimitives` beat hand-written SIMD by **2×** | "Intrinsics are faster than the library" |

## Checklist

- [ ] Parsing helpers take `ReadOnlySpan<char>`, not `string`
- [ ] No `Substring` / `string.Split` on a measured hot path
- [ ] Every `stackalloc` uses a compile-time constant size, with a heap fallback above it
- [ ] Every `ArrayPool.Rent` has a `try/finally` and slices to the requested length
- [ ] Nothing is pooled below ~1 KB; everything above ~85 KB is
- [ ] Every lambda is `static` unless it provably must capture
- [ ] Every immutable struct is `readonly`; no `in` on a non-`readonly` struct
- [ ] Collections with a known count are presized
- [ ] No `IEnumerable<T>` parameters on hot paths — `List<T>`, `T[]`, or `Span<T>`
- [ ] JSON is source-generated, not reflection-based
- [ ] Hot paths with a zero-allocation claim have a `GC.GetAllocatedBytesForCurrentThread()` test
- [ ] Every optimization here has its justifying measurement in a comment beside it
- [ ] Output is asserted identical before and after

## Where this goes next

| Step | Document | Why you'd return |
|---|---|---|
| 1 | [WaysToMeasure.md](WaysToMeasure.md) | Verify the change moved the metric that motivated it |
| 2 | [AsyncVsSync.md](AsyncVsSync.md) | Async allocations, and why a span can't cross an `await` |
| 3 | [ParallelismVsConcurrency.md](ParallelismVsConcurrency.md) | SIMD is data parallelism; per-item closures are why parallel loops allocate |

## References

- [Memory and spans — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/)
- [`ArrayPool<T>` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1)
- [`SearchValues` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.searchvalues)
- [`TensorPrimitives` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.tensors.tensorprimitives)
- [Stack allocation and escape analysis in .NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime#stack-allocation)
- [Performance Improvements in .NET 10 — .NET Blog](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
- [System.Text.Json source generation — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
- [C# 13 params collections — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-13.0/params-collections)
