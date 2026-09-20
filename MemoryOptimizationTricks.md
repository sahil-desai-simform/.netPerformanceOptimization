# Step 4 — Memory Optimization: Spans, Pooling, and No-Allocation Code

> **Prerequisites from [Step 1](WaysToMeasure.md):** [§2.3 allocation rate vs retained set](WaysToMeasure.md#23-memory-two-independent-problems),
> [§7.1 the heap layout](WaysToMeasure.md#71-the-layout-youre-reasoning-about),
> [§7.2 GC modes and DATAS](WaysToMeasure.md#72-gc-modes--what-youre-actually-running),
> [§9.1 reading the generated assembly](WaysToMeasure.md#91-looking-at-the-generated-assembly).
> From [Step 2](AsyncVsSync.md): [§11](AsyncVsSync.md#11-where-async-allocations-come-from) — async
> allocations, and the rule that sends you to `Memory<T>` (§4 below).
>
> **Baseline:** .NET 10. `[.NET 8]` / `[.NET 9]` / `[C# 13]` tags mark arrivals.
>
> **Companion code:** [samples/PerfLab/Benchmarks/Step4/](samples/PerfLab/Benchmarks/Step4/) and
> [samples/PerfLab/Api/Step4Endpoints.cs](samples/PerfLab/Api/Step4Endpoints.cs).

---

## The one-sentence version

**Allocation is a pointer bump; *collection* is what costs.** The goal is not fewer objects — it's
fewer *surviving* objects. And for most code the right amount of effort here is none, which §15 is
about.

---

## Table of contents

1. [The cost model](#1-the-cost-model)
2. [`Span<T>` and `ReadOnlySpan<T>`](#2-spant-and-readonlyspant)
3. [`stackalloc`](#3-stackalloc)
4. [`Memory<T>` — when a span won't do](#4-memoryt--when-a-span-wont-do)
5. [Strings without allocating](#5-strings-without-allocating)
6. [Parsing and formatting](#6-parsing-and-formatting)
7. [Pooling](#7-pooling)
8. [Structs](#8-structs)
9. [Hidden allocations](#9-hidden-allocations)
10. [Collections](#10-collections)
11. [Serialization](#11-serialization)
12. [SIMD](#12-simd)
13. [Writing code the JIT can stack-allocate](#13-writing-code-the-jit-can-stack-allocate)
14. [Proving it](#14-proving-it)
15. [When not to do any of this](#15-when-not-to-do-any-of-this)

---

## 1. The cost model

### 1.1 Allocation is nearly free; collection is not

Allocating on the .NET heap is a **pointer bump** — increment the allocation pointer, zero the
memory, return. Tens of instructions. You are not going to meaningfully optimize that.

What costs is **collection**, and the cost is proportional to what **survives**:

| | Gen0 | Gen2 |
|---|---|---|
| Frequency | Constant | Rare |
| Cost model | Proportional to **surviving** objects | Proportional to **total heap** |
| Typical pause | Sub-millisecond | Milliseconds to seconds |
| A garbage object costs | Effectively nothing | — |

The generational hypothesis — most objects die young — is what makes this work. **Your job as an
optimizer is to keep it true.**

### 1.2 Two goals that look alike

| Goal | Fixes | Doesn't fix |
|---|---|---|
| **Allocate less** | Gen0 frequency, `alloc-rate`, CPU spent in GC | A growing working set |
| **Allocate shorter-lived** | Gen2 size, pause duration, working set | Allocation rate |

A cache with a bad eviction policy makes objects *survive*, which is worse than allocating twice as
many that die immediately. This is [Step 1 §2.3](WaysToMeasure.md#23-memory-two-independent-problems)
restated with the fix attached — and it's why §7 spends as much space on when pooling *hurts* as on
how to do it.

### 1.3 The order to work in

1. **Delete work.** The cheapest allocation is the one you don't make. Caching a result, fixing an
   N+1, or not building a string you then throw away beats every technique below.
2. **Fix the hot path only.** [Step 1 §1.2's Amdahl ceiling](WaysToMeasure.md#12-amdahls-law-as-a-sanity-filter)
   applies unchanged.
3. **Then** reach for the techniques here, cheapest-in-readability first: `static` lambdas and
   presized collections before `Span<T>`, `Span<T>` long before `MemoryMarshal`.

---

## 2. `Span<T>` and `ReadOnlySpan<T>`

### 2.1 What it is

A `Span<T>` is a **view** — a pointer and a length — over memory you already have. Slicing does not
copy:

```csharp
string csv = "alpha,beta,gamma";

string part = csv.Substring(0, 5);            // allocates a new 5-char string
ReadOnlySpan<char> view = csv.AsSpan(0, 5);   // allocates nothing at all
```

It can point at an array, a string, `stackalloc` memory, native memory, or a field — with one API
over all of them.

### 2.2 The rules, and why they exist

`Span<T>` is a `ref struct`, which means:

| Rule | Reason |
|---|---|
| **Stack only** — can't be a field of a class, boxed, or in an array | It may point at stack memory; outliving the frame would be a dangling pointer |
| **Can't cross an `await`** | Suspension hoists locals to the heap, and a span can't go there |
| **Can't be used in an iterator (`yield`)** | Same reason — iterators hoist locals |
| **Can't be captured by a lambda** | Closures are heap objects |
| Can't implement an interface / be a generic arg | Relaxed by `allows ref struct` [C# 13] |

These are compile-time errors, not runtime bugs. The restrictions are the price of it being safe:
you get pointer performance with bounds checking and no `unsafe`.

`[C# 13]` `allows ref struct` lets a generic parameter accept them:

```csharp
public static void Process<T>(T buffer) where T : allows ref struct { }
```

### 2.3 In practice

```csharp
// Parsing without allocating: every slice is a view
public static int SumCsv(ReadOnlySpan<char> line)
{
    int sum = 0;
    while (!line.IsEmpty)
    {
        int comma = line.IndexOf(',');
        ReadOnlySpan<char> field = comma < 0 ? line : line[..comma];
        sum += int.Parse(field);                  // int.Parse has a span overload
        line = comma < 0 ? default : line[(comma + 1)..];
    }
    return sum;
}
```

**Take `ReadOnlySpan<char>` rather than `string` in parsing helpers.** A `string` implicitly converts
to a span, so callers are unaffected — but a caller who already has a span no longer has to allocate
one to call you. This one habit removes more allocations than any other single change.

`[C# 14]` adds implicit conversions between `T[]`, `Span<T>`, and `ReadOnlySpan<T>`, so many
`.AsSpan()` calls become unnecessary.

---

## 3. `stackalloc`

```csharp
Span<char> buffer = stackalloc char[64];    // no heap, no GC, freed on return
```

### 3.1 The limit is real

Stacks are ~1 MB by default. `stackalloc` with a size that comes from input is a stack-overflow
waiting to happen — and a stack overflow **cannot be caught**; it kills the process.

The standard defensive pattern:

```csharp
const int StackLimit = 256;

Span<char> buffer = length <= StackLimit
    ? stackalloc char[StackLimit]
    : new char[length];                      // or ArrayPool for larger/hotter cases
```

Note `stackalloc char[StackLimit]`, not `stackalloc char[length]` — a constant size lets the JIT
allocate the frame once rather than dynamically.

### 3.2 `[SkipLocalsInit]`

By default the runtime zeroes stack locals. For a large `stackalloc` in a hot method that's a
measurable memset:

```csharp
[SkipLocalsInit]
public void Parse(ReadOnlySpan<byte> input)
{
    Span<byte> scratch = stackalloc byte[512];   // not zeroed
    // ...
}
```

**Only** when you write every byte before reading it. Otherwise you read whatever was on the stack —
an information-disclosure bug in the worst case. Requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.

### 3.3 Where each option wins

Measured (i5-13420H, .NET 10.0.4, `--job short`). `stackalloc` falls back to `ArrayPool` above 1 KB,
which is the defensive pattern from §3.1:

```
| Method                                      | Size   | Mean          | Ratio | Allocated |
|-------------------------------------------- |------- |--------------:|------:|----------:|
| 'stackalloc (falls back to pool over 1 KB)' | 128    |      3.795 ns |  0.54 |         - |
| GC.AllocateUninitializedArray               | 128    |      6.850 ns |  0.98 |     152 B |
| 'new byte[n]'                               | 128    |      6.964 ns |  1.00 |     152 B |
| 'ArrayPool rent/return'                     | 128    |      7.658 ns |  1.10 |         - |
| 'ArrayPool with clearArray: true'           | 128    |     10.326 ns |  1.48 |         - |
|                                             |        |               |       |           |
| 'ArrayPool rent/return'                     | 1024   |     13.703 ns |  0.35 |         - |
| 'ArrayPool with clearArray: true'           | 1024   |     20.996 ns |  0.53 |         - |
| 'stackalloc (falls back to pool over 1 KB)' | 1024   |     28.210 ns |  0.71 |         - |
| 'new byte[n]'                               | 1024   |     39.769 ns |  1.00 |    1048 B |
|                                             |        |               |       |           |
| 'ArrayPool rent/return'                     | 65536  |    979.007 ns |  0.32 |         - |
| 'stackalloc (→ pool)'                       | 65536  |  1,030.984 ns |  0.34 |         - |
| 'ArrayPool with clearArray: true'           | 65536  |  1,906.373 ns |  0.63 |         - |
| GC.AllocateUninitializedArray               | 65536  |  2,024.369 ns |  0.66 |   65560 B |
| 'new byte[n]'                               | 65536  |  3,051.589 ns |  1.00 |   65560 B |
|                                             |        |               |       |           |
| 'ArrayPool rent/return'                     | 131072 |  1,987.108 ns |  0.04 |         - |
| 'stackalloc (→ pool)'                       | 131072 |  1,995.370 ns |  0.04 |         - |
| 'ArrayPool with clearArray: true'           | 131072 |  3,820.359 ns |  0.08 |         - |
| GC.AllocateUninitializedArray               | 131072 | 47,779.466 ns |  0.99 |  131110 B |
| 'new byte[n]'                               | 131072 | 48,022.534 ns |  1.00 |  131110 B |
```

| Size | Winner | Note |
|---|---|---|
| **128 B** | `stackalloc` (1.9× vs `new`) | **`ArrayPool` LOSES to `new`** here — rent/return bookkeeping exceeds a gen0 bump |
| **1 KB** | `ArrayPool` (2.9×) | The crossover has already happened |
| **64 KB** | `ArrayPool` (3.1×) | `AllocateUninitializedArray` saves 34% — zeroing is now significant |
| **128 KB** | `ArrayPool` (**24×**) | Over the LOH threshold. Everything changes. |

Three things the table settles:

1. **Pooling small buffers is a pessimisation.** At 128 bytes `ArrayPool` is 10% *slower* than
   `new byte[128]`, and `clearArray: true` makes it 48% slower. The gen0 allocator is a pointer
   bump; you will not beat it for small, short-lived buffers.
2. **The LOH threshold is a cliff, not a slope.** 64 KB → 128 KB takes `new` from 3.05 µs to
   **48.0 µs** — 16× for 2× the bytes — and the Gen0/Gen1/Gen2 columns all light up, because LOH
   allocations are collected with gen2. Above 85,000 bytes, pooling stops being an optimization and
   becomes the correct default.
3. **`clearArray: true` roughly doubles the pool's cost** (2.0× at 65 KB). Required for anything
   sensitive — just know you're paying a memset, and price it rather than adding it reflexively.

Note also that `GC.AllocateUninitializedArray` saves nothing on the LOH (47.8 vs 48.0 µs): there,
the cost is the allocation itself, not the zeroing.

---

## 4. `Memory<T>` — when a span won't do

`Span<T>` cannot cross an `await` (§2.2). Async I/O is exactly where you most want a buffer
abstraction. That is what `Memory<T>` is for:

```csharp
public async Task ProcessAsync(Memory<byte> buffer, CancellationToken ct)
{
    int read = await _stream.ReadAsync(buffer, ct);   // Memory survives the await
    Parse(buffer.Span[..read]);                       // get a Span only where you use it
}
```

| | `Span<T>` | `Memory<T>` |
|---|---|---|
| Kind | `ref struct` | regular struct |
| Heap-storable / async / class field | ❌ | ✅ |
| Can wrap `stackalloc` | ✅ | ❌ |
| Access cost | direct | via `.Span` |

**Rule of thumb:** `Memory<T>` at async boundaries and in fields; take `.Span` at the point of use,
inside a synchronous method.

`IMemoryOwner<T>` from `MemoryPool<T>.Shared` gives pooled memory with ownership semantics — useful
when a buffer's lifetime spans several async calls and `try/finally` around a rent is awkward.

---

## 5. Strings without allocating

Strings are immutable, so every `Substring`, `Split`, `Trim`, `ToUpper`, and `+` produces a new one.
In a parsing hot path this is usually the single largest source of gen0 pressure.

Measured over a 93-character CSV line (i5-13420H, .NET 10.0.4, `--job short`):

```
| Method                                        | Mean      | Error     | Ratio | Allocated |
|---------------------------------------------- |----------:|----------:|------:|----------:|
| string.Split                                  | 79.816 ns | 17.165 ns |  1.00 |     392 B |
| 'MemoryExtensions.Split into ranges [.NET 8]' | 45.038 ns |  6.736 ns |  0.56 |         - |
| 'manual span scan'                            | 29.249 ns | 13.581 ns |  0.37 |         - |
|                                               |           |           |       |           |
| 'Substring + int.Parse'                       | 22.530 ns |  3.131 ns |  0.28 |      32 B |
| 'AsSpan slice + int.Parse'                    | 15.129 ns |  6.416 ns |  0.19 |         - |
|                                               |           |           |       |           |
| 'IndexOfAny(char[])'                          | 54.065 ns | 14.187 ns |  0.68 |         - |
| 'SearchValues [.NET 8]'                       | 54.847 ns | 13.972 ns |  0.69 |         - |
|                                               |           |           |       |           |
| interpolation                                 | 24.245 ns | 16.958 ns |  0.30 |      96 B |
| 'string.Create with a stack buffer'           |  9.468 ns |  3.180 ns |  0.12 |      56 B |
```

| Pair | Speedup | Allocation |
|---|---|---|
| `string.Split` → manual span scan | **2.7×** | 392 B → **0** |
| `string.Split` → `MemoryExtensions.Split` | 1.8× | 392 B → **0** |
| `Substring` → `AsSpan` slice | 1.5× | 32 B → **0** |
| interpolation → `string.Create` | 2.6× | 96 B → 56 B |
| `IndexOfAny` → `SearchValues` | **none** | both 0 |

**`SearchValues` showed no benefit here, and that's worth understanding rather than dismissing.**
54.8 vs 54.1 ns with errors of ±14 — indistinguishable. With only **three** delimiters over a
**93-character** input, `IndexOfAny(a, b, c)` already takes a specialised vectorised path in the
BCL, so there's nothing left to win. `SearchValues` pulls ahead when the needle set is larger (say
8+ characters, where the per-character comparison chain stops fitting in a few SIMD ops) or the
haystack is long. It is still the right default for a fixed set — it is never *worse*, and it scales
where `IndexOfAny` doesn't — but on a small set it is not the free win it's often presented as.

Note also the middle pair: `MemoryExtensions.Split` is allocation-free but only 1.8×, while the
manual scan is 2.7×. The range-based API still writes and reads a `Range` per field. When you're
consuming fields in order anyway, scanning directly is both simpler and faster.

### 5.1 `SearchValues<T>` [.NET 8]

```csharp
// static readonly is NOT optional — this precomputes a vectorized lookup table
private static readonly SearchValues<char> Delimiters = SearchValues.Create(",;=|");

int index = input.IndexOfAny(Delimiters);
```

Constructing a `SearchValues` per call costs far more than the search it accelerates. It is a
startup-time investment that pays back on every subsequent search.

### 5.2 `MemoryExtensions.Split` into ranges [.NET 8]

```csharp
ReadOnlySpan<char> input = line;
Span<Range> ranges = stackalloc Range[8];
int count = input.Split(ranges, ',');

for (int i = 0; i < count; i++)
    Handle(input[ranges[i]]);          // each is a view, not a copy
```

`string.Split` allocates a `string[]` plus one string per field. This allocates nothing.

### 5.3 `string.Create`

One exact-sized allocation, filled through a span:

```csharp
string result = string.Create(length, state, static (span, s) =>
{
    // write exactly `length` chars into span
});
```

**The length must be exact.** You get a span of precisely that size, and anything you leave unwritten
stays `'\0'` in the finished string — silent corruption, not an exception. §6 is about computing that
length without allocating, which is where this technique usually goes wrong.

### 5.4 UTF-8

```csharp
ReadOnlySpan<byte> key = "name"u8;                       // UTF-8 literal, no allocation
writer.WriteString("name"u8, value);                     // no transcoding
value.TryFormat(utf8Destination, out int written);       // IUtf8SpanFormattable [.NET 8]
```

.NET strings are UTF-16; JSON, HTTP, and most wire formats are UTF-8. Working in UTF-8 end to end
skips transcoding entirely. `u8` literals are compile-time constants — `ReadOnlySpan<byte>` over
static data, allocating nothing, ever.

---

## 6. Parsing and formatting

Every `.ToString()` allocates. Every `Parse(string)` requires you to have a string.

```csharp
// Allocates
string s = value.ToString();
int n = int.Parse(text.Substring(0, 5));

// Doesn't
Span<char> buffer = stackalloc char[32];
value.TryFormat(buffer, out int written);
int n = int.Parse(text.AsSpan(0, 5));
```

`ISpanFormattable` / `ISpanParsable<T>` are implemented by every BCL primitive, `DateTime`,
`TimeSpan`, `Guid`, and `decimal`. Implement them on your own types and they compose with everything
above.

### 6.1 Closing the loop on Step 1 — and being wrong about it

[Step 1 §4.3](WaysToMeasure.md#43-reading-the-summary-table) left an unresolved result: a
`string.Create` CSV formatter allocated **30% less** than `StringBuilder` and ran **47% slower**. The
diagnosis was that its sizing pass called `decimal.ToString()` on every record — allocating a string
purely to count its characters.

The obvious fix is §6's subject: `TryFormat` into a stack buffer gives the length with no
allocation. **I predicted it would then win on both columns. It didn't.** Measured (i5-13420H,
.NET 10.0.4, `--job short`, N=1000):

```
| Method                                   | Mean     | Ratio | Allocated | Alloc Ratio |
|----------------------------------------- |---------:|------:|----------:|------------:|
| 'ArrayPool buffer, single pass'          | 32.02 μs |  0.79 |  37.21 KB |        0.37 |
| StringBuilder_                           | 40.60 μs |  1.00 |  99.78 KB |        1.00 |
| 'StringCreate (TryFormat sizing)'        | 55.35 μs |  1.36 |  37.21 KB |        0.37 |
| 'StringCreate (Step 1, ToString sizing)' | 59.69 μs |  1.47 |  71.06 KB |        0.71 |
```

The `TryFormat` fix did exactly what §6 says it does — **allocations fell 48%** (71.06 → 37.21 KB)
and time improved 7%. And the result is *still* 36% slower than the `StringBuilder` it was supposed
to beat.

**So the diagnosis in Step 1 was incomplete.** `ToString()` in the sizing pass was a real allocation
bug, but it was not why the approach was slow. The real cost is **structural: `string.Create` needs
an exact length, so it must format every value twice** — once to measure, once to write. For
`decimal`, formatting is expensive enough that doing it twice costs more than `StringBuilder`'s
single pass with amortised growth, no matter how cheaply you do the measuring.

The approach that wins is the one that **doesn't have a sizing pass at all**: format once into an
over-allocated pooled buffer, then copy out the used portion. That is 21% faster than
`StringBuilder` *and* allocates 63% less — the only row here that beats it on both axes.

Three things worth taking from this:

1. **"Fewer allocations" and "faster" are different columns, again.** Both `string.Create` rows
   allocate less than `StringBuilder`; both are slower than it.
2. **Fixing the thing you measured doesn't mean you fixed the thing that was slow.** The allocation
   in the sizing pass was real, and removing it was not enough, because the sizing pass itself was
   the problem.
3. **The one-pass-into-a-pooled-buffer shape is the general answer** when exact length is awkward to
   compute. Reach for it before reaching for `string.Create`.

> Step 1 said to keep a measurement that contradicts the story and rewrite the prose. This section
> is that rule applied to my own prediction — the plan for this document asserted `TryFormat` "must
> win on both," and the benchmark said otherwise.

---

## 7. Pooling

### 7.1 `ArrayPool<T>`

```csharp
byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
try
{
    // Rent returns an array AT LEAST this long — usually longer.
    // Always slice to what you asked for. Using buffer.Length is a classic bug.
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
| **Never return twice** | Two renters get the same array. Silent, catastrophic, load-dependent. |
| **Never keep a reference after returning** | Same failure, from the other side |
| **Slice to the requested length** | The array is longer than you asked for |
| **`Return(buffer, clearArray: true)` for sensitive data** | The next renter sees whatever you left |

### 7.2 When pooling hurts

Pooling is not free, and it is not always a win:

- **Pooled objects survive.** They are promoted to gen2 and stay there. You have traded an
  allocation-rate problem for a retained-set problem — [§1.2](#12-two-goals-that-look-alike)'s exact
  trade, and not always the better side of it.
- **Rent/return is real work.** For small, short-lived buffers, gen0 allocation genuinely beats it.
  §3's benchmark shows where the crossover is.
- **The bugs are severe.** A double-return is a data-corruption bug that appears under load and
  reproduces nowhere else.

**Pool when:** buffers are large (approaching or past the 85,000-byte LOH threshold), rented
frequently, and the lifetime is clearly scoped. **Don't pool** a 64-byte buffer.

### 7.3 Other pools

| Type | For |
|---|---|
| `ObjectPool<T>` (`Microsoft.Extensions.ObjectPool`) | Expensive-to-construct objects — `StringBuilder`, parser state |
| `RecyclableMemoryStream` | `MemoryStream` replacement; avoids LOH churn and the double-buffer on `ToArray` |
| `MemoryPool<T>.Shared` | `IMemoryOwner<T>` when ownership spans async calls |

---

## 8. Structs

### 8.1 `readonly struct` and defensive copies

```csharp
public readonly struct Point(double x, double y)
{
    public double X { get; } = x;
    public double Y { get; } = y;
}
```

Without `readonly`, the compiler inserts a **defensive copy** every time you access a member through
a `readonly` field or an `in` parameter — because it can't prove the member doesn't mutate. For a
large struct in a hot loop that copy is the whole cost. Make every immutable struct `readonly`.

### 8.2 `in` and `ref readonly`

```csharp
public static double Distance(in Point a, in Point b)    // pass by reference, no copy
```

Worth it for structs larger than ~16 bytes. Below that, by-value is as cheap and simpler. **`in` on
a non-`readonly` struct is actively harmful** — you get the defensive copy at every member access *on
top of* the indirection.

### 8.3 `InlineArray` [.NET 8]

```csharp
[System.Runtime.CompilerServices.InlineArray(8)]
public struct Buffer8<T> { private T _element0; }

var buf = new Buffer8<int>();
buf[3] = 42;
Span<int> span = buf;      // implicit
```

A fixed-size array inline in a struct, with no heap allocation and no `unsafe`. This is the mechanism
behind `params ReadOnlySpan<T>` (§9).

### 8.4 Struct enumerators

`List<T>`, arrays, `Dictionary<K,V>`, and `Span<T>` all have **struct** enumerators, so `foreach` over
them allocates nothing. Assign one to `IEnumerable<T>` and the enumerator boxes:

```csharp
foreach (int x in list) { }                    // struct enumerator, 0 allocations
IEnumerable<int> e = list;
foreach (int x in e) { }                       // boxed enumerator
```

This is why a method taking `IEnumerable<T>` allocates where one taking `List<T>` or `Span<T>` does
not. .NET 10 devirtualizes this for **arrays** specifically ([Step 1 §4.5](WaysToMeasure.md#45-comparing-runtimes--directly-relevant-to-should-we-upgrade)),
which is why that measurement showed 32 B → 0 B.

---

## 9. Hidden allocations

The allocations you didn't write. None of these *look* like allocations, which is the point.

Measured (i5-13420H, .NET 10.0.4, `--job short`). Read as pairs — each is the idiomatic version
against its allocation-free equivalent:

```
| Method                                      | Mean       | Error     | Allocated |
|-------------------------------------------- |-----------:|----------:|----------:|
| 'boxing: object holds an int'               | 2,078.4 ns | 311.15 ns |   24000 B |
| 'no boxing: stay in the value type'         |   292.1 ns |  70.28 ns |         - |
|                                             |            |           |           |
| 'capturing lambda (display class per call)' |   314.7 ns | 157.23 ns |      24 B |
| 'static lambda (delegate cached)'           |   305.3 ns |  55.94 ns |         - |
| 'plain loop (no delegate at all)'           |   436.1 ns | 228.49 ns |         - |
|                                             |            |           |           |
| Enum.ToString()                             |   750.3 ns | 349.49 ns |    2400 B |
| 'switch expression over the enum'           |   119.8 ns |  25.92 ns |         - |
|                                             |            |           |           |
| 'params int[] (array per call)'             |   391.5 ns | 179.85 ns |    4000 B |
| 'params ReadOnlySpan<int> [C# 13]'          |   107.2 ns |   8.34 ns |         - |
|                                             |            |           |           |
| 'LINQ chain (enumerators + delegates)'      |   970.5 ns | 556.11 ns |     104 B |
| 'single pass'                               |   458.8 ns | 493.08 ns |         - |
```

| Pair | Speedup | Allocation |
|---|---|---|
| Boxing → value type | **7.1×** | 24,000 B → 0 |
| `Enum.ToString()` → `switch` | **6.3×** | 2,400 B → 0 |
| `params int[]` → `params ReadOnlySpan` | **3.7×** | 4,000 B → 0 |
| LINQ chain → single pass | **2.1×** | 104 B → 0 |
| Capturing → `static` lambda | *no measurable difference* | 24 B → 0 |

Two of these deserve comment because the table disagrees with the usual framing:

**The `static` lambda pair shows no time difference** (314.7 vs 305.3 ns, with errors of 157 and 56).
The saving is 24 bytes per call — one display-class object — and at this scale the `Count()` call
dominates completely. So `static` lambdas are **not** a speed optimization. Their value is that the
compiler *errors* if the lambda accidentally captures, turning a silent per-call allocation into a
build failure. That's still worth doing everywhere, for a different reason than people usually give.

**The plain loop is the slowest of the three** (436.1 ns), though its error bar overlaps the others.
`Enumerable.Count(predicate)` over an array is well-optimized; replacing it with a hand-written loop
bought nothing here. Rewriting LINQ out of code that isn't hot is exactly the effort §15 says not to
spend.

| Pattern | Allocates | Instead |
|---|---|---|
| `object o = 42;` | Box, 24 B | Generics, or stay in the value type |
| `n => n > threshold` (captures) | Display class + delegate | `static n => n > 500`, or a loop |
| `Enum.ToString()` | Reflection-backed string | `switch` returning literals |
| `params int[]` | Array per call | `params ReadOnlySpan<int>` [C# 13] |
| `string.Format` / interpolation | Boxes args, allocates result | Interpolated string handlers, `TryFormat` |
| LINQ chains in hot loops | Enumerator + delegate per operator | A single pass |
| `IEnumerable<T>` parameter | Boxes struct enumerators | `List<T>`, `T[]`, `Span<T>` |
| `yield return` | State machine object | Return a collection or span |
| `Tuple<T1,T2>` | Class | `ValueTuple` — `(a, b)` |
| `new EventHandler(M)` in a loop | Delegate per call | Cache it |
| `async` state machine on suspension | Box | [Step 2 §11](AsyncVsSync.md#11-where-async-allocations-come-from) |

### 9.1 `static` lambdas

```csharp
items.Where(static x => x.IsActive)     // compiler ERROR if it accidentally captures
```

The `static` modifier on a lambda is the **lowest-cost** item in this document: zero readability
cost, nothing to maintain. Per the measurement above it is not a speed win — its value is that the
compiler *errors* if the lambda accidentally captures, turning a silent per-call allocation into a
build failure. Apply it everywhere it compiles, and the ones that don't compile are the ones worth
looking at.

### 9.2 `params ReadOnlySpan<T>` [C# 13]

```csharp
static int Sum(params ReadOnlySpan<int> values) { }
Sum(1, 2, 3, 4);      // compiler builds an InlineArray on the STACK — no array allocated
```

Overload resolution prefers the span form when both exist, so adding it to an existing API removes
allocations at every call site without touching callers.

---

## 10. Collections

### 10.1 Presize

```csharp
var list = new List<T>(expectedCount);
var dict = new Dictionary<K, V>(expectedCount);
var sb = new StringBuilder(expectedLength);
```

`List<T>` doubles when full, so growing to N allocates ~2N total and produces log₂(N) garbage arrays,
each a copy. If you know the count, say so.

### 10.2 `CollectionsMarshal`

```csharp
// Iterate a List<T> as a span — no enumerator, no bounds check per element
foreach (ref Item item in CollectionsMarshal.AsSpan(list)) { }

// One hash lookup for get-or-add instead of two
ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out bool existed);
count++;
```

These hand you a reference into the collection's internals. **Do not add or remove while holding
one** — the backing array may be replaced and your reference dangles.

### 10.3 `FrozenDictionary` / `FrozenSet` [.NET 8]

```csharp
private static readonly FrozenDictionary<string, Handler> Handlers =
    BuildHandlers().ToFrozenDictionary();
```

Spends extra time at construction to pick an optimal lookup strategy. A safe default for read-only
maps built once at startup — construction cost is paid once and lookup is never worse. But the
size of the win depends on key type and shape; [Step 3 §7.3](ParallelismVsConcurrency.md#73-frozendictionary-net-8)
has a measurement where it showed **no** advantage over `Dictionary`. Verify on your keys.

---

## 11. Serialization

```csharp
// Source generation: no reflection, trimmable, AOT-safe, faster startup
[JsonSerializable(typeof(OrderDto))]
internal partial class AppJsonContext : JsonSerializerContext;

JsonSerializer.Serialize(order, AppJsonContext.Default.OrderDto);
```

For the hot path, write UTF-8 directly:

```csharp
var buffer = new ArrayBufferWriter<byte>();
using var writer = new Utf8JsonWriter(buffer);
writer.WriteStartObject();
writer.WriteNumber("id"u8, order.Id);      // UTF-8 literals: no transcoding
writer.WriteString("name"u8, order.Name);
writer.WriteEndObject();
```

And when reading, prefer `Utf8JsonReader` over a span to deserializing into an object graph you
immediately discard — the cheapest DTO is the one you never materialize.

---

## 12. SIMD

One instruction, many values, one core. Orthogonal to [Step 3](ParallelismVsConcurrency.md)'s task
parallelism, and they compose.

Measured — summing a `float[]` (i5-13420H, `Vector<float>.Count` = 8, .NET 10.0.4, `--job short`):

```
| Method                                  | Length  | Mean          | Ratio |
|---------------------------------------- |-------- |--------------:|------:|
| 'scalar loop'                           | 1024    |     459.30 ns |  1.00 |
| 'Vector<T> (portable width)'            | 1024    |      70.70 ns |  0.15 |
| 'Vector256<T> (fixed width + fallback)' | 1024    |      71.88 ns |  0.16 |
| 'TensorPrimitives.Sum [.NET 9]'         | 1024    |      35.59 ns |  0.08 |
|                                         |         |               |       |
| 'scalar loop'                           | 1048576 | 512,662.79 ns |  1.00 |
| 'Vector<T> (portable width)'            | 1048576 | 101,401.97 ns |  0.20 |
| 'Vector256<T> (fixed width + fallback)' | 1048576 |  98,895.06 ns |  0.19 |
| 'TensorPrimitives.Sum [.NET 9]'         | 1048576 |  85,646.39 ns |  0.17 |
```

**`TensorPrimitives` won at both sizes, and by 2× over hand-written SIMD at the small one**
(35.6 vs 70.7 ns). It is better at exactly the parts that are tedious and easy to get wrong: tail
handling, avoiding per-iteration slice overhead, and picking the instruction set. This is the
strongest possible argument for the recommendation below — you are unlikely to beat it, and you are
quite likely to introduce a tail bug trying.

**`Vector<T>` and `Vector256<T>` are indistinguishable** (70.70 vs 71.88 ns, error ±102 on the
latter), because `Vector<float>.Count` is 8 on this CPU — the portable abstraction *is* 256-bit
here. So the fixed-width version bought nothing while costing an `IsSupported` fallback path. Prefer
`Vector<T>` unless you've measured a specific reason not to.

Both SIMD approaches also beat scalar by more at 1 K (6.5×) than at 1 M (5.1×), because the large
array stops fitting in cache and becomes memory-bandwidth-bound. SIMD makes the arithmetic faster;
it cannot make the memory bus wider.

### 12.1 The four levels

| Approach | Trade-off |
|---|---|
| **`TensorPrimitives`** [.NET 9] | **Start here.** Written and tuned by the intrinsics team, picks the best path per CPU, zero correctness risk. If it has your operation, stop. |
| **`Vector<T>`** | Portable; width chosen at JIT time. Write once, runs well everywhere. |
| **`Vector128/256/512<T>`** | Fixed width, fastest where supported. **Requires an `IsSupported` fallback.** |
| **`System.Runtime.Intrinsics.X86/Arm`** | Individual instructions. Maximum control, maximum cost, per-architecture code. |

### 12.2 Rules

- **Always handle the tail.** `length % width` elements remain; a loop that ignores them is a
  correctness bug, not a performance one.
- **Always provide a fallback** for fixed-width paths. `Vector256.IsHardwareAccelerated` is not
  guaranteed.
- **Verify it vectorized** rather than assuming — [Step 1 §9.1](WaysToMeasure.md#91-looking-at-the-generated-assembly):
  ```bash
  dotnet run -c Release -f net10.0 -- bench --filter '*SimdSum*' --disasm
  # look for ymm/zmm registers and vpaddd / vaddps
  ```
- **`Vector512` is not automatically better.** Some CPUs downclock under AVX-512. Measure.

> `[.NET 10]` adds `AVX10.2` intrinsics (`System.Runtime.Intrinsics.X86.Avx10v2`), currently disabled
> by default since no shipping hardware supports it yet.

---

## 13. Writing code the JIT can stack-allocate

Every other technique here is you doing work. This one is **getting out of the way**: .NET 10's
escape analysis stack-allocates objects it can prove don't outlive the method — no change to how the
code reads.

Measured — the same source, compiled and run on both runtimes (i5-13420H, `--job short`, .NET 8.0
as baseline). Each pair is a shape the JIT can prove, against the same logic with one thing added
that defeats the proof:

```
| Method                                      | Runtime   | Mean       | Ratio | Allocated |
|-------------------------------------------- |---------- |-----------:|------:|----------:|
| 'small array, stays local'                  | .NET 8.0  |  4.5274 ns |  1.00 |      40 B |
| 'small array, stays local'                  | .NET 10.0 |  2.0227 ns |  0.45 |       - |
|                                             |           |            |       |           |
| 'small array, stored in a field (escapes)'  | .NET 8.0  |  7.3626 ns |  1.00 |      40 B |
| 'small array, stored in a field (escapes)'  | .NET 10.0 | 10.1741 ns |  1.38 |      40 B |
|                                             |           |            |       |           |
| 'object via non-escaping struct field'      | .NET 8.0  |  3.4848 ns |  1.00 |      40 B |
| 'object via non-escaping struct field'      | .NET 10.0 |  0.7603 ns |  0.22 |       - |
|                                             |           |            |       |           |
| 'delegate, stays local'                     | .NET 8.0  | 51.9580 ns |  1.00 |      88 B |
| 'delegate, stays local'                     | .NET 10.0 | 46.4228 ns |  0.89 |      24 B |
|                                             |           |            |       |           |
| 'delegate, returned via a field (escapes)'  | .NET 8.0  | 51.5843 ns |  1.00 |      88 B |
| 'delegate, returned via a field (escapes)'  | .NET 10.0 | 53.4878 ns |  1.04 |      88 B |
|                                             |           |            |       |           |
| 'array via IEnumerable (enumerator boxing)' | .NET 8.0  | 94.7448 ns |  1.00 |      32 B |
| 'array via IEnumerable (enumerator boxing)' | .NET 10.0 | 41.4460 ns |  0.44 |       - |
|                                             |           |            |       |           |
| 'array directly (control)'                  | .NET 8.0  | 39.8081 ns |  1.00 |         - |
| 'array directly (control)'                  | .NET 10.0 | 42.2513 ns |  1.06 |         - |
```

**Three patterns went to literal zero allocations, and their escaping twins did not.** That contrast
is what makes this a measurement rather than a coincidence — if .NET 10 had simply gotten faster at
allocating, both halves of each pair would have moved.

| Shape | .NET 8 → .NET 10 | Speedup |
|---|---|---|
| Small array, local | 40 B → **0 B** | 2.2× |
| Object via local struct field | 40 B → **0 B** | 4.6× |
| Array via `IEnumerable` | 32 B → **0 B** | 2.3× |
| Delegate, local | 88 B → 24 B | 1.1× |
| *Small array, escapes* | *40 B → 40 B* | *none* |
| *Delegate, escapes* | *88 B → 88 B* | *none* |

The `IEnumerable` row is the one to dwell on. On .NET 8, iterating an array through
`IEnumerable<int>` cost **2.4× more** than indexing it directly (94.7 vs 39.8 ns) — the abstraction
penalty people write awkward code to avoid. On .NET 10 it is **41.4 vs 42.3 ns: statistically
identical to direct indexing**. The penalty is gone. Array interface devirtualization plus enumerator
stack allocation closed it completely.

Two caveats, stated because the table invites over-reading:

- **The delegate row only got to 24 B, not 0.** The `Func` object is stack-allocated; the closure
  display class holding the captured variable still isn't. Runtime work in progress.
- **`small array, escapes` measured *slower* on .NET 10** (10.17 vs 7.36 ns). Both allocate 40 B, so
  nothing changed about the allocation. At a 3 ns absolute difference on a `--job short` run I would
  not read anything into the direction without a full-precision run; what matters for this section
  is the unchanged Allocated column.

### 13.1 What .NET 10 can do

| Pattern | Status |
|---|---|
| Small fixed-size arrays of value types | ✅ [.NET 10] |
| Small fixed-size arrays of reference types | ✅ [.NET 10] |
| Objects referenced only via non-escaping **local struct fields** | ✅ [.NET 10] |
| Non-escaping **delegates** (the `Func`, not its closure) | ✅ [.NET 10] |
| Boxes | ✅ [.NET 9] |
| Closure display classes | ❌ not yet |

### 13.2 What makes an object escape

An object escapes — and is heap-allocated — if it is:

- **returned** from the method,
- **stored in a field** (of anything that isn't itself provably local),
- **captured** by something that escapes,
- **passed to a method the JIT did not inline**,
- of **variable or large size**.

Which makes the actionable rules:

1. Keep short-lived arrays **small and fixed-size** (`[1, 2, 3, 4]`, not `new int[n]`).
2. Don't store a temporary in a field "just to avoid re-creating it" — that's the old advice, and on
   .NET 10 it is now actively counterproductive.
3. Keep helper methods small enough to inline; a big helper blocks stack allocation in its *caller*.
4. Use `static` lambdas — a capture is a reference from a heap object.

### 13.3 Verify, don't assume

This is exactly what [Step 1 §9.1](WaysToMeasure.md#91-looking-at-the-generated-assembly) is for. A
stack-allocated shape has **no** `CORINFO_HELP_NEWSFAST` / `CORINFO_HELP_NEWARR_1_VC` call:

```bash
dotnet run -c Release -f net10.0 -- bench --filter '*StackAllocationPatterns*' --disasm
```

The `[MemoryDiagnoser]` 0 B row tells you it happened; the disassembly tells you *why*, which is what
you need when it stops happening after an innocuous refactor.

---

## 14. Proving it

### 14.1 The allocation regression test

The single most valuable thing in this document. Turns "don't regress this hot path" into a test that
fails in CI, and it is far more stable than any timing assertion:

```csharp
[Fact]
public void Parse_Does_Not_Allocate()
{
    Parse(Sample);      // warm up — measuring cold code measures the JIT

    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 100; i++) Parse(Sample);
    long after = GC.GetAllocatedBytesForCurrentThread();

    Assert.Equal(0, after - before);
}
```

`GC.GetAllocatedBytesForCurrentThread()` is cheap, precise, and deterministic — no statistical
machinery, no flakiness, no dedicated runner.

### 14.2 The rest

| Question | Tool |
|---|---|
| Bytes per operation? | BDN `[MemoryDiagnoser]` → `Allocated` |
| Did it actually reach zero? | A `0 B` row, confirmed with `--disasm` (§13.3) |
| Which call site allocates? | VS **.NET Object Allocation Tracking** ([Step 1 §5.1](WaysToMeasure.md#51-visual-studio)) |
| Is it allocation rate or retention? | `gc-heap-size` floor across gen2s ([Step 1 §2.3](WaysToMeasure.md#23-memory-two-independent-problems)) |
| Did the service improve? | Load test + `alloc-rate`, `time-in-gc`, p99 |

That last row is the one that closes [Step 1 §1.1's loop](WaysToMeasure.md#11-the-loop).
`/step4/naive-report` vs `/step4/pooled-report` produce identical output with different allocation
strategies. Measured in-process via `/step4/alloc-report?rows=500`:

```json
{ "identicalOutput": true, "naiveBytes": 82976, "pooledBytes": 21632, "reduction": 0.739 }
```

**74% fewer bytes, byte-identical output.** The JSON pair does better still — reflection-based
`JsonSerializer` at 107,376 B vs `Utf8JsonWriter` over a pooled buffer at 13,032 B, an **88%
reduction**.

Two things to notice about how those numbers were obtained, both of which are the point:

- **`identicalOutput` is asserted, not assumed.** An optimization that changes behaviour isn't an
  optimization. Make the equality check part of the measurement.
- **The endpoint warms up before measuring.** Calling `/step4/pooled-report` cold reports *more*
  allocation than the naive version, because the first `ArrayPool.Rent` has nothing to hand you and
  allocates. A pool's benefit is a steady-state property; measuring it cold measures the opposite.

And then the question none of that answers: **did p99 move under load?** Frequently it doesn't —
gen0 collection of short-lived garbage is very cheap — and that is a real result worth having before
you ship the complexity.

---

## 15. When not to do any of this

Every technique in this document costs something that doesn't show up in a benchmark table.

| Technique | Real cost |
|---|---|
| `Span<T>` everywhere | `ref struct` lifetime rules; can't cross `await`, `yield`, or a lambda |
| `ArrayPool` | Double-return and use-after-return bugs — load-dependent, silent, severe |
| `stackalloc` | Stack overflow is **uncatchable**; it kills the process |
| `[SkipLocalsInit]` | Reading uninitialized stack memory; information disclosure |
| `MemoryMarshal` / `Unsafe` | You have left the safety guarantees. Memory corruption is now possible. |
| Hand-written SIMD | Per-architecture code paths, tail-handling bugs, hard to review |
| Pooling everything | Trades gen0 churn for gen2 growth — often the worse problem |

### The rubric

**Do it when** the code is on a measured hot path, the allocation shows up in a profile, and the
technique is one of the cheap ones (`static` lambdas, presizing, `AsSpan` over `Substring`,
`FrozenDictionary`). These have essentially no readability cost.

**Think hard when** it requires pooling, `stackalloc`, or restructuring for `ref struct` lifetimes.
Write down the measurement that justified it, in a comment, next to the code. The next person will
otherwise "simplify" it back.

**Don't when** the code isn't hot, the allocation isn't in the profile, or you haven't measured. The
gen0 allocator is very good. Most code should allocate freely and be readable.

> The fastest code in your service is the code you deleted. The second fastest is the code that never
> ran because you cached the result. Everything in this document is a distant third — and worth
> reaching for only once the first two are exhausted.

---

## Where this goes next

This is the last step. Loop back:

| Step | Document | Why you'd return |
|---|---|---|
| 1 | [WaysToMeasure.md](WaysToMeasure.md) | Verify the change moved the metric that motivated it. §14.2's last row. |
| 2 | [AsyncVsSync.md](AsyncVsSync.md) | §11's async allocations — and `[.NET 11]` runtime async removes much of what §9 lists |
| 3 | [ParallelismVsConcurrency.md](ParallelismVsConcurrency.md) | §12's SIMD is data parallelism; §9's per-item closures are why parallel loops allocate |

---

## References

- [Memory and spans — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/)
- [`ArrayPool<T>` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1)
- [`SearchValues` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.searchvalues)
- [`TensorPrimitives` — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.tensors.tensorprimitives)
- [Stack allocation and escape analysis in .NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime#stack-allocation)
- [Performance Improvements in .NET 10 — .NET Blog](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/)
- [System.Text.Json source generation — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
- [C# 13 params collections — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-13.0/params-collections)
