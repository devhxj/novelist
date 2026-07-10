param(
 [int]$Rounds = 2,
 [string]$Configuration = "Debug",
 [string]$Output = "build/tmp/corpus-driven-writing/recovery-metrics.json",
 [int]$CheckpointTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$hostProject = Join-Path $PSScriptRoot "CorpusHarnessHost/CorpusHarnessHost.csproj"
$workRoot = Join-Path $repoRoot "build/tmp/corpus-driven-writing/recovery"
$outputPath = Join-Path $repoRoot $Output
$points = @("after_reservation", "after_model", "after_record", "during_finalize", "after_commit")

function Wait-Checkpoint([string]$Path, [System.Diagnostics.Process]$Process) {
 $deadline = [DateTimeOffset]::UtcNow.AddSeconds($CheckpointTimeoutSeconds)
 while ([DateTimeOffset]::UtcNow -lt $deadline) {
 if (Test-Path -LiteralPath $Path) { return }
 if ($Process.HasExited) { throw "Harness host exited before checkpoint. exit=$($Process.ExitCode)" }
 Start-Sleep -Milliseconds 50
 }
 throw "Timed out waiting for checkpoint $Path"
}

function Read-JsonOutput([string]$StdoutPath) {
 $text = (Get-Content -LiteralPath $StdoutPath -Raw).Trim()
 if (-not $text) { throw "Harness host produced no JSON output: $StdoutPath" }
 return $text | ConvertFrom-Json
}

New-Item -ItemType Directory -Force -Path $workRoot, (Split-Path $outputPath) | Out-Null
Push-Location $repoRoot
try {
 & dotnet build $hostProject -c $Configuration -v minimal
 if ($LASTEXITCODE -ne 0) { throw "Harness host build failed." }
 $hostDll = Join-Path $PSScriptRoot "CorpusHarnessHost/bin/$Configuration/net10.0/Novelist.IntegrationTests.dll"
 $results = @()
 for ($round = 1; $round -le $Rounds; $round++) {
 foreach ($point in $points) {
 $scenarioId = "round-$round-$point"
 $scenarioRoot = Join-Path $workRoot $scenarioId
 if (Test-Path -LiteralPath $scenarioRoot) { Remove-Item -LiteralPath $scenarioRoot -Recurse -Force }
 New-Item -ItemType Directory -Force -Path $scenarioRoot | Out-Null
 $database = Join-Path $scenarioRoot "novelist.db"
 $checkpoint = Join-Path $scenarioRoot "checkpoint.txt"
 $scenario = Join-Path $scenarioRoot "scenario.json"
 $modelResult = Join-Path $scenarioRoot "model-result.txt"
 $faultStdout = Join-Path $scenarioRoot "fault.stdout.log"
 $faultStderr = Join-Path $scenarioRoot "fault.stderr.log"
 $faultArgs = @(
 $hostDll, "fault", "--database", $database, "--checkpoint", $checkpoint,
 "--scenario", $scenario, "--model-result", $modelResult,
 "--point", $point, "--scenario-id", $scenarioId
 )
 $process = Start-Process dotnet -ArgumentList $faultArgs -PassThru -WindowStyle Hidden -RedirectStandardOutput $faultStdout -RedirectStandardError $faultStderr
 try {
 Wait-Checkpoint $checkpoint $process
 Stop-Process -Id $process.Id -Force
 $process.WaitForExit()
 }
 finally {
 if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
 $process.Dispose()
 }

 $recoverStdout = Join-Path $scenarioRoot "recover.stdout.json"
 $recoverStderr = Join-Path $scenarioRoot "recover.stderr.log"
 $recoverArgs = @($hostDll, "recover", "--database", $database, "--scenario", $scenario)
 $recover = Start-Process dotnet -ArgumentList $recoverArgs -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $recoverStdout -RedirectStandardError $recoverStderr
 if ($recover.ExitCode -ne 0) { throw "Recovery failed for $scenarioId. $(Get-Content $recoverStderr -Raw)" }
 $result = Read-JsonOutput $recoverStdout
 $results += $result
 Write-Output ("recovery round={0} point={1} passed={2} recovery_ms={3:N2}" -f $round, $point, $result.passed, $result.recovery_ms)
 }
 }
 $recoveryValues = @($results | ForEach-Object { [double]$_.recovery_ms } | Sort-Object)
 function Percentile([double[]]$Values, [double]$P) {
 if ($Values.Count -eq 0) { return 0 }
 $index = [math]::Max(0, [math]::Min($Values.Count - 1, [math]::Ceiling($P * $Values.Count) - 1))
 return $Values[$index]
 }
 $report = [ordered]@{
 schema_version = "corpus-m2-recovery-metrics-v1"
 generated_at = [DateTimeOffset]::UtcNow.ToString("O")
 rounds = $Rounds
 fault_points = $points
 cases = $results
 recovery_ms = [ordered]@{ count = $recoveryValues.Count; p50 = Percentile $recoveryValues 0.50; p95 = Percentile $recoveryValues 0.95; max = ($recoveryValues | Measure-Object -Maximum).Maximum }
 zero_duplicate_cases = @($results | Where-Object { $_.audit.duplicateOutputRows -eq 0 }).Count
 zero_loss_cases = @($results | Where-Object { $_.audit.outputRows -eq 1 -and $_.audit.succeededWorkItems -eq 1 }).Count
 exact_token_cases = @($results | Where-Object { $_.audit.tokensSpent -eq $_.audit.expectedTokensSpent -and $_.audit.tokensReserved -eq 0 }).Count
 passed = @($results | Where-Object { -not $_.passed }).Count -eq 0
 }
 $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $outputPath -Encoding utf8
 if (-not $report.passed) { throw "Recovery harness failed. See $outputPath" }
 Write-Output "metrics=$outputPath"
}
finally {
 Pop-Location
}
