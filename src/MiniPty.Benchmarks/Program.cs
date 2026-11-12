using BenchmarkDotNet.Running;

// Release configuration is required for meaningful timings and allocation stats.
// Examples:
//   dotnet run -c Release --project src/MiniPty.Benchmarks
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *categories*Binary*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *categories*Text*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --filter *Startup*
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --burst-probe [sessions] [bytes] [interval ms] [bursts]
//   dotnet run -c Release --project src/MiniPty.Benchmarks -- --flood-probe <sustained|slow|echo|neighbors|control|oneshot> [--option value ...]
if (args.Length > 0 && args[0] == "--burst-probe")
    return await MiniPty.Benchmarks.BurstProbe.RunAsync(args[1..]);

if (args.Length > 0 && args[0] == "--flood-probe")
{
    // Exit outright: Porta.Pty can leave a foreground thread behind that would keep the process running.
    Environment.Exit(await MiniPty.Benchmarks.FloodProbe.RunAsync(args[1..]));
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
