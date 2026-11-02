using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace PerfLab.Api;

/// <summary>
/// CPU lab — three real-world hotspots, each with a fixed twin at the same URL plus "-fixed".
///
/// /step1/cpu burns CPU in an anonymous lambda, which teaches the counter signature but not how to
/// find the guilty line. These put the cost in NAMED methods, so the profiler points straight at them:
///
///   1. Visual Studio → Alt+F2 → CPU Usage → Start.
///   2. bombardier -c 8 -d 20s "http://localhost:5199/cpu/duplicate-orders"
///   3. Stop collection. Hot Path → OrderChecks.CountDuplicatesSlow → double-click.
///      The source opens with CPU % per line; the List.Contains line carries it.
///   4. Repeat against the -fixed URL: compare bombardier's Reqs/sec and the method's Self CPU.
///
/// Inputs are built once at startup so input generation never shows up in the profile.
/// </summary>
internal static class CpuLabEndpoints
{
    public static readonly string[] Catalog =
    [
        "GET  /cpu/duplicate-orders?n=20000         — List.Contains inside a loop: O(n²).",
        "GET  /cpu/duplicate-orders-fixed?n=20000   — HashSet: O(n).",
        "GET  /cpu/validate-emails?n=100            — compiles a new Regex for every email.",
        "GET  /cpu/validate-emails-fixed?n=100      — one source-generated Regex.",
        "GET  /cpu/parse-quantities?n=10000         — int.Parse + catch for the invalid half.",
        "GET  /cpu/parse-quantities-fixed?n=10000   — int.TryParse.",
    ];

    private static readonly int[] OrderIds = CreateOrderIds(100_000);
    private static readonly string[] Emails = CreateEmails(10_000);
    private static readonly string[] Quantities = CreateQuantities(100_000);

    public static void MapCpuLabEndpoints(this WebApplication app)
    {
        // ── 1. Wrong data structure: the cost hides in one innocent-looking line. ──────────
        app.MapGet("/cpu/duplicate-orders", (int n = 20000) =>
            Results.Ok(new { n, duplicates = OrderChecks.CountDuplicatesSlow(Take(OrderIds, n)) }));

        app.MapGet("/cpu/duplicate-orders-fixed", (int n = 20000) =>
            Results.Ok(new { n, duplicates = OrderChecks.CountDuplicatesFast(Take(OrderIds, n)) }));

        // ── 2. Repeated setup: paying a one-time cost on every call. The hot frames are deep
        //    inside the framework (RegexCompiler, Reflection.Emit); expand Hot Path until you
        //    reach the first frame in YOUR code — that is the line to fix.
        app.MapGet("/cpu/validate-emails", (int n = 100) =>
            Results.Ok(new { n, valid = OrderChecks.CountValidEmailsSlow(Take(Emails, n)) }));

        app.MapGet("/cpu/validate-emails-fixed", (int n = 100) =>
            Results.Ok(new { n, valid = OrderChecks.CountValidEmailsFast(Take(Emails, n)) }));

        // ── 3. Exceptions as control flow: each throw captures a stack trace and unwinds. ──
        app.MapGet("/cpu/parse-quantities", (int n = 10000) =>
            Results.Ok(new { n, total = OrderChecks.SumQuantitiesSlow(Take(Quantities, n)) }));

        app.MapGet("/cpu/parse-quantities-fixed", (int n = 10000) =>
            Results.Ok(new { n, total = OrderChecks.SumQuantitiesFast(Take(Quantities, n)) }));
    }

    private static ReadOnlySpan<T> Take<T>(T[] source, int n) => source.AsSpan(0, Math.Clamp(n, 1, source.Length));

    private static int[] CreateOrderIds(int count)
    {
        var random = new Random(42);
        var ids = new int[count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = random.Next(0, count / 2);   // ~half are duplicates
        }

        return ids;
    }

    private static string[] CreateEmails(int count)
    {
        var emails = new string[count];
        for (int i = 0; i < emails.Length; i++)
        {
            emails[i] = i % 3 == 0 ? $"user{i}.example.com" : $"user{i}@example.com";
        }

        return emails;
    }

    private static string[] CreateQuantities(int count)
    {
        var quantities = new string[count];
        for (int i = 0; i < quantities.Length; i++)
        {
            quantities[i] = i % 2 == 0 ? i.ToString() : $"{i}x";   // half are invalid
        }

        return quantities;
    }
}

/// <summary>
/// The hot methods. NoInlining keeps each one as its own frame in the profiler; in real code the
/// JIT may inline a small method into its caller, and its cost then shows up under the caller.
/// </summary>
internal static partial class OrderChecks
{
    private const string EmailPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CountDuplicatesSlow(ReadOnlySpan<int> orderIds)
    {
        var seen = new List<int>();
        int duplicates = 0;
        foreach (int id in orderIds)
        {
            if (seen.Contains(id))   // ← HOT: linear scan, every iteration
            {
                duplicates++;
            }
            else
            {
                seen.Add(id);
            }
        }

        return duplicates;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CountDuplicatesFast(ReadOnlySpan<int> orderIds)
    {
        var seen = new HashSet<int>(orderIds.Length);
        int duplicates = 0;
        foreach (int id in orderIds)
        {
            if (!seen.Add(id))
            {
                duplicates++;
            }
        }

        return duplicates;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CountValidEmailsSlow(ReadOnlySpan<string> emails)
    {
        int valid = 0;
        foreach (string email in emails)
        {
            var regex = new Regex(EmailPattern, RegexOptions.Compiled);   // ← HOT: parse + compile to IL, every email
            if (regex.IsMatch(email))
            {
                valid++;
            }
        }

        return valid;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CountValidEmailsFast(ReadOnlySpan<string> emails)
    {
        int valid = 0;
        foreach (string email in emails)
        {
            if (EmailRegex().IsMatch(email))
            {
                valid++;
            }
        }

        return valid;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long SumQuantitiesSlow(ReadOnlySpan<string> quantities)
    {
        long total = 0;
        foreach (string quantity in quantities)
        {
            try
            {
                total += int.Parse(quantity);
            }
            catch (FormatException)   // ← HOT: half the inputs throw
            {
            }
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long SumQuantitiesFast(ReadOnlySpan<string> quantities)
    {
        long total = 0;
        foreach (string quantity in quantities)
        {
            if (int.TryParse(quantity, out int value))
            {
                total += value;
            }
        }

        return total;
    }

    [GeneratedRegex(EmailPattern)]
    private static partial Regex EmailRegex();
}
