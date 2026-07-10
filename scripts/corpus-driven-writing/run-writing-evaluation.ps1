param(
    [Parameter(Mandatory = $true)]
    [string]$Fixture,
    [string]$Output = "build/tmp/corpus-driven-writing/evaluation",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$fixturePath = if ([System.IO.Path]::IsPathRooted($Fixture)) {
    [System.IO.Path]::GetFullPath($Fixture)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Fixture))
}
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Output))
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "build/tmp/corpus-driven-writing")).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$allowedRootPrefix = $allowedRoot + [System.IO.Path]::DirectorySeparatorChar
$project = Join-Path $PSScriptRoot "CorpusEvaluationHarness/CorpusEvaluationHarness.csproj"

if (-not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) {
    throw "Evaluation fixture does not exist."
}

if ($outputPath -ne $allowedRoot -and -not $outputPath.StartsWith($allowedRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Evaluation output must stay under build/tmp/corpus-driven-writing."
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
Push-Location $repoRoot
try {
    & dotnet run --project $project -c $Configuration -- --fixture $fixturePath --output $outputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Evaluation harness failed."
    }
}
finally {
    Pop-Location
}
