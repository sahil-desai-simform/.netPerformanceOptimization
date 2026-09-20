```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                   | N    | Mean     | Error     | StdDev   | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|----------------------------------------- |----- |---------:|----------:|---------:|------:|--------:|--------:|-------:|----------:|------------:|
| &#39;ArrayPool buffer, single pass&#39;          | 1000 | 32.02 μs | 19.747 μs | 1.082 μs |  0.79 |    0.02 |  5.9814 |      - |  37.21 KB |        0.37 |
| StringBuilder_                           | 1000 | 40.60 μs |  4.475 μs | 0.245 μs |  1.00 |    0.01 | 16.1133 | 2.6855 |  99.78 KB |        1.00 |
| &#39;StringCreate (TryFormat sizing)&#39;        | 1000 | 55.35 μs | 37.446 μs | 2.053 μs |  1.36 |    0.04 |  5.9814 |      - |  37.21 KB |        0.37 |
| &#39;StringCreate (Step 1, ToString sizing)&#39; | 1000 | 59.69 μs |  5.842 μs | 0.320 μs |  1.47 |    0.01 | 11.5356 |      - |  71.06 KB |        0.71 |
