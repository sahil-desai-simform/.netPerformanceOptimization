using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace PerfLab.Benchmarks.Step1;

/// <summary>
/// The worked example from WaysToMeasure.md §4.2 / §4.3.
///
/// Purpose: show how to read a BenchmarkDotNet summary table — Mean, Error, Ratio,
/// Gen0 (collections per 1000 ops, NOT per op) and Allocated (bytes per single op).
///
///   dotnet run -c Release -f net10.0 -- --filter *CsvFormatting*
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class CsvFormatting
{
    // Not readonly and not const: a readonly/const input can be constant-folded by the JIT,
    // which is measurement pitfall #2 in §4.7.
    private Record[] _records = null!;

    [Params(10, 1_000)]
    public int N;

    [GlobalSetup]
    public void Setup() => _records = Enumerable
        .Range(0, N)
        .Select(i => new Record(i, $"name-{i}", i * 1.5m))
        .ToArray();

    /// <summary>O(n²): every += copies the whole accumulated string.</summary>
    [Benchmark(Baseline = true)]
    public string StringConcat()
    {
        string s = "";
        foreach (Record r in _records)
        {
            s += $"{r.Id},{r.Name},{r.Amount}\n";
        }

        return s;
    }

    /// <summary>Amortised growth, one final copy.</summary>
    [Benchmark]
    public string StringBuilder_()
    {
        var sb = new StringBuilder(N * 32);
        foreach (Record r in _records)
        {
            sb.Append(r.Id).Append(',').Append(r.Name).Append(',').Append(r.Amount).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Pre-sized StringBuilder + AppendLine. Shows that "use a StringBuilder" is not the
    /// end of the story — the capacity guess matters, and so does avoiding interpolation.
    /// </summary>
    [Benchmark]
    public string StringBuilder_NoInterpolation()
    {
        var sb = new StringBuilder(capacity: N * 32);
        for (int i = 0; i < _records.Length; i++)
        {
            ref Record r = ref _records[i];
            sb.Append(r.Id);
            sb.Append(',');
            sb.Append(r.Name);
            sb.Append(',');
            sb.Append(r.Amount);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// string.Create with a span-based callback: a single exact-sized allocation.
    ///
    /// Read the measured result carefully — this is the most instructive row in the table.
    /// It wins clearly on Allocated (~30% less than StringBuilder) but LOSES on Mean, because
    /// the sizing pass calls r.Amount.ToString(), which allocates a string per record just to
    /// count its characters. Fewer bytes did not buy less time.
    ///
    /// That is the whole lesson of Step 1 in one row: "allocates less" and "is faster" are
    /// different claims, measured by different columns, and you do not get to assume one from
    /// the other.
    ///
    /// Step4/CsvFormattingFixed.cs picks this up. Removing the ToString() from the sizing pass
    /// with decimal.TryFormat cuts allocations by 48% — and it is STILL slower than
    /// StringBuilder, because the two-pass structure (format everything twice: once to measure,
    /// once to write) is the actual cost. The shape that wins does no sizing pass at all.
    /// </summary>
    [Benchmark]
    public string StringCreate()
    {
        int length = 0;
        foreach (Record r in _records)
        {
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
