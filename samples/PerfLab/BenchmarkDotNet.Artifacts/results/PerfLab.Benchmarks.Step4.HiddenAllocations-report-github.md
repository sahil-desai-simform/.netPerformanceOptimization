```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                      | Mean       | Error     | StdDev   | Ratio | Gen0   | Allocated | Alloc Ratio |
|-------------------------------------------- |-----------:|----------:|---------:|------:|-------:|----------:|------------:|
| &#39;boxing: object holds an int&#39;               | 2,078.4 ns | 311.15 ns | 17.06 ns |  1.00 | 3.8223 |   24000 B |       1.000 |
| &#39;no boxing: stay in the value type&#39;         |   292.1 ns |  70.28 ns |  3.85 ns |  0.14 |      - |         - |       0.000 |
| &#39;capturing lambda (display class per call)&#39; |   314.7 ns | 157.23 ns |  8.62 ns |  0.15 | 0.0038 |      24 B |       0.001 |
| &#39;static lambda (delegate cached)&#39;           |   305.3 ns |  55.94 ns |  3.07 ns |  0.15 |      - |         - |       0.000 |
| &#39;plain loop (no delegate at all)&#39;           |   436.1 ns | 228.49 ns | 12.52 ns |  0.21 |      - |         - |       0.000 |
| Enum.ToString()                             |   750.3 ns | 349.49 ns | 19.16 ns |  0.36 | 0.3824 |    2400 B |       0.100 |
| &#39;switch expression over the enum&#39;           |   119.8 ns |  25.92 ns |  1.42 ns |  0.06 |      - |         - |       0.000 |
| &#39;params int[] (array per call)&#39;             |   391.5 ns | 179.85 ns |  9.86 ns |  0.19 | 0.6375 |    4000 B |       0.167 |
| &#39;params ReadOnlySpan&lt;int&gt; [C# 13]&#39;          |   107.2 ns |   8.34 ns |  0.46 ns |  0.05 |      - |         - |       0.000 |
| &#39;LINQ chain (enumerators + delegates)&#39;      |   970.5 ns | 556.11 ns | 30.48 ns |  0.47 | 0.0153 |     104 B |       0.004 |
| &#39;single pass&#39;                               |   458.8 ns | 493.08 ns | 27.03 ns |  0.22 |      - |         - |       0.000 |
