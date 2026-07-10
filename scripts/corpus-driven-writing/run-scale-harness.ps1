param(
 [ValidateSet("FullPipeline", "JobStore")]
 [string]$Mode = "FullPipeline",
 [string]$Configuration = "Debug",
 [int]$MinimumCharacters = 50000,
 [int]$JobSize = 100,
 [double]$MinimumThroughput = 20,
 [double]$MaximumClaimP95Ms = 100,
 [double]$MaximumListP95Ms = 200,
 [double]$MaximumProgressP95Ms = 200,
 [int]$MinimumLatencySamples = 30,
 [string]$Output = "build/tmp/corpus-driven-writing/scale-50k-metrics.json"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$hostProject = Join-Path $PSScriptRoot "CorpusHarnessHost/CorpusHarnessHost.csproj"
$fixtureName = if ($Mode -eq "FullPipeline") { "scale-50k.jsonl" } else { "scale-job-store-$MinimumCharacters.jsonl" }
$fixturePath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/$fixtureName"
$databaseDirectory = if ($Mode -eq "FullPipeline") { "scale-50k" } else { "scale-job-store" }
$databasePath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/$databaseDirectory/novelist.db"
$outputPath = Join-Path $repoRoot $Output
$stdoutPath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/$databaseDirectory.stdout.json"
$stderrPath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/$databaseDirectory.stderr.log"
$progressPath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/$databaseDirectory-progress.json"

Push-Location $repoRoot
try {
 $fixtureOutput = "build/tmp/corpus-driven-writing/$fixtureName"
 & $PSScriptRoot/generate-fixtures.ps1 -ScaleCharacterCount $MinimumCharacters -ScaleOutput $fixtureOutput -SkipGolden
 & dotnet build $hostProject -c $Configuration -v minimal
 if ($LASTEXITCODE -ne 0) { throw "Harness host build failed." }
 $hostDll = Join-Path $PSScriptRoot "CorpusHarnessHost/bin/$Configuration/net10.0/Novelist.IntegrationTests.dll"
New-Item -ItemType Directory -Force -Path (Split-Path $databasePath), (Split-Path $outputPath) | Out-Null
 Remove-Item -LiteralPath $outputPath, $progressPath -Force -ErrorAction SilentlyContinue
 $command = if ($Mode -eq "FullPipeline") { "scale-full" } else { "scale" }
 $arguments = @(
 $hostDll, $command, "--database", $databasePath, "--fixture", $fixturePath,
 "--minimum-characters", $MinimumCharacters, "--job-size", $JobSize,
 "--minimum-throughput", $MinimumThroughput,
 "--maximum-claim-p95-ms", $MaximumClaimP95Ms,
 "--maximum-list-p95-ms", $MaximumListP95Ms,
 "--maximum-progress-p95-ms", $MaximumProgressP95Ms,
 "--minimum-latency-samples", $MinimumLatencySamples,
 "--metrics-output", $outputPath,
 "--progress-output", $progressPath
 )
 $process = Start-Process dotnet -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
 if ($process.ExitCode -ne 0) { throw "Scale harness failed. $(Get-Content $stderrPath -Raw)" }
 if (-not (Test-Path -LiteralPath $outputPath)) { throw "Scale host exited without writing metrics. See $stderrPath" }
 $report = (Get-Content -LiteralPath $outputPath -Raw) | ConvertFrom-Json
 $expectedSchema = if ($Mode -eq "FullPipeline") { "corpus-m2-full-scale-metrics-v1" } else { "corpus-m2-scale-metrics-v1" }
 if ($report.schema_version -ne $expectedSchema -or $null -eq $report.result) {
 throw "Scale host wrote an invalid metrics envelope. See $outputPath"
 }
 if ($report.result.passed -ne $true) { throw "Scale thresholds failed. See $outputPath" }
 Write-Output "mode=$Mode"
Write-Output "metrics=$outputPath"
 Write-Output "progress=$progressPath"
}
finally {
 Pop-Location
}
