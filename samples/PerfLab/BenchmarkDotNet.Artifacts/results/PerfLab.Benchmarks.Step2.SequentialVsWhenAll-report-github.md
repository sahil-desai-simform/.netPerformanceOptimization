```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method         | Operations | Mean      | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------- |----------- |----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| **Sequential**     | **2**          |  **29.79 ms** |  **25.500 ms** |  **1.398 ms** |  **1.00** |    **0.06** |     **704 B** |        **1.00** |
| WhenAll        | 2          |  13.51 ms |  12.322 ms |  0.675 ms |  0.45 |    0.03 |     896 B |        1.27 |
| WhenAllViaLinq | 2          |  14.17 ms |  10.740 ms |  0.589 ms |  0.48 |    0.03 |     984 B |        1.40 |
|                |            |           |            |           |       |         |           |             |
| **Sequential**     | **10**         | **132.10 ms** | **189.294 ms** | **10.376 ms** |  **1.00** |    **0.10** |    **2816 B** |        **1.00** |
| WhenAll        | 10         |  14.83 ms |   5.612 ms |  0.308 ms |  0.11 |    0.01 |    3168 B |        1.12 |
| WhenAllViaLinq | 10         |  14.62 ms |   6.528 ms |  0.358 ms |  0.11 |    0.01 |    3256 B |        1.16 |
