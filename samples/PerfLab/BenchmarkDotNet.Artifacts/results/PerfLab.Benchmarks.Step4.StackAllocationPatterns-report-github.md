```

BenchmarkDotNet v0.15.8, Windows 10 (10.0.19045.6456/22H2/2022Update)
13th Gen Intel Core i5-13420H 2.10GHz, 1 CPU, 12 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  Job-BBNZIP : .NET 10.0.4 (10.0.4, 10.0.426.12010), X64 RyuJIT x86-64-v3
  Job-HDDFYY : .NET 8.0.27 (8.0.27, 8.0.2726.22922), X64 RyuJIT x86-64-v3

IterationCount=5  WarmupCount=3  

```
| Method                                      | Runtime   | Mean       | Ratio | Gen0   | Allocated | Alloc Ratio |
|-------------------------------------------- |---------- |-----------:|------:|-------:|----------:|------------:|
| &#39;small array, stays local&#39;                  | .NET 10.0 |  2.0227 ns |  0.45 |      - |         - |        0.00 |
| &#39;small array, stays local&#39;                  | .NET 8.0  |  4.5274 ns |  1.00 | 0.0064 |      40 B |        1.00 |
|                                             |           |            |       |        |           |             |
| &#39;small array, stored in a field (escapes)&#39;  | .NET 10.0 | 10.1741 ns |  1.38 | 0.0064 |      40 B |        1.00 |
| &#39;small array, stored in a field (escapes)&#39;  | .NET 8.0  |  7.3626 ns |  1.00 | 0.0064 |      40 B |        1.00 |
|                                             |           |            |       |        |           |             |
| &#39;delegate, stays local&#39;                     | .NET 10.0 | 46.4228 ns |  0.89 | 0.0038 |      24 B |        0.27 |
| &#39;delegate, stays local&#39;                     | .NET 8.0  | 51.9580 ns |  1.00 | 0.0139 |      88 B |        1.00 |
|                                             |           |            |       |        |           |             |
| &#39;delegate, returned via a field (escapes)&#39;  | .NET 10.0 | 53.4878 ns |  1.04 | 0.0140 |      88 B |        1.00 |
| &#39;delegate, returned via a field (escapes)&#39;  | .NET 8.0  | 51.5843 ns |  1.00 | 0.0140 |      88 B |        1.00 |
|                                             |           |            |       |        |           |             |
| &#39;object via non-escaping struct field&#39;      | .NET 10.0 |  0.7603 ns |  0.22 |      - |         - |        0.00 |
| &#39;object via non-escaping struct field&#39;      | .NET 8.0  |  3.4848 ns |  1.00 | 0.0064 |      40 B |        1.00 |
|                                             |           |            |       |        |           |             |
| &#39;array via IEnumerable (enumerator boxing)&#39; | .NET 10.0 | 41.4460 ns |  0.44 |      - |         - |        0.00 |
| &#39;array via IEnumerable (enumerator boxing)&#39; | .NET 8.0  | 94.7448 ns |  1.00 | 0.0050 |      32 B |        1.00 |
|                                             |           |            |       |        |           |             |
| &#39;array directly (control)&#39;                  | .NET 10.0 | 42.2513 ns |  1.06 |      - |         - |          NA |
| &#39;array directly (control)&#39;                  | .NET 8.0  | 39.8081 ns |  1.00 |      - |         - |          NA |
