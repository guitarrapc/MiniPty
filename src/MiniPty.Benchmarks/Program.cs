using BenchmarkDotNet.Running;

// Release configuration is required for meaningful timings and allocation stats.
// Examples:
//   dotnet run -c Release --project src/MiniPty.Benchmarks
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *categories*Binary*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *categories*Text*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *Startup*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --burst-probe [sessions] [bytes] [interval ms] [bursts]
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --alloc-probe <none|in-box|out-of-band> [listener 0|1] [rounds] [payload bytes] [warm-up rounds] [list threads 1|0]
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --start-probe [rounds] [program [arguments...]]
if (args.Length > 0 && args[0] == "--burst-probe")
    return await MiniPty.Benchmarks.BurstProbe.RunAsync(args[1..]);

if (args.Length > 0 && args[0] == "--alloc-probe")
    return MiniPty.Benchmarks.AllocProbe.Run(args[1..]);

if (args.Length > 0 && args[0] == "--start-probe")
    return await MiniPty.Benchmarks.StartProbe.RunAsync(args[1..]);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
