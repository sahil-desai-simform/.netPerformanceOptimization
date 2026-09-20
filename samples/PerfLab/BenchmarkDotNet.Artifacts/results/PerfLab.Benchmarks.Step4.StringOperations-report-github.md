```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                        | Mean      | Error     | StdDev    | Ratio | Gen0   | Allocated | Alloc Ratio |
|---------------------------------------------- |----------:|----------:|----------:|------:|-------:|----------:|------------:|
| string.Split                                  | 79.816 ns | 17.165 ns | 0.9409 ns |  1.00 | 0.0625 |     392 B |        1.00 |
| &#39;manual span scan&#39;                            | 29.249 ns | 13.581 ns | 0.7444 ns |  0.37 |      - |         - |        0.00 |
| &#39;MemoryExtensions.Split into ranges [.NET 8]&#39; | 45.038 ns |  6.736 ns | 0.3692 ns |  0.56 |      - |         - |        0.00 |
| &#39;Substring + int.Parse&#39;                       | 22.530 ns |  3.131 ns | 0.1716 ns |  0.28 | 0.0051 |      32 B |        0.08 |
| &#39;AsSpan slice + int.Parse&#39;                    | 15.129 ns |  6.416 ns | 0.3517 ns |  0.19 |      - |         - |        0.00 |
| &#39;IndexOfAny(char[])&#39;                          | 54.065 ns | 14.187 ns | 0.7776 ns |  0.68 |      - |         - |        0.00 |
| &#39;SearchValues [.NET 8]&#39;                       | 54.847 ns | 13.972 ns | 0.7659 ns |  0.69 |      - |         - |        0.00 |
| interpolation                                 | 24.245 ns | 16.958 ns | 0.9295 ns |  0.30 | 0.0153 |      96 B |        0.24 |
| &#39;string.Create with a stack buffer&#39;           |  9.468 ns |  3.180 ns | 0.1743 ns |  0.12 | 0.0089 |      56 B |        0.14 |
