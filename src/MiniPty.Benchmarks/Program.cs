using BenchmarkDotNet.Running;

// Release configuration is required for meaningful timings and allocation stats.
// Examples:
//   dotnet run -c Release --project src/MiniPty.Benchmarks
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *categories*Binary*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *categories*Text*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *Startup*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --burst-probe [sessions] [bytes] [interval ms] [bursts]
if (args.Length > 0 && args[0] == "--burst-probe")
    return await MiniPty.Benchmarks.BurstProbe.RunAsync(args[1..]);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
