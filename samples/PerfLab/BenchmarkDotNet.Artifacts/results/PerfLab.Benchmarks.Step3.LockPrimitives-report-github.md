```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                         | Mean        | Error        | StdDev     | Ratio  | RatioSD | Completed Work Items | Lock Contentions | Gen0   | Allocated | Alloc Ratio |
|----------------------------------------------- |------------:|-------------:|-----------:|-------:|--------:|---------------------:|-----------------:|-------:|----------:|------------:|
| &#39;uncontended: no lock&#39;                         |    21.99 μs |     2.049 μs |   0.112 μs |   1.00 |    0.01 |                    - |                - |      - |         - |          NA |
| &#39;uncontended: Interlocked&#39;                     |   395.18 μs |     9.857 μs |   0.540 μs |  17.97 |    0.08 |                    - |                - |      - |         - |          NA |
| &#39;uncontended: lock(object)&#39;                    | 1,589.41 μs |    33.626 μs |   1.843 μs |  72.27 |    0.33 |                    - |                - |      - |         - |          NA |
| &#39;uncontended: System.Threading.Lock [.NET 9]&#39;  | 1,425.67 μs |   227.338 μs |  12.461 μs |  64.83 |    0.57 |                    - |                - |      - |         - |          NA |
| &#39;uncontended: ReaderWriterLockSlim (write)&#39;    | 1,427.65 μs |   239.050 μs |  13.103 μs |  64.92 |    0.59 |                    - |                - |      - |         - |          NA |
| &#39;uncontended: SemaphoreSlim(1,1)&#39;              | 3,488.77 μs |   525.837 μs |  28.823 μs | 158.64 |    1.33 |                    - |                - |      - |         - |          NA |
| &#39;CONTENDED: lock(object)&#39;                      | 5,412.70 μs | 2,887.726 μs | 158.286 μs | 246.13 |    6.33 |              13.0000 |          14.6875 |      - |    4272 B |          NA |
| &#39;CONTENDED: System.Threading.Lock [.NET 9]&#39;    | 6,189.71 μs | 2,642.699 μs | 144.855 μs | 281.46 |    5.84 |              13.0000 |          23.8516 |      - |    4266 B |          NA |
| &#39;CONTENDED: Interlocked&#39;                       | 2,506.35 μs |   119.623 μs |   6.557 μs | 113.97 |    0.57 |              11.7305 |                - |      - |    3997 B |          NA |
| &#39;CONTENDED: no sharing (accumulate + combine)&#39; |    14.13 μs |     6.367 μs |   0.349 μs |   0.64 |    0.01 |               9.8549 |                - | 0.6561 |    4067 B |          NA |
