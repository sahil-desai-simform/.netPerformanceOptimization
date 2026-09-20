using System.Buffers;
using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step4;

/// <summary>
/// MemoryOptimizationTricks.md §5 — string work without allocating.
///
///   dotnet run -c Release -f net10.0 -- bench --filter *StringOperations*
///
/// Strings are immutable, so every Substring, Split, Trim, ToUpper and concat produces a new one.
/// In a parsing hot path that is usually the single largest source of gen0 pressure — and it is
/// also the easiest to remove, because ReadOnlySpan&lt;char&gt; slices without copying.
///
/// SearchValues [.NET 8] is the one that surprises people: it precomputes a vectorised lookup for
/// a set of characters. It must be a static readonly field to pay off — constructing one per call
/// costs far more than the search it accelerates.
/// </summary>
[MemoryDiagnoser]
public class StringOperations
{
    private const string Line =
        "2026-09-13T14:22:01Z,order-88213,ACME Corporation,1499.95,SHIPPED,priority=high;region=emea";

    // Static readonly: built once at type init. This is not optional — a per-call SearchValues
    // constructor is slower than the IndexOfAny it replaces.
    private static readonly SearchValues<char> Delimiters = SearchValues.Create(",;=");

    private string _input = null!;

    [GlobalSetup]
    public void Setup() => _input = Line;

    // ── 1. Splitting ────────────────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "string.Split")]
    public int SplitAllocating()
    {
        string[] parts = _input.Split(',');       // string[] + one string per field
        int total = 0;
        foreach (string p in parts)
        {
            total += p.Length;
        }

        return total;
    }

    [Benchmark(Description = "manual span scan")]
    public int SplitViaSpan()
    {
        ReadOnlySpan<char> remaining = _input;
        int total = 0;

        while (!remaining.IsEmpty)
        {
            int comma = remaining.IndexOf(',');
            if (comma < 0)
            {
                total += remaining.Length;
                break;
            }

            total += remaining[..comma].Length;    // a view, not a copy
            remaining = remaining[(comma + 1)..];
        }

        return total;
    }

    [Benchmark(Description = "MemoryExtensions.Split into ranges [.NET 8]")]
    public int SplitViaRanges()
    {
        ReadOnlySpan<char> input = _input;
        Span<Range> ranges = stackalloc Range[16];
        int count = input.Split(ranges, ',');

        int total = 0;
        for (int i = 0; i < count; i++)
        {
            total += input[ranges[i]].Length;
        }

        return total;
    }

    // ── 2. Substring vs slice ───────────────────────────────────────────────────────────────

    [Benchmark(Description = "Substring + int.Parse")]
    public int SubstringParse()
    {
        int start = _input.IndexOf("order-", StringComparison.Ordinal) + 6;
        int end = _input.IndexOf(',', start);
        string id = _input.Substring(start, end - start);   // allocates
        return int.Parse(id);
    }

    [Benchmark(Description = "AsSpan slice + int.Parse")]
    public int SpanParse()
    {
        ReadOnlySpan<char> input = _input;
        int start = input.IndexOf("order-".AsSpan(), StringComparison.Ordinal) + 6;
        ReadOnlySpan<char> rest = input[start..];
        int end = rest.IndexOf(',');
        return int.Parse(rest[..end]);                      // int.Parse has a span overload
    }

    // ── 3. Multi-character search ───────────────────────────────────────────────────────────

    [Benchmark(Description = "IndexOfAny(char[])")]
    public int IndexOfAnyChars()
    {
        ReadOnlySpan<char> input = _input;
        int count = 0;
        int pos;
        while ((pos = input.IndexOfAny(',', ';', '=')) >= 0)
        {
            count++;
            input = input[(pos + 1)..];
        }

        return count;
    }

    [Benchmark(Description = "SearchValues [.NET 8]")]
    public int IndexOfAnySearchValues()
    {
        ReadOnlySpan<char> input = _input;
        int count = 0;
        int pos;
        while ((pos = input.IndexOfAny(Delimiters)) >= 0)
        {
            count++;
            input = input[(pos + 1)..];
        }

        return count;
    }

    // ── 4. Building a string ────────────────────────────────────────────────────────────────

    [Benchmark(Description = "interpolation")]
    public string Interpolated() => $"order={_input.Length}:{_input[..8]}";

    [Benchmark(Description = "string.Create with a stack buffer")]
    public string CreatedWithSpan()
    {
        // The length must be EXACT. string.Create hands you a span of precisely this size and
        // whatever you leave unwritten stays '\0' in the finished string — a silent corruption
        // rather than an exception. Guessing "big enough" is not an option here, which is the
        // constraint that makes the sizing pass worth getting right (see CsvFormattingFixed).
        int total = 6 + CountDigits(_input.Length) + 1 + 8;

        return string.Create(total, _input, static (span, input) =>
        {
            "order=".AsSpan().CopyTo(span);
            int pos = 6;
            input.Length.TryFormat(span[pos..], out int written);
            pos += written;
            span[pos++] = ':';
            input.AsSpan(0, 8).CopyTo(span[pos..]);
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
}
