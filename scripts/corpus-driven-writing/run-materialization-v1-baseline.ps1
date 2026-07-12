param(
    [string]$Calibration = "tests/Novelist.IntegrationTests/Fixtures/corpus-driven-writing/materialization-quality-calibration-v1.json",
    [string]$Holdout = "tests/Novelist.IntegrationTests/Fixtures/corpus-driven-writing/materialization-quality-holdout-v1.json",
    [string]$Output = "build/tmp/corpus-driven-writing/materialization-v1-baseline",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$calibrationPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Calibration))
$holdoutPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Holdout))
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Output))
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "build/tmp/corpus-driven-writing"))
if (-not $outputPath.StartsWith($allowedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Baseline output must stay under build/tmp/corpus-driven-writing."
}

Push-Location $repoRoot
try {
    dotnet run --project scripts/corpus-driven-writing/CorpusEvaluationHarness/CorpusEvaluationHarness.csproj -c $Configuration -- `
        --materialization-v1-baseline `
        --calibration $calibrationPath `
        --holdout $holdoutPath `
        --output $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Materialization v1 baseline failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
