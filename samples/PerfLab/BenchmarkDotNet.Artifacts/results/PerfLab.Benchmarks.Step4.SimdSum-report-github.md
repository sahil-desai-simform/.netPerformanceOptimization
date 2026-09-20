```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]   : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                  | Length  | Mean          | Error         | StdDev       | Ratio | RatioSD | Allocated | Alloc Ratio |
|---------------------------------------- |-------- |--------------:|--------------:|-------------:|------:|--------:|----------:|------------:|
| **&#39;scalar loop&#39;**                           | **1024**    |     **459.30 ns** |     **131.04 ns** |     **7.183 ns** |  **1.00** |    **0.02** |         **-** |          **NA** |
| &#39;Vector&lt;T&gt; (portable width)&#39;            | 1024    |      70.70 ns |      19.69 ns |     1.079 ns |  0.15 |    0.00 |         - |          NA |
| &#39;Vector256&lt;T&gt; (fixed width + fallback)&#39; | 1024    |      71.88 ns |     102.03 ns |     5.593 ns |  0.16 |    0.01 |         - |          NA |
| &#39;TensorPrimitives.Sum [.NET 9]&#39;         | 1024    |      35.59 ns |      11.03 ns |     0.605 ns |  0.08 |    0.00 |         - |          NA |
|                                         |         |               |               |              |       |         |           |             |
| **&#39;scalar loop&#39;**                           | **1048576** | **512,662.79 ns** | **166,983.62 ns** | **9,152.936 ns** |  **1.00** |    **0.02** |         **-** |          **NA** |
| &#39;Vector&lt;T&gt; (portable width)&#39;            | 1048576 | 101,401.97 ns |  75,374.57 ns | 4,131.535 ns |  0.20 |    0.01 |         - |          NA |
| &#39;Vector256&lt;T&gt; (fixed width + fallback)&#39; | 1048576 |  98,895.06 ns |  35,959.06 ns | 1,971.037 ns |  0.19 |    0.00 |         - |          NA |
| &#39;TensorPrimitives.Sum [.NET 9]&#39;         | 1048576 |  85,646.39 ns |  11,471.79 ns |   628.808 ns |  0.17 |    0.00 |         - |          NA |
