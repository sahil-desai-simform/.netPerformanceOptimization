using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step3;

/// <summary>
/// ParallelismVsConcurrency.md §6 — synchronisation primitives, uncontended and contended.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *LockPrimitives*
///
/// Two regimes, and advice that's right in one is often wrong in the other:
///
///   UNCONTENDED — one thread, no waiting. You're measuring raw instruction cost. Everything looks
///                 cheap; Interlocked looks best.
///   CONTENDED   — many threads on one lock. You're measuring the SERIALISATION, and the primitive
///                 barely matters compared to how long the critical section is.
///
/// The headline result people expect from this table is "System.Threading.Lock [.NET 9] beats
/// lock(object)". It does, modestly. The result that actually changes code is that the
/// no-shared-state version beats all of them by an order of magnitude — the fastest lock is the
/// one you don't take.
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class LockPrimitives
{
    private const int OpsPerBenchmark = 100_000;

    private readonly object _monitorGate = new();
    private readonly ReaderWriterLockSlim _rwLock = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

#if NET9_0_OR_GREATER
    // System.Threading.Lock is .NET 9+. This is the one place a #if is clearer than excluding the
    // whole file per TFM, because the VERSION DIFFERENCE IS THE POINT of the benchmark.
    private readonly Lock _lockObject = new();
#endif

    private long _counter;

    // ── Uncontended: single-threaded. Raw primitive cost. ───────────────────────────────────

    [Benchmark(Baseline = true, Description = "uncontended: no lock")]
    public long NoLock()
    {
        long local = 0;
        for (int i = 0; i < OpsPerBenchmark; i++)
        {
            local++;
        }

        return local;
    }

    [Benchmark(Description = "uncontended: Interlocked")]
    public long Interlocked_()
    {
        _counter = 0;
        for (int i = 0; i < OpsPerBenchmark; i++)
        {
            Interlocked.Increment(ref _counter);
        }

        return _counter;
    }

    [Benchmark(Description = "uncontended: lock(object)")]
    public long MonitorLock()
    {
        _counter = 0;
        for (int i = 0; i < OpsPerBenchmark; i++)
        {
            lock (_monitorGate)
            {
                _counter++;
            }
        }

        return _counter;
    }

#if NET9_0_OR_GREATER
    [Benchmark(Description = "uncontended: System.Threading.Lock [.NET 9]")]
    public long SystemThreadingLock()
    {
        _counter = 0;
        for (int i = 0; i < OpsPerBenchmark; i++)
        {
            lock (_lockObject)
            {
                _counter++;
            }
        }

        return _counter;
    }
#endif

    [Benchmark(Description = "uncontended: ReaderWriterLockSlim (write)")]
    public long ReaderWriterSlim()
    {
        _counter = 0;
        for (int i = 0; i < OpsPerBenchmark; i++)
        {
            _rwLock.EnterWriteLock();
            try
            {
                _counter++;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        return _counter;
    }

    [Benchmark(Description = "uncontended: SemaphoreSlim(1,1)")]
    public long Semaphore()
    {
        _counter = 0;
        for (int i = 0; i < OpsPerBenchmark; i++)
        {
            _semaphore.Wait();
            try
            {
                _counter++;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        return _counter;
    }

    // ── Contended: every core hammering the same lock. ──────────────────────────────────────
    //    Watch the "Lock Contentions" column from [ThreadingDiagnoser], not just the Mean.

    [Benchmark(Description = "CONTENDED: lock(object)")]
    public long ContendedMonitor()
    {
        _counter = 0;
        int perThread = OpsPerBenchmark / Environment.ProcessorCount;

        Parallel.For(0, Environment.ProcessorCount, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                lock (_monitorGate)
                {
                    _counter++;
                }
            }
        });

        return _counter;
    }

#if NET9_0_OR_GREATER
    [Benchmark(Description = "CONTENDED: System.Threading.Lock [.NET 9]")]
    public long ContendedSystemLock()
    {
        _counter = 0;
        int perThread = OpsPerBenchmark / Environment.ProcessorCount;

        Parallel.For(0, Environment.ProcessorCount, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                lock (_lockObject)
                {
                    _counter++;
                }
            }
        });

        return _counter;
    }
#endif

    [Benchmark(Description = "CONTENDED: Interlocked")]
    public long ContendedInterlocked()
    {
        _counter = 0;
        int perThread = OpsPerBenchmark / Environment.ProcessorCount;

        Parallel.For(0, Environment.ProcessorCount, _ =>
        {
            for (int i = 0; i < perThread; i++)
            {
                Interlocked.Increment(ref _counter);
            }
        });

        return _counter;
    }

    /// <summary>
    /// The answer that actually matters: restructure so there is nothing to contend on.
    /// Per-thread accumulation, combined once. Compare this to every row above.
    /// </summary>
    [Benchmark(Description = "CONTENDED: no sharing (accumulate + combine)")]
    public long ContendedNoSharing()
    {
        long total = 0;
        int perThread = OpsPerBenchmark / Environment.ProcessorCount;

        Parallel.For(
            0,
            Environment.ProcessorCount,
            () => 0L,
            (_, __, local) =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    local++;
                }

                return local;
            },
            local => Interlocked.Add(ref total, local));

        return total;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _rwLock.Dispose();
        _semaphore.Dispose();
    }
}
