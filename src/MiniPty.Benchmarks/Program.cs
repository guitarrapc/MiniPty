using BenchmarkDotNet.Running;

// Release configuration is required for meaningful timings and allocation stats.
// Examples:
//   dotnet run -c Release --project src/MiniPty.Benchmarks
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *categories*Binary*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *categories*Text*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *Integration*
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
