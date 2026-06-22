using BenchmarkDotNet.Running;

// Release configuration is required for meaningful timings and allocation stats.
// Examples:
//   dotnet run -c Release --project benchmarks/MiniPty.Benchmarks
//   dotnet run -c Release --project benchmarks/MiniPty.Benchmarks -- --filter *PtyOutput*
//   dotnet run -c Release --project benchmarks/MiniPty.Benchmarks -- --filter *Integration*
//   dotnet run -c Release --project benchmarks/MiniPty.Benchmarks -- --filter *categories*Micro*
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
