```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                    | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|------------------------------------------ |-----------:|------------:|----------:|------:|--------:|--------:|-------:|----------:|------------:|
| &#39;Dictionary (not thread-safe)&#39;            |   5.135 μs |   0.7383 μs | 0.0405 μs |  1.00 |    0.01 |       - |      - |         - |          NA |
| ConcurrentDictionary                      |   4.835 μs |   4.8583 μs | 0.2663 μs |  0.94 |    0.05 |       - |      - |         - |          NA |
| &#39;FrozenDictionary [.NET 8]&#39;               |   5.774 μs |  13.5306 μs | 0.7417 μs |  1.12 |    0.13 |       - |      - |         - |          NA |
| &#39;ConcurrentDictionary: parallel GetOrAdd&#39; |  82.237 μs |  17.3437 μs | 0.9507 μs | 16.01 |    0.19 | 24.4141 | 5.7373 |  147780 B |          NA |
| &#39;Dictionary + lock: parallel add&#39;         | 208.165 μs | 131.1058 μs | 7.1864 μs | 40.54 |    1.24 | 14.1602 | 2.1973 |   77688 B |          NA |
