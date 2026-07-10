param(
 [string]$Configuration = "Debug",
 [int]$MinimumCharacters = 2000000,
 [int]$JobSize = 100,
 [double]$MinimumThroughput = 20,
 [double]$MaximumClaimP95Ms = 100,
 [double]$MaximumListP95Ms = 200,
 [string]$Output = "build/tmp/corpus-driven-writing/scale-metrics.json"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$hostProject = Join-Path $PSScriptRoot "CorpusHarnessHost/CorpusHarnessHost.csproj"
$fixturePath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/scale-2m.jsonl"
$databasePath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/scale/novelist.db"
$outputPath = Join-Path $repoRoot $Output
$stdoutPath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/scale.stdout.json"
$stderrPath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/scale.stderr.log"

Push-Location $repoRoot
try {
 & $PSScriptRoot/generate-fixtures.ps1 -ScaleCharacterCount $MinimumCharacters -ScaleOutput "build/tmp/corpus-driven-writing/scale-2m.jsonl"
 & dotnet build $hostProject -c $Configuration -v minimal
 if ($LASTEXITCODE -ne 0) { throw "Harness host build failed." }
 $hostDll = Join-Path $PSScriptRoot "CorpusHarnessHost/bin/$Configuration/net10.0/Novelist.IntegrationTests.dll"
 New-Item -ItemType Directory -Force -Path (Split-Path $databasePath), (Split-Path $outputPath) | Out-Null
 $arguments = @(
 $hostDll, "scale", "--database", $databasePath, "--fixture", $fixturePath,
 "--minimum-characters", $MinimumCharacters, "--job-size", $JobSize,
 "--minimum-throughput", $MinimumThroughput,
 "--maximum-claim-p95-ms", $MaximumClaimP95Ms,
 "--maximum-list-p95-ms", $MaximumListP95Ms
 )
 $process = Start-Process dotnet -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
 if ($process.ExitCode -ne 0) { throw "Scale harness failed. $(Get-Content $stderrPath -Raw)" }
 $result = (Get-Content -LiteralPath $stdoutPath -Raw) | ConvertFrom-Json
 $report = [ordered]@{
 schema_version = "corpus-m2-scale-metrics-v1"
 generated_at = [DateTimeOffset]::UtcNow.ToString("O")
 result = $result
 }
 $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $outputPath -Encoding utf8
 if (-not $result.passed) { throw "Scale thresholds failed. See $outputPath" }
 Write-Output "metrics=$outputPath"
}
finally {
 Pop-Location
}
