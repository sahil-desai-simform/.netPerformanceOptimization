```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                        | N    | Mean         | Error        | StdDev      | Ratio | RatioSD | Gen0      | Gen1     | Allocated  | Alloc Ratio |
|------------------------------ |----- |-------------:|-------------:|------------:|------:|--------:|----------:|---------:|-----------:|------------:|
| StringBuilder_                | 10   |     293.3 ns |     26.12 ns |     1.43 ns |  0.60 |    0.01 |    0.1593 |        - |     1000 B |        0.47 |
| StringBuilder_NoInterpolation | 10   |     303.6 ns |    106.92 ns |     5.86 ns |  0.62 |    0.01 |    0.1593 |        - |     1000 B |        0.47 |
| StringCreate                  | 10   |     468.1 ns |     29.99 ns |     1.64 ns |  0.96 |    0.02 |    0.0968 |        - |      608 B |        0.28 |
| StringConcat                  | 10   |     487.9 ns |    162.47 ns |     8.91 ns |  1.00 |    0.02 |    0.3414 |        - |     2144 B |        1.00 |
|                               |      |              |              |             |       |         |           |          |            |             |
| StringBuilder_NoInterpolation | 1000 |  36,893.9 ns | 15,054.02 ns |   825.16 ns |  0.04 |    0.00 |   16.1133 |   2.6855 |   102176 B |       0.005 |
| StringBuilder_                | 1000 |  37,434.1 ns |  1,417.30 ns |    77.69 ns |  0.04 |    0.00 |   16.1133 |   2.6855 |   102176 B |       0.005 |
| StringCreate                  | 1000 |  56,901.8 ns |  4,849.93 ns |   265.84 ns |  0.06 |    0.00 |   11.5356 |        - |    72768 B |       0.004 |
| StringConcat                  | 1000 | 923,303.1 ns | 41,068.66 ns | 2,251.11 ns |  1.00 |    0.00 | 2965.8203 | 248.0469 | 18654920 B |       1.000 |
