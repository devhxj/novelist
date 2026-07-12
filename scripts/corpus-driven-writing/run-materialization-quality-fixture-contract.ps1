param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

Push-Location $repoRoot
try {
    dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj `
        --no-restore `
        --configuration $Configuration `
        --filter "FullyQualifiedName~ReferenceMaterializationQualityFixtureTests" `
        -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Materialization quality fixture contract failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
