param(
 [int]$Rounds = 2,
 [string]$Configuration = "Debug",
 [switch]$SkipScale
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$summaryPath = Join-Path $repoRoot "build/tmp/corpus-driven-writing/m2-quantitative-summary.json"

& $PSScriptRoot/run-recovery-harness.ps1 -Rounds $Rounds -Configuration $Configuration
if (-not $SkipScale) { & $PSScriptRoot/run-scale-harness.ps1 -Configuration $Configuration }

$recovery = Get-Content (Join-Path $repoRoot "build/tmp/corpus-driven-writing/recovery-metrics.json") -Raw | ConvertFrom-Json
$scale = if ($SkipScale) { $null } else { Get-Content (Join-Path $repoRoot "build/tmp/corpus-driven-writing/scale-metrics.json") -Raw | ConvertFrom-Json }
$summary = [ordered]@{
 schema_version = "corpus-m2-quantitative-summary-v1"
 generated_at = [DateTimeOffset]::UtcNow.ToString("O")
 recovery = $recovery
 scale = $scale
 passed = $recovery.passed -and ($SkipScale -or $scale.result.passed)
}
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $summaryPath -Encoding utf8
if (-not $summary.passed) { throw "M2 quantitative acceptance failed. See $summaryPath" }
Write-Output "summary=$summaryPath"
