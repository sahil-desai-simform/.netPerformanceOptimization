```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method        | SynchronousPercent | Mean        | Error       | StdDev    | Ratio | Gen0   | Allocated | Alloc Ratio |
|-------------- |------------------- |------------:|------------:|----------:|------:|-------:|----------:|------------:|
| **WithTask**      | **0**                  | **76,145.8 ns** |    **91.74 ns** |   **5.03 ns** |  **1.00** | **1.7090** |   **10576 B** |        **1.00** |
| WithValueTask | 0                  | 79,052.8 ns | 4,536.82 ns | 248.68 ns |  1.04 | 1.8311 |   11384 B |        1.08 |
|               |                    |             |             |           |       |        |           |             |
| **WithTask**      | **50**                 | **44,806.1 ns** | **6,842.49 ns** | **375.06 ns** |  **1.00** | **1.3428** |    **8328 B** |        **1.00** |
| WithValueTask | 50                 | 45,406.3 ns | 5,378.21 ns | 294.80 ns |  1.01 | 0.9155 |    5783 B |        0.69 |
|               |                    |             |             |           |       |        |           |             |
| **WithTask**      | **100**                |  **1,038.0 ns** |    **69.70 ns** |   **3.82 ns** |  **1.00** | **1.0557** |    **6624 B** |        **1.00** |
| WithValueTask | 100                |    492.0 ns |   260.75 ns |  14.29 ns |  0.47 | 0.0114 |      72 B |        0.01 |
