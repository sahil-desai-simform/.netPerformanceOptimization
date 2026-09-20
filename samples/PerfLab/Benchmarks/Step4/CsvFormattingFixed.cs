using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace PerfLab.Benchmarks.Step4;

/// <summary>
/// MemoryOptimizationTricks.md §6 — closing the loop on Step 1.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *CsvFormattingFixed*
///
/// THE STORY, ACROSS TWO DOCUMENTS:
///
/// Step 1's CsvFormatting benchmark produced an awkward result that was kept rather than tidied
/// away: `StringCreate` allocated 30% LESS than `StringBuilder` and ran 47% SLOWER.
///
///     | StringBuilder_ | 1000 | 33,157 ns | 102,176 B |
///     | StringCreate   | 1000 | 48,783 ns |  72,768 B |   ← fewer bytes, more time
///
/// Step 1 used that to make a point about reading Mean and Allocated as separate claims. It did
/// not explain WHY, because the why is this document's subject.
///
/// The why: string.Create needs the exact length up front, so the implementation does a sizing
/// pass first — and that pass called `r.Amount.ToString()` purely to count characters. Every
/// record allocated a string that was immediately discarded. The "allocation-free" formatter was
/// allocating once per record before it formatted anything.
///
/// The fix is §6's subject: `TryFormat` writes into a stack buffer and reports the length without
/// ever producing a string. The prediction going in was that `StringCreateFixed` would then win on
/// BOTH columns.
///
/// IT DOESN'T, AND THAT IS WHY THIS FILE IS WORTH READING. Measured: TryFormat sizing cuts
/// allocations 48% (71.06 → 37.21 KB) and time by 7% — and the result is still 36% SLOWER than
/// StringBuilder. The ToString() call was a real allocation bug, but it was never why the approach
/// was slow. The real cost is structural: string.Create needs an exact length, so every value gets
/// formatted TWICE — once to measure, once to write — and for decimal that double work exceeds
/// StringBuilder's single pass no matter how cheaply you do the measuring.
///
/// The row that wins on both columns is `PooledBuffer`, which has no sizing pass at all: format
/// once into an over-allocated pooled buffer, then copy out what you used.
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class CsvFormattingFixed
{
    private Record[] _records = null!;

    [Params(1_000)]
    public int N;

    [GlobalSetup]
    public void Setup() => _records = Enumerable
        .Range(0, N)
        .Select(i => new Record(i, $"name-{i}", i * 1.5m))
        .ToArray();

    /// <summary>Step 1's winner on time, carried over unchanged as the baseline.</summary>
    [Benchmark(Baseline = true)]
    public string StringBuilder_()
    {
        var sb = new StringBuilder(N * 32);
        foreach (Record r in _records)
        {
            sb.Append(r.Id).Append(',').Append(r.Name).Append(',').Append(r.Amount).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Step 1's version, carried over unchanged. The sizing pass allocates per record.</summary>
    [Benchmark(Description = "StringCreate (Step 1, ToString sizing)")]
    public string StringCreateOriginal()
    {
        int length = 0;
        foreach (Record r in _records)
        {
            // ← the bug: allocates a string per record purely to read its Length
            length += CountDigits(r.Id) + 1 + r.Name.Length + 1 + r.Amount.ToString().Length + 1;
        }

        return string.Create(length, _records, static (span, records) =>
        {
            int pos = 0;
            foreach (Record r in records)
            {
                r.Id.TryFormat(span[pos..], out int written);
                pos += written;
                span[pos++] = ',';
                r.Name.AsSpan().CopyTo(span[pos..]);
                pos += r.Name.Length;
                span[pos++] = ',';
                r.Amount.TryFormat(span[pos..], out written);
                pos += written;
                span[pos++] = '\n';
            }
        });
    }

    /// <summary>
    /// The fix. TryFormat into a stack buffer gives the length with zero allocation.
    /// decimal's maximum formatted length is 31 characters (29 digits + sign + point), so a
    /// 32-char stackalloc is always sufficient — no bounds risk, no heap.
    /// </summary>
    [Benchmark(Description = "StringCreate (TryFormat sizing)")]
    public string StringCreateFixed()
    {
        int length = 0;
        Span<char> scratch = stackalloc char[32];

        foreach (Record r in _records)
        {
            r.Amount.TryFormat(scratch, out int amountLength);
            length += CountDigits(r.Id) + 1 + r.Name.Length + 1 + amountLength + 1;
        }

        return string.Create(length, _records, static (span, records) =>
        {
            int pos = 0;
            foreach (Record r in records)
            {
                r.Id.TryFormat(span[pos..], out int written);
                pos += written;
                span[pos++] = ',';
                r.Name.AsSpan().CopyTo(span[pos..]);
                pos += r.Name.Length;
                span[pos++] = ',';
                r.Amount.TryFormat(span[pos..], out written);
                pos += written;
                span[pos++] = '\n';
            }
        });
    }

    /// <summary>
    /// Single pass into a pooled buffer, then one copy out. Avoids the sizing pass entirely by
    /// over-allocating and trimming — usually the most practical shape when the exact length is
    /// awkward to compute. The rent/return discipline is §7's subject.
    /// </summary>
    [Benchmark(Description = "ArrayPool buffer, single pass")]
    public string PooledBuffer()
    {
        char[] buffer = System.Buffers.ArrayPool<char>.Shared.Rent(N * 48);
        try
        {
            Span<char> span = buffer;
            int pos = 0;

            foreach (Record r in _records)
            {
                r.Id.TryFormat(span[pos..], out int written);
                pos += written;
                span[pos++] = ',';
                r.Name.AsSpan().CopyTo(span[pos..]);
                pos += r.Name.Length;
                span[pos++] = ',';
                r.Amount.TryFormat(span[pos..], out written);
                pos += written;
                span[pos++] = '\n';
            }

            return new string(span[..pos]);
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static int CountDigits(int value)
    {
        int digits = value <= 0 ? 1 : 0;
        while (value != 0)
        {
            digits++;
            value /= 10;
        }

        return digits;
    }

    public readonly record struct Record(int Id, string Name, decimal Amount);
}
