#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable

// Rewrites the benchmark tables in README.md from the reports of one benchmark workflow run.
//
// The README's Performance section quotes numbers that a reader should find, as written, in the run it links to.
// Copying them by hand let the tables and the link point at different runs, and converted units (μs to ms, bytes to
// KiB) made a quoted number impossible to find in the report. This script takes every cell from the BenchmarkDotNet
// CSV reports of one run, in the reports' own units, and replaces only the blocks between the README's markers:
//
//     <!-- benchmark:<name>:start -->  ...  <!-- benchmark:<name>:end -->
//
// The prose around the markers is left alone. The benchmark workflow runs this after a full run and commits the
// result; run it locally on a downloaded run the same way:
//
//     gh run download <run id> -D <dir>
//     dotnet run scripts/UpdateReadmeBenchmarks.cs -- --artifacts <dir> --run-url https://github.com/<owner>/<repo>/actions/runs/<run id>
//
// Options:
//     --artifacts <dir>   One BenchmarkDotNet.Artifacts-<suffix> directory per runner, as actions/download-artifact
//                         and `gh run download` lay them out. Required.
//     --run-url <url>     The workflow run the reports come from. Required.
//     --run-date <date>   The date the run is quoted with, UTC today by default.
//     --readme <path>     The README to rewrite, README.md by default.
//
// Exit codes: 0 when the README was rewritten, 1 when a report or a README marker is missing or malformed (the README
// is then left untouched), 2 for a usage error.

using System.Globalization;
using System.Text;

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Usage: dotnet run scripts/UpdateReadmeBenchmarks.cs -- --artifacts <dir> --run-url <url> [--run-date <yyyy-MM-dd>] [--readme <path>]");
    return 2;
}

try
{
    Readme.Rewrite(options.ReadmePath, BenchmarkTables.Build(options));
    Console.WriteLine($"Rewrote the benchmark tables in {options.ReadmePath} from run {options.RunId}.");
    return 0;
}
catch (Exception ex) when (ex is InvalidDataException or IOException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

/// <summary>The command line.</summary>
sealed record Options(string ArtifactsRoot, string RunUrl, string RunDate, string ReadmePath)
{
    /// <summary>The run's number, the last segment of its URL.</summary>
    public string RunId
    {
        get
        {
            var url = RunUrl.TrimEnd('/');
            return url[(url.LastIndexOf('/') + 1)..];
        }
    }

    public static Options Parse(string[] args)
    {
        string? artifacts = null, runUrl = null, runDate = null, readme = null;
        for (var i = 0; i < args.Length; i += 2)
        {
            var name = args[i];
            if (i + 1 >= args.Length)
                throw new ArgumentException($"{name} needs a value.");

            var value = args[i + 1];
            switch (name)
            {
                case "--artifacts": artifacts = value; break;
                case "--run-url": runUrl = value; break;
                case "--run-date": runDate = value; break;
                case "--readme": readme = value; break;
                default: throw new ArgumentException($"Unknown option {name}.");
            }
        }

        if (artifacts is null)
            throw new ArgumentException("--artifacts is required.");
        if (runUrl is null)
            throw new ArgumentException("--run-url is required.");

        return new Options(
            artifacts,
            runUrl,
            runDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            readme ?? "README.md");
    }
}

/// <summary>One job of the benchmark matrix in .github/workflows/benchmark.yaml.</summary>
/// <param name="Suffix">The artifact's suffix: <c>BenchmarkDotNet.Artifacts-{Suffix}</c>.</param>
/// <param name="Name">The name the README uses.</param>
/// <param name="Label">The runner label, which is also how the run page names the job.</param>
sealed record Runner(string Suffix, string Name, string Label);

/// <summary>Builds the README's blocks from the reports, each cell copied as the report writes it.</summary>
static class BenchmarkTables
{
    // Keep in step with the matrix in .github/workflows/benchmark.yaml.
    private static readonly Runner[] Runners =
    [
        new("linux", "Linux", "ubuntu-24.04"),
        new("macos", "macOS", "macos-26"),
        new("windows", "Windows", "windows-2025"),
    ];

    // The ConPty parameter's values in table order: Unix has one, Windows runs the out-of-band and the in-box ConPTY.
    private static readonly string[] ConPtyOrder = ["none", "out-of-band", "in-box"];

    public static List<(string Name, List<string> Lines)> Build(Options options)
    {
        var runtimes = new SortedSet<string>(StringComparer.Ordinal);
        var machines = new List<string>();
        var latency = new List<string[]>();
        var throughput = new List<string[]>();
        var startup = new List<string[]>();

        foreach (var runner in Runners)
        {
            var results = Path.Combine(options.ArtifactsRoot, $"BenchmarkDotNet.Artifacts-{runner.Suffix}", "results");

            var machine = Machine.Read(ReportPath(results, "PtyLatencyBenchmarks", "-github.md"));
            runtimes.Add(machine.Runtime);
            machines.Add($"- {runner.Name} (`{runner.Label}`): {machine.Cpu}");

            var report = Report.Load(ReportPath(results, "PtyLatencyBenchmarks", ".csv"));
            foreach (var conPty in ConPtyValues(report))
            {
                var mini = report.Get("MiniPty", ("ConPty", conPty));
                var porta = report.Get("PortaPty", ("ConPty", conPty));
                latency.Add([
                    runner.Name,
                    conPty,
                    $"{mini["P50"]} / {mini["P95"]}",
                    $"{porta["P50"]} / {porta["P95"]}",
                    $"{mini["Allocated"]} / {porta["Allocated"]}",
                ]);
            }

            report = Report.Load(ReportPath(results, "PtyThroughputBenchmarks", ".csv"));
            foreach (var conPty in ConPtyValues(report))
            {
                var mini = report.Get("MiniPty", ("ConPty", conPty), ("PayloadBytes", "1048576"));
                var porta = report.Get("PortaPty", ("ConPty", conPty), ("PayloadBytes", "1048576"));
                throughput.Add([
                    runner.Name,
                    conPty,
                    mini["MiB/s"],
                    porta["MiB/s"],
                    $"{mini["Allocated"]} / {porta["Allocated"]}",
                ]);
            }

            report = Report.Load(ReportPath(results, "PtyStartupBenchmarks", ".csv"));
            var exit = report.Get("Session_Exit0_WaitForExit");
            var echo = report.Get("Session_Echo_Bytes");
            startup.Add([
                runner.Name,
                $"{exit["Mean"]} / {exit["Allocated"]}",
                $"{echo["Mean"]} / {echo["Allocated"]}",
            ]);
        }

        var run = new List<string>
        {
            $"The numbers below are from [run {options.RunId}]({options.RunUrl}) on {options.RunDate}, with {string.Join(" and ", runtimes)} on GitHub Actions hosted runners:",
            "",
        };
        run.AddRange(machines);

        return
        [
            ("run", run),
            ("latency", Table(["Runner", "`ConPty`", "`MiniPty` P50 / P95", "`PortaPty` P50 / P95", "Allocated (`MiniPty` / `PortaPty`)"], latency)),
            ("throughput", Table(["Runner", "`ConPty`", "`MiniPty` MiB/s", "`PortaPty` MiB/s", "Allocated (`MiniPty` / `PortaPty`)"], throughput)),
            ("startup", Table(["Runner", "`Session_Exit0_WaitForExit` Mean / Allocated", "`Session_Echo_Bytes` Mean / Allocated"], startup)),
        ];
    }

    private static string ReportPath(string results, string benchmark, string suffix)
    {
        var path = Path.Combine(results, $"MiniPty.Benchmarks.{benchmark}-report{suffix}");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Report not found: {path}. Was the run filtered to fewer benchmarks than '*'?", path);
        return path;
    }

    // The report's ConPty values in table order. The out-of-band ConPTY is a case only where it was staged.
    private static List<string> ConPtyValues(Report report)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in report.Rows)
        {
            var value = row["ConPty"];
            if (Array.IndexOf(ConPtyOrder, value) < 0)
                throw new InvalidDataException($"Unknown ConPty value '{value}' in {report.Path}.");
            present.Add(value);
        }

        var ordered = new List<string>(present.Count);
        foreach (var value in ConPtyOrder)
        {
            if (present.Contains(value))
                ordered.Add(value);
        }
        return ordered;
    }

    private static List<string> Table(string[] header, List<string[]> rows)
    {
        var separator = new string[header.Length];
        for (var i = 0; i < header.Length; i++)
            separator[i] = new string('-', header[i].Length + 2);

        var lines = new List<string>(rows.Count + 2)
        {
            "| " + string.Join(" | ", header) + " |",
            "|" + string.Join("|", separator) + "|",
        };
        foreach (var row in rows)
            lines.Add("| " + string.Join(" | ", row) + " |");
        return lines;
    }
}

/// <summary>The machine a runner's reports were measured on, from a BenchmarkDotNet Markdown report's header.</summary>
/// <param name="Cpu">The CPU line, as the report writes it.</param>
/// <param name="Runtime">The host runtime, such as <c>.NET 10.0.14</c>.</param>
sealed record Machine(string Cpu, string Runtime)
{
    // The CPU line follows the "BenchmarkDotNet v..., <OS>" line, and the runtime is on the [Host] line:
    //
    //     BenchmarkDotNet v0.15.6, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
    //     AMD EPYC 9V45 4.26GHz, 1 CPU, 4 logical and 2 physical cores
    //     .NET SDK 10.0.403
    //       [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
    public static Machine Read(string path)
    {
        var lines = File.ReadAllLines(path);
        string? cpu = null;
        string? runtime = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (cpu is null && line.StartsWith("BenchmarkDotNet v", StringComparison.Ordinal) && i + 1 < lines.Length)
                cpu = lines[i + 1].Trim();

            if (runtime is null && line.StartsWith("[Host]", StringComparison.Ordinal))
            {
                var host = line[(line.IndexOf(':') + 1)..].Trim();
                var end = host.IndexOf(' ', ".NET ".Length);
                if (host.StartsWith(".NET ", StringComparison.Ordinal) && end > 0)
                    runtime = host[..end];
            }
        }

        if (cpu is null || runtime is null)
            throw new InvalidDataException($"No machine lines (CPU and [Host] runtime) in {path}.");
        return new Machine(cpu, runtime);
    }
}

/// <summary>A BenchmarkDotNet CSV report: one row per benchmark case, every value as the summary table writes it.</summary>
sealed class Report
{
    private readonly Dictionary<string, int> columns;

    private Report(string path, Dictionary<string, int> columns, List<Row> rows)
    {
        Path = path;
        this.columns = columns;
        Rows = rows;
    }

    public string Path { get; }

    public List<Row> Rows { get; }

    public static Report Load(string path)
    {
        var lines = File.ReadAllLines(path);
        // The separator follows the culture BenchmarkDotNet ran in, so take it from the header's first column name.
        if (lines.Length == 0 || !lines[0].StartsWith("Method", StringComparison.Ordinal) || lines[0].Length <= "Method".Length)
            throw new InvalidDataException($"{path} does not start with a Method column.");

        var separator = lines[0]["Method".Length];
        var header = ParseLine(lines[0], separator, path);
        var columns = new Dictionary<string, int>(header.Length, StringComparer.Ordinal);
        for (var i = 0; i < header.Length; i++)
            columns[header[i]] = i;

        var report = new Report(path, columns, []);
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
                continue;

            var fields = ParseLine(lines[i], separator, path);
            if (fields.Length != header.Length)
                throw new InvalidDataException($"Line {i + 1} of {path} has {fields.Length} fields, the header {header.Length}.");
            report.Rows.Add(new Row(report, fields));
        }
        return report;
    }

    /// <summary>The one row of a method whose parameters have the given values.</summary>
    public Row Get(string method, params (string Column, string Value)[] parameters)
    {
        Row? found = null;
        foreach (var row in Rows)
        {
            if (row["Method"] != method || !Matches(row, parameters))
                continue;
            if (found is not null)
                throw new InvalidDataException($"More than one row for {Describe(method, parameters)} in {Path}.");
            found = row;
        }

        return found ?? throw new InvalidDataException($"No row for {Describe(method, parameters)} in {Path}.");
    }

    internal int Column(string name) =>
        columns.TryGetValue(name, out var index) ? index : throw new InvalidDataException($"No {name} column in {Path}.");

    private static bool Matches(Row row, (string Column, string Value)[] parameters)
    {
        foreach (var (column, value) in parameters)
        {
            if (row[column] != value)
                return false;
        }
        return true;
    }

    private static string Describe(string method, (string Column, string Value)[] parameters)
    {
        var text = new StringBuilder(method);
        foreach (var (column, value) in parameters)
            text.Append(' ').Append(column).Append('=').Append(value);
        return text.ToString();
    }

    // One CSV line: fields with the separator or a quote are quoted, and a quote inside them is doubled.
    private static string[] ParseLine(string line, char separator, string path)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"')
                    field.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"')
                    field.Append(line[++i]);
                else
                    quoted = false;
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (quoted)
            throw new InvalidDataException($"Unterminated quote in {path}: {line}");
        fields.Add(field.ToString());
        return [.. fields];
    }
}

/// <summary>One benchmark case of a <see cref="Report"/>.</summary>
sealed class Row(Report report, string[] fields)
{
    public string this[string column] => fields[report.Column(column)];
}

/// <summary>Replaces the README's marked blocks, keeping its encoding (BOM or not) and line endings.</summary>
static class Readme
{
    public static void Rewrite(string path, List<(string Name, List<string> Lines)> blocks)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.AsSpan().StartsWith("﻿"u8);
        var encoding = new UTF8Encoding(hasBom);
        var bom = hasBom ? 3 : 0;
        var text = encoding.GetString(bytes, bom, bytes.Length - bom);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // Every block is replaced in memory first, so a missing marker leaves the file untouched.
        foreach (var (name, lines) in blocks)
            text = Replace(text, name, lines, newline);

        File.WriteAllText(path, text, encoding);
    }

    private static string Replace(string text, string name, List<string> lines, string newline)
    {
        var start = $"<!-- benchmark:{name}:start -->";
        var end = $"<!-- benchmark:{name}:end -->";
        var startAt = text.IndexOf(start, StringComparison.Ordinal);
        var endAt = text.IndexOf(end, StringComparison.Ordinal);
        if (startAt < 0
            || endAt < startAt
            || text.IndexOf(start, startAt + start.Length, StringComparison.Ordinal) >= 0
            || text.IndexOf(end, endAt + end.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidDataException($"README must have exactly one {start} ... {end} block, in that order.");
        }

        // Blank lines around the content keep a table or list from running into the comment lines in Markdown.
        var block = start + newline + newline + string.Join(newline, lines) + newline + newline + end;
        return string.Concat(text.AsSpan(0, startAt), block, text.AsSpan(endAt + end.Length));
    }
}
