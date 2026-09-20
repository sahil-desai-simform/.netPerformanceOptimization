```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                              | Mean      | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------ |----------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| &#39;return await Inner()&#39;              | 11.944 ns |  2.134 ns | 0.1170 ns |  1.00 |    0.01 | 0.0229 |     144 B |        1.00 |
| &#39;return Inner()  (elided)&#39;          |  3.675 ns |  2.028 ns | 0.1112 ns |  0.31 |    0.01 | 0.0115 |      72 B |        0.50 |
| &#39;return await Inner(), 3 frames&#39;    | 29.282 ns | 22.648 ns | 1.2414 ns |  2.45 |    0.09 | 0.0459 |     288 B |        2.00 |
| &#39;return Inner(), 3 frames (elided)&#39; |  3.705 ns |  1.632 ns | 0.0894 ns |  0.31 |    0.01 | 0.0115 |      72 B |        0.50 |
