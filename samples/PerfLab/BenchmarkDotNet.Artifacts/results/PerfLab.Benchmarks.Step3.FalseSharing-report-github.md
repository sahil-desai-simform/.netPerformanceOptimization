```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                  | Mean      | Error     | StdDev    | Ratio | Allocated | Alloc Ratio |
|------------------------ |----------:|----------:|----------:|------:|----------:|------------:|
| Packed                  | 30.589 ms | 5.1436 ms | 0.2819 ms |  1.00 |   4.16 KB |        1.00 |
| Padded                  | 11.191 ms | 3.4521 ms | 0.1892 ms |  0.37 |   3.89 KB |        0.94 |
| ThreadLocalAccumulation |  1.173 ms | 0.2847 ms | 0.0156 ms |  0.04 |   4.43 KB |        1.07 |
