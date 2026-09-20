using BenchmarkDotNet.Running;

// PerfLab — companion code for the .NET Performance Optimization docs.
//
//   dotnet run -c Release -f net10.0 -- api                        the ASP.NET Core lab
//   dotnet run -c Release -f net10.0 -- bench --list flat          what benchmarks exist
//   dotnet run -c Release -f net10.0 -- bench --filter *Span*      run some of them
//   dotnet run -c Release -f net10.0 -- --filter *Span*            'bench' is optional
//
// The API targets net10.0 only (see PerfLab.csproj); the benchmarks build on net8.0/9.0/10.0
// so the cross-runtime comparison tables have something to compare.

string[] rest = args.Length > 0 ? args[1..] : [];

if (args.Length > 0 && args[0].Equals("api", StringComparison.OrdinalIgnoreCase))
{
#if NET10_0_OR_GREATER
    PerfLab.Api.ApiHost.Run(rest);
#else
    Console.Error.WriteLine("The API lab targets net10.0 only. Re-run with: -f net10.0");
    Environment.ExitCode = 1;
#endif
    return;
}

bool explicitBench = args.Length > 0 && args[0].Equals("bench", StringComparison.OrdinalIgnoreCase);
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(explicitBench ? rest : args);

public partial class Program;
