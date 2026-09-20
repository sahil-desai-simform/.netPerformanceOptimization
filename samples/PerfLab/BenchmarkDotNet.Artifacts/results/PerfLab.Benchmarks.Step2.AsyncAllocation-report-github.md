```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                      | Mean          | Error       | StdDev     | Ratio    | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------------------------------- |--------------:|------------:|-----------:|---------:|--------:|-------:|----------:|------------:|
| &#39;no await at all&#39;                           |     0.4778 ns |   0.7391 ns |  0.0405 ns |     1.00 |    0.10 |      - |         - |          NA |
| &#39;async, completes synchronously&#39;            |     7.8738 ns |   1.8815 ns |  0.1031 ns |    16.55 |    1.18 | 0.0115 |      72 B |          NA |
| &#39;async, suspends (boxes the state machine)&#39; | 1,027.1812 ns | 148.7598 ns |  8.1540 ns | 2,159.65 |  152.77 | 0.0153 |      96 B |          NA |
| &#39;Task.Run wrapping sync work&#39;               |   983.4172 ns | 125.2482 ns |  6.8653 ns | 2,067.63 |  146.11 | 0.0114 |      72 B |          NA |
| &#39;suspends, empty ExecutionContext&#39;          |   996.3603 ns | 106.4533 ns |  5.8351 ns | 2,094.84 |  147.87 | 0.0153 |      96 B |          NA |
| &#39;suspends, AsyncLocal populated&#39;            | 1,084.4773 ns | 300.8804 ns | 16.4923 ns | 2,280.11 |  163.33 | 0.0267 |     168 B |          NA |
| &#39;suspends, flow suppressed&#39;                 | 1,075.0816 ns | 471.1014 ns | 25.8227 ns | 2,260.36 |  165.97 | 0.0534 |     343 B |          NA |
