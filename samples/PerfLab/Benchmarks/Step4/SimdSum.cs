using System.Numerics;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;

namespace PerfLab.Benchmarks.Step4;

/// <summary>
/// MemoryOptimizationTricks.md §12 — SIMD, the last lever before "buy more machines".
///
///   dotnet run -c Release -f net10.0 -- bench --filter *SimdSum*
///
/// Data parallelism: one instruction, many values, one core. Different axis entirely from Step 3's
/// task parallelism — and they compose (a Parallel.For whose body is vectorised uses both).
///
/// Four levels of commitment, in increasing order of how much you're giving up:
///
///   Scalar          — a plain loop. The JIT may auto-vectorise simple shapes; usually it doesn't.
///   Vector&lt;T&gt;       — portable, width chosen at JIT time. Write once, runs well everywhere.
///   Vector256&lt;T&gt;    — fixed width. Faster where supported; needs an IsSupported fallback path.
///   TensorPrimitives— [.NET 9] someone else already wrote and tuned the intrinsics. Start here.
///
/// The honest recommendation is the last one: if TensorPrimitives has your operation, use it. It
/// is written by the people who write the intrinsics, it picks the best path for the hardware, and
/// it has no correctness risk. Hand-written SIMD is for operations it does not cover.
///
/// Verify vectorisation actually happened rather than assuming (WaysToMeasure.md §9.1):
///   dotnet run -c Release -f net10.0 -- bench --filter *SimdSum* --disasm
///   → look for ymm/zmm registers and vpaddd / vaddps
/// </summary>
[MemoryDiagnoser]
public class SimdSum
{
    private float[] _data = null!;

    [Params(1_024, 1_048_576)]
    public int Length;

    [GlobalSetup]
    public void Setup()
    {
        _data = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            _data[i] = i * 0.5f;
        }

        Console.WriteLine();
        Console.WriteLine($"  Vector.IsHardwareAccelerated : {Vector.IsHardwareAccelerated}");
        Console.WriteLine($"  Vector<float>.Count          : {Vector<float>.Count} floats per operation");
        Console.WriteLine($"  Vector128/256/512 supported  : {Vector128.IsHardwareAccelerated} / " +
                          $"{Vector256.IsHardwareAccelerated} / {Vector512.IsHardwareAccelerated}");
        Console.WriteLine();
    }

    [Benchmark(Baseline = true, Description = "scalar loop")]
    public float Scalar()
    {
        ReadOnlySpan<float> data = _data;
        float sum = 0;
        for (int i = 0; i < data.Length; i++)
        {
            sum += data[i];
        }

        return sum;
    }

    [Benchmark(Description = "Vector<T> (portable width)")]
    public float PortableVector()
    {
        ReadOnlySpan<float> data = _data;
        int width = Vector<float>.Count;
        var acc = Vector<float>.Zero;

        int i = 0;
        for (; i <= data.Length - width; i += width)
        {
            acc += new Vector<float>(data.Slice(i, width));
        }

        float sum = Vector.Sum(acc);
        for (; i < data.Length; i++)      // tail: the elements that didn't fill a full vector
        {
            sum += data[i];
        }

        return sum;
    }

    [Benchmark(Description = "Vector256<T> (fixed width + fallback)")]
    public float FixedWidthVector()
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            return Scalar();              // the fallback is mandatory, not optional
        }

        ReadOnlySpan<float> data = _data;
        const int Width = 8;              // 256 bits / 32-bit float
        Vector256<float> acc = Vector256<float>.Zero;

        int i = 0;
        for (; i <= data.Length - Width; i += Width)
        {
            acc += Vector256.Create(data.Slice(i, Width));
        }

        float sum = Vector256.Sum(acc);
        for (; i < data.Length; i++)
        {
            sum += data[i];
        }

        return sum;
    }

    [Benchmark(Description = "TensorPrimitives.Sum [.NET 9]")]
    public float TensorPrimitivesSum() =>
        System.Numerics.Tensors.TensorPrimitives.Sum<float>(_data);
}
