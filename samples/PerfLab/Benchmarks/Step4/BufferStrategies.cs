using System.Buffers;
using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step4;

/// <summary>
/// MemoryOptimizationTricks.md §3 / §7 — where to put a temporary buffer.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *BufferStrategies*
///
/// Three options, and the right answer changes with size — which is why this sweeps [Params]
/// rather than stating a rule:
///
///   new byte[n]   — simplest. Gen0 allocation; free to collect if it dies immediately.
///   stackalloc    — zero heap. Bounded by stack size (~1 MB), so it has a hard ceiling.
///   ArrayPool     — reusable. Rent/return bookkeeping, and pooled arrays live in gen2.
///
/// What the table is for: finding where each stops being the best choice on YOUR hardware.
/// Expect stackalloc to dominate small sizes, ArrayPool to win large ones, and `new` to be
/// perfectly adequate in a surprisingly wide middle — the gen0 allocator is a pointer bump, and
/// a buffer that dies immediately costs almost nothing to collect.
///
/// The 85,000-byte LOH threshold is the one hard discontinuity. Above it `new` stops being cheap
/// (no compaction, collected only with gen2) and pooling stops being optional.
/// </summary>
[MemoryDiagnoser]
public class BufferStrategies
{
    /// <summary>128 B and 1 KB are stackalloc territory; 64 KB is not; 128 KB is over the LOH threshold.</summary>
    [Params(128, 1024, 64 * 1024, 128 * 1024)]
    public int Size;

    private const int StackallocLimit = 1024;   // keep well under the ~1 MB stack

    [Benchmark(Baseline = true, Description = "new byte[n]")]
    public int NewArray()
    {
        byte[] buffer = new byte[Size];
        return Fill(buffer);
    }

    [Benchmark(Description = "stackalloc (falls back to pool over 1 KB)")]
    public int StackallocWithFallback()
    {
        // The standard defensive pattern: stackalloc only below a fixed limit, pool above it.
        // Unconditional `stackalloc size` where size comes from input is a stack-overflow bug.
        if (Size <= StackallocLimit)
        {
            Span<byte> buffer = stackalloc byte[Size];
            return Fill(buffer);
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(Size);
        try
        {
            return Fill(rented.AsSpan(0, Size));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Benchmark(Description = "ArrayPool rent/return")]
    public int Pooled()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Size);
        try
        {
            // Rent returns an array AT LEAST this long, usually longer. Always slice to the size
            // you asked for — using buffer.Length is a classic source of subtle bugs.
            return Fill(buffer.AsSpan(0, Size));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Benchmark(Description = "ArrayPool with clearArray: true")]
    public int PooledCleared()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Size);
        try
        {
            return Fill(buffer.AsSpan(0, Size));
        }
        finally
        {
            // Required when the buffer held anything sensitive — the next renter sees whatever
            // you left behind. It costs a memset proportional to size; price it here.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    [Benchmark(Description = "GC.AllocateUninitializedArray")]
    public int Uninitialized()
    {
        // Skips zeroing. Only valid when you overwrite every byte you subsequently read —
        // which Fill does. For large buffers the memset is a measurable fraction of the cost.
        byte[] buffer = GC.AllocateUninitializedArray<byte>(Size);
        return Fill(buffer);
    }

    private static int Fill(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; i += 64)
        {
            buffer[i] = (byte)i;
        }

        return buffer.Length;
    }
}
