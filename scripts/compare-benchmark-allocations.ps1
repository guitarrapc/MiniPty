param(
    [string]$BaselinePath = "BenchmarkDotNet.Artifacts/baselines/integration.json",
    [string]$ResultsDirectory = "BenchmarkDotNet.Artifacts/results"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $BaselinePath)) {
    Write-Error "Baseline file not found: $BaselinePath"
}

$baseline = Get-Content $BaselinePath -Raw | ConvertFrom-Json
$csv = Get-ChildItem -Path $ResultsDirectory -Filter "MiniPty.Benchmarks.PtyIntegrationBenchmarks-report.csv" -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $csv) {
    Write-Error "No PtyIntegrationBenchmarks CSV found under $ResultsDirectory. Run benchmarks first."
}

$rows = Import-Csv $csv.FullName
$failures = New-Object System.Collections.Generic.List[string]
$improvements = New-Object System.Collections.Generic.List[string]

foreach ($property in $baseline.benchmarks.PSObject.Properties) {
    $name = $property.Name
    $expected = [long]$property.Value
    $row = $rows | Where-Object { $_.Method -eq $name } | Select-Object -First 1
    if (-not $row) {
        $failures.Add("MISSING: $name (not in benchmark results)")
        continue
    }

    $allocatedText = $row.Allocated
    if ($allocatedText -match "^([\d.]+)\s*KB$") {
        $actual = [long]([double]$Matches[1] * 1024)
    }
    elseif ($allocatedText -match "^([\d.]+)\s*B$") {
        $actual = [long]$Matches[1]
    }
    else {
        $failures.Add("PARSE: $name allocated '$allocatedText'")
        continue
    }

    if ($actual -gt $expected) {
        $failures.Add("REGRESSION: $name allocated $actual B (baseline $expected B, +$($actual - $expected) B)")
    }
    elseif ($actual -lt $expected) {
        $improvements.Add("IMPROVED: $name allocated $actual B (baseline $expected B, -$($expected - $actual) B)")
    }
    else {
        Write-Host "OK: $name allocated $actual B"
    }
}

foreach ($line in $improvements) {
    Write-Host $line
}

if ($failures.Count -gt 0) {
    Write-Error ($failures -join [Environment]::NewLine)
}

Write-Host "Allocation check passed against baseline commit $($baseline.baselineCommit)."
