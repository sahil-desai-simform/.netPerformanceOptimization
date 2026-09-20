```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                | WorkPerItem | Mean          | Error        | StdDev      | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------- |------------ |--------------:|-------------:|------------:|------:|--------:|-------:|----------:|------------:|
| **SequentialFor**         | **1**           |      **4.070 μs** |     **1.577 μs** |   **0.0864 μs** |  **1.00** |    **0.03** |      **-** |         **-** |          **NA** |
| ParallelFor           | 1           |     16.984 μs |     7.523 μs |   0.4124 μs |  4.17 |    0.12 | 0.6256 |    3920 B |          NA |
| ParallelForBoundedDop | 1           |     10.013 μs |    12.801 μs |   0.7017 μs |  2.46 |    0.16 | 0.4120 |    2607 B |          NA |
| ParallelForChunked    | 1           |      7.247 μs |     1.031 μs |   0.0565 μs |  1.78 |    0.04 | 0.5188 |    3217 B |          NA |
|                       |             |               |              |             |       |         |        |           |             |
| **SequentialFor**         | **100**         |    **278.649 μs** |    **73.938 μs** |   **4.0528 μs** |  **1.00** |    **0.02** |      **-** |         **-** |          **NA** |
| ParallelFor           | 100         |     49.744 μs |    19.262 μs |   1.0558 μs |  0.18 |    0.00 | 0.6104 |    3987 B |          NA |
| ParallelForBoundedDop | 100         |     72.622 μs |     9.005 μs |   0.4936 μs |  0.26 |    0.00 | 0.3662 |    2586 B |          NA |
| ParallelForChunked    | 100         |     92.740 μs |    11.446 μs |   0.6274 μs |  0.33 |    0.00 | 0.4883 |    3510 B |          NA |
|                       |             |               |              |             |       |         |        |           |             |
| **SequentialFor**         | **10000**       | **28,695.100 μs** |   **139.859 μs** |   **7.6661 μs** |  **1.00** |    **0.00** |      **-** |         **-** |          **NA** |
| ParallelFor           | 10000       |  3,906.504 μs |   518.267 μs |  28.4080 μs |  0.14 |    0.00 |      - |    4051 B |          NA |
| ParallelForBoundedDop | 10000       |  6,407.810 μs | 4,141.789 μs | 227.0255 μs |  0.22 |    0.01 |      - |    2643 B |          NA |
| ParallelForChunked    | 10000       |  4,612.784 μs | 6,963.951 μs | 381.7176 μs |  0.16 |    0.01 |      - |    4087 B |          NA |
