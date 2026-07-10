param(
 [int]$Rounds = 2,
 [int]$RuntimeSamples = 30,
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

 if ($RuntimeSamples -lt 1) { throw "RuntimeSamples must be at least 1." }
 $controlRoot = Join-Path $workRoot "runtime-control"
 $controlMetrics = Join-Path $workRoot "runtime-control-metrics.json"
 $controlStdout = Join-Path $workRoot "runtime-control.stdout.json"
 $controlStderr = Join-Path $workRoot "runtime-control.stderr.log"
 $controlArgs = @(
 $hostDll, "runtime-control", "--root", $controlRoot,
 "--samples", $RuntimeSamples,
 "--metrics-output", $controlMetrics
 )
 $controlProcess = Start-Process dotnet -ArgumentList $controlArgs -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $controlStdout -RedirectStandardError $controlStderr
 if ($controlProcess.ExitCode -ne 0) { throw "Runtime control harness failed. $(Get-Content $controlStderr -Raw)" }
 if (-not (Test-Path -LiteralPath $controlMetrics)) { throw "Runtime control harness wrote no metrics. See $controlStderr" }
 $runtimeControl = Get-Content -LiteralPath $controlMetrics -Raw | ConvertFrom-Json
 if ($runtimeControl.schema_version -ne "corpus-m2-runtime-control-metrics-v1" -or -not $runtimeControl.passed) {
 throw "Runtime control metrics failed. See $controlMetrics"
 }

 $staleRoot = Join-Path $workRoot "runtime-stale-lease"
 $staleMetrics = Join-Path $workRoot "runtime-stale-lease-metrics.json"
 $staleStdout = Join-Path $workRoot "runtime-stale-lease.stdout.json"
 $staleStderr = Join-Path $workRoot "runtime-stale-lease.stderr.log"
 $staleArgs = @(
 $hostDll, "runtime-stale-lease", "--root", $staleRoot,
 "--samples", $RuntimeSamples,
 "--metrics-output", $staleMetrics
 )
 $staleProcess = Start-Process dotnet -ArgumentList $staleArgs -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $staleStdout -RedirectStandardError $staleStderr
 if ($staleProcess.ExitCode -ne 0) { throw "Runtime stale lease harness failed. $(Get-Content $staleStderr -Raw)" }
 if (-not (Test-Path -LiteralPath $staleMetrics)) { throw "Runtime stale lease harness wrote no metrics. See $staleStderr" }
 $runtimeStaleLease = Get-Content -LiteralPath $staleMetrics -Raw | ConvertFrom-Json
 if ($runtimeStaleLease.schema_version -ne "corpus-m2-runtime-stale-lease-metrics-v1" -or -not $runtimeStaleLease.passed) {
 throw "Runtime stale lease metrics failed. See $staleMetrics"
 }

 $recoveryValues = @($results | ForEach-Object { [double]$_.recovery_ms } | Sort-Object)
 function Percentile([double[]]$Values, [double]$P) {
 if ($Values.Count -eq 0) { return 0 }
 $index = [math]::Max(0, [math]::Min($Values.Count - 1, [math]::Ceiling($P * $Values.Count) - 1))
 return $Values[$index]
 }
 $checkpointPassed = @($results | Where-Object { -not $_.passed }).Count -eq 0
 $report = [ordered]@{
 schema_version = "corpus-m2-recovery-metrics-v2"
 generated_at = [DateTimeOffset]::UtcNow.ToString("O")
 checkpoint_recovery = [ordered]@{
 rounds = $Rounds
 fault_points = $points
 cases = $results
 recovery_ms = [ordered]@{ count = $recoveryValues.Count; p50 = Percentile $recoveryValues 0.50; p95 = Percentile $recoveryValues 0.95; max = ($recoveryValues | Measure-Object -Maximum).Maximum }
 zero_duplicate_cases = @($results | Where-Object { $_.audit.duplicateOutputRows -eq 0 }).Count
 zero_loss_cases = @($results | Where-Object { $_.audit.outputRows -eq 1 -and $_.audit.succeededWorkItems -eq 1 }).Count
 exact_token_cases = @($results | Where-Object { $_.audit.tokensSpent -eq $_.audit.expectedTokensSpent -and $_.audit.tokensReserved -eq 0 }).Count
 passed = $checkpointPassed
 }
 runtime_wall_clock = [ordered]@{
 samples_per_control = $RuntimeSamples
 control = $runtimeControl
 stale_lease = $runtimeStaleLease
 }
 passed = $checkpointPassed -and $runtimeControl.passed -and $runtimeStaleLease.passed
 }
 $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $outputPath -Encoding utf8
 if (-not $report.passed) { throw "Recovery harness failed. See $outputPath" }
 Write-Output ("runtime pause p95_ms={0:N2} cancel p95_ms={1:N2} stale_reclaim p95_ms={2:N2}" -f $runtimeControl.pause.p95, $runtimeControl.cancel.p95, $runtimeStaleLease.reclaim_after_expiry_ms.p95)
 Write-Output "metrics=$outputPath"
}
finally {
 Pop-Location
}
