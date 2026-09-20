```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method               | Mean        | Error       | StdDev     | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------- |------------:|------------:|-----------:|------:|--------:|----------:|------------:|
| Sequential           | 1,436.49 ms | 1,914.56 ms | 104.943 ms | 1.004 |    0.09 |  25.96 KB |        1.00 |
| WhenAllUnbounded     |    13.85 ms |    25.34 ms |   1.389 ms | 0.010 |    0.00 |  28.06 KB |        1.08 |
| ParallelForEachAsync |   184.60 ms |   526.70 ms |  28.870 ms | 0.129 |    0.02 |  37.69 KB |        1.45 |
| SemaphoreThrottled   |   184.63 ms |   290.06 ms |  15.899 ms | 0.129 |    0.01 |   45.9 KB |        1.77 |
| ChannelPipeline      |   199.80 ms |   275.82 ms |  15.119 ms | 0.140 |    0.01 |  30.98 KB |        1.19 |
