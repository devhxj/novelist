param(
 [string]$Configuration = "Release",
 [int]$BenchmarkWorkItems = 1000
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$hostProject = Join-Path $PSScriptRoot "CorpusHarnessHost/CorpusHarnessHost.csproj"
$hostDll = Join-Path $PSScriptRoot "CorpusHarnessHost/bin/$Configuration/net10.0/Novelist.IntegrationTests.dll"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "novelist-scale-harness-$([Guid]::NewGuid().ToString('N'))"

function Assert-True([bool]$Condition, [string]$Message) {
 if (-not $Condition) { throw $Message }
}

try {
 New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
 $fixture = Join-Path $testRoot "fixture.jsonl"
 $database = Join-Path $testRoot "novelist.db"
 $metrics = Join-Path $testRoot "metrics.json"
 $progress = Join-Path $testRoot "progress.json"
 $stdout = Join-Path $testRoot "stdout.json"
 $finalized = Join-Path $testRoot "finalized.json"
 $invalid = Join-Path $testRoot "incomplete.stdout.json"
 $invalidOutput = Join-Path $testRoot "invalid-metrics.json"
 $fullDatabase = Join-Path $testRoot "full-pipeline.db"
 $fullMetrics = Join-Path $testRoot "full-pipeline-metrics.json"
 $fullProgress = Join-Path $testRoot "full-pipeline-progress.json"

 [IO.File]::WriteAllLines($fixture, @(
 '{"source_id":"source-1","library_id":"library-1","chapter_index":1,"sequence_index":1,"text":"alpha","license_state":"allowed"}',
 '{"source_id":"source-2","library_id":"library-2","chapter_index":2,"sequence_index":2,"text":"beta","license_state":"allowed"}'
 ), [Text.UTF8Encoding]::new($false))

 & dotnet build $hostProject -c $Configuration -v minimal --no-restore
 if ($LASTEXITCODE -ne 0) { throw "Harness host build failed." }
 & dotnet $hostDll scale --database $database --fixture $fixture --minimum-characters 9 --job-size 1 `
 --minimum-throughput 0 --maximum-claim-p95-ms 10000 --maximum-list-p95-ms 10000 `
 --metrics-output $metrics --progress-output $progress | Set-Content -LiteralPath $stdout -Encoding utf8
 if ($LASTEXITCODE -ne 0) { throw "Scale host smoke run failed." }

 $metricsJson = Get-Content -LiteralPath $metrics -Raw | ConvertFrom-Json
 $progressJson = Get-Content -LiteralPath $progress -Raw | ConvertFrom-Json
 Assert-True ($metricsJson.schema_version -eq "corpus-m2-scale-metrics-v1") "Metrics schema mismatch."
Assert-True ($metricsJson.result.passed -eq $true) "Metrics did not pass."
 Assert-True ($metricsJson.result.post_finalize_job_reads -eq 0) "Scale loop performed post-finalize job reads."
 Assert-True ($progressJson.schema_version -eq "corpus-m2-scale-progress-v1") "Progress schema mismatch."
 Assert-True ($progressJson.status -eq "completed") "Progress did not reach completed."
Assert-True ($progressJson.processed_work_items -eq 2) "Progress work-item count mismatch."
 Assert-True (@(Get-ChildItem -LiteralPath $testRoot -Filter '*.tmp' -Force).Count -eq 0) "Atomic host writes left temporary files."

 & dotnet $hostDll scale-full --database $fullDatabase --fixture $fixture --minimum-characters 9 `
 --minimum-throughput 0 --maximum-claim-p95-ms 10000 --maximum-list-p95-ms 10000 `
 --maximum-progress-p95-ms 10000 `
 --minimum-latency-samples 1 `
 --metrics-output $fullMetrics --progress-output $fullProgress | Out-Null
 if ($LASTEXITCODE -ne 0) { throw "Full-pipeline scale host smoke run failed." }
 $fullMetricsJson = Get-Content -LiteralPath $fullMetrics -Raw | ConvertFrom-Json
 $fullProgressJson = Get-Content -LiteralPath $fullProgress -Raw | ConvertFrom-Json
 Assert-True ($fullMetricsJson.schema_version -eq "corpus-m2-full-scale-metrics-v1") "Full-pipeline metrics schema mismatch."
 Assert-True ($fullMetricsJson.result.passed -eq $true) "Full-pipeline metrics did not pass."
 Assert-True ($fullMetricsJson.result.pipeline -eq "scheduler_snapshot_builder_worker_fake_analyzer") "Full-pipeline host skipped the worker path."
 Assert-True ($fullMetricsJson.result.anchors -ge 2) "Full-pipeline fixture did not create two anchors."
 Assert-True ($fullMetricsJson.result.libraries -ge 2) "Full-pipeline fixture did not create two libraries."
 Assert-True ($fullMetricsJson.result.session_library_bindings -ge 2) "Full-pipeline fixture did not bind libraries to a session."
 Assert-True ($fullMetricsJson.result.completion_rows -eq $fullMetricsJson.result.work_items) "Full-pipeline host lost work-item outputs."
 Assert-True ($fullMetricsJson.result.duplicate_outputs -eq 0) "Full-pipeline host produced duplicate outputs."
 Assert-True ($fullMetricsJson.result.fake_analyzer_calls -eq $fullMetricsJson.result.work_items) "Fake analyzer did not receive every frozen work item."
 Assert-True ($fullMetricsJson.result.tokens.persisted_budget_penetration -eq 0) "Full-pipeline host exceeded persisted job budget."
 Assert-True ($fullMetricsJson.result.claim_ms.count -ge 1) "Full-pipeline host did not collect claim latency samples."
 Assert-True ($fullMetricsJson.result.task_list_ms.count -ge 1) "Full-pipeline host did not collect list latency samples."
 Assert-True ($fullMetricsJson.result.job_progress_ms.count -ge 1) "Full-pipeline host did not collect job progress latency samples."
 Assert-True ($fullMetricsJson.result.storage.active_leases -eq 0) "Full-pipeline host left an active lease."
 Assert-True ($fullMetricsJson.result.storage.database_bytes_after -gt 0) "Full-pipeline host did not record database size."
 Assert-True ($fullProgressJson.schema_version -eq "corpus-m2-full-scale-progress-v1") "Full-pipeline progress schema mismatch."
 Assert-True ($fullProgressJson.status -eq "completed") "Full-pipeline progress did not reach completed."

 & $PSScriptRoot/finalize-existing-scale.ps1 -Stdout $stdout -Output $finalized | Out-Null
 $finalizedJson = Get-Content -LiteralPath $finalized -Raw | ConvertFrom-Json
 Assert-True ($finalizedJson.schema_version -eq "corpus-m2-scale-metrics-v1") "Finalized schema mismatch."
Assert-True ($finalizedJson.result.passed -eq $true) "Finalized result did not pass."

 & $PSScriptRoot/finalize-existing-scale.ps1 -Stdout $metrics -Output $finalized | Out-Null
 $envelopeFinalizedJson = Get-Content -LiteralPath $finalized -Raw | ConvertFrom-Json
 Assert-True ($envelopeFinalizedJson.result.passed -eq $true) "Metrics envelope finalization failed."
 Assert-True (@(Get-ChildItem -LiteralPath $testRoot -Filter '*.tmp' -Force).Count -eq 0) "Finalizer left temporary files."

 [IO.File]::WriteAllText($invalid, '{"passed":true', [Text.UTF8Encoding]::new($false))
 $rejected = $false
 try { & $PSScriptRoot/finalize-existing-scale.ps1 -Stdout $invalid -Output $invalidOutput | Out-Null }
 catch { $rejected = $true }
 Assert-True $rejected "Incomplete stdout was not rejected."
Assert-True (-not (Test-Path -LiteralPath $invalidOutput)) "Incomplete stdout created metrics."

 $benchmarkFixture = Join-Path $testRoot "benchmark.jsonl"
 $benchmarkDatabase = Join-Path $testRoot "benchmark.db"
 $benchmarkMetrics = Join-Path $testRoot "benchmark-metrics.json"
 $benchmarkProgress = Join-Path $testRoot "benchmark-progress.json"
 $writer = [IO.StreamWriter]::new($benchmarkFixture, $false, [Text.UTF8Encoding]::new($false))
 try {
 for ($index = 0; $index -lt $BenchmarkWorkItems; $index++) {
 $writer.WriteLine((@{
 source_id = "benchmark-source-$index"
 library_id = "benchmark-library-$($index % 2)"
 chapter_index = 1 + [int][Math]::Floor($index / 100)
 sequence_index = $index
 text = "benchmark item $index"
 license_state = "allowed"
 } | ConvertTo-Json -Compress))
 }
 }
 finally { $writer.Dispose() }
 & dotnet $hostDll scale --database $benchmarkDatabase --fixture $benchmarkFixture `
 --minimum-characters $BenchmarkWorkItems --job-size 100 --minimum-throughput 20 `
 --maximum-claim-p95-ms 100 --maximum-list-p95-ms 200 `
 --metrics-output $benchmarkMetrics --progress-output $benchmarkProgress | Out-Null
 if ($LASTEXITCODE -ne 0) { throw "Medium scale benchmark failed." }
 $benchmark = Get-Content -LiteralPath $benchmarkMetrics -Raw | ConvertFrom-Json
 Assert-True ($benchmark.result.passed -eq $true) "Medium scale benchmark did not meet fixed thresholds."
 Assert-True ($benchmark.result.work_items -eq $BenchmarkWorkItems) "Medium scale benchmark work-item count mismatch."
 Assert-True ($benchmark.result.output_rows -eq $BenchmarkWorkItems) "Medium scale benchmark lost outputs."
 Assert-True ($benchmark.result.duplicate_outputs -eq 0) "Medium scale benchmark produced duplicate outputs."
 Assert-True ($benchmark.result.post_finalize_job_reads -eq 0) "Medium scale benchmark regressed to per-item job reads."

 Write-Output "scale harness tests passed"
}
finally {
 if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
