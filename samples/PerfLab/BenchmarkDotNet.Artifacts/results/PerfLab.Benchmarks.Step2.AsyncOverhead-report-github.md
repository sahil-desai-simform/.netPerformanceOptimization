```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                          | Mean          | Error       | StdDev     | Ratio    | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------------------ |--------------:|------------:|-----------:|---------:|--------:|-------:|----------:|------------:|
| &#39;sync call&#39;                                     |     0.6776 ns |   1.4687 ns |  0.0805 ns |     1.01 |    0.15 |      - |         - |          NA |
| &#39;async, never suspends&#39;                         |    12.3689 ns |   5.1166 ns |  0.2805 ns |    18.43 |    1.95 | 0.0229 |     144 B |          NA |
| &#39;async ValueTask, never suspends&#39;               |     5.0989 ns |   2.4413 ns |  0.1338 ns |     7.60 |    0.81 |      - |         - |          NA |
| &#39;async, suspends once&#39;                          |   730.4685 ns |  38.7969 ns |  2.1266 ns | 1,088.34 |  113.30 | 0.0162 |     104 B |          NA |
| &#39;sync chain, depth 10&#39;                          |     3.5610 ns |   0.7079 ns |  0.0388 ns |     5.31 |    0.55 |      - |         - |          NA |
| &#39;async chain, depth 10, never suspends&#39;         |   106.2650 ns |  34.5080 ns |  1.8915 ns |   158.33 |   16.66 | 0.1377 |     864 B |          NA |
| &#39;async chain, depth 10, suspends at the bottom&#39; | 1,381.6823 ns | 521.9697 ns | 28.6109 ns | 2,058.59 |  217.43 | 0.1926 |    1220 B |          NA |
