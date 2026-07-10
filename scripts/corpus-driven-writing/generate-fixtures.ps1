param(
 [int]$GoldenSentenceCount = 500,
 [int]$ScaleCharacterCount = 2000000,
 [string]$ScaleOutput = "build/tmp/corpus-driven-writing/scale-2m.jsonl"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$fixtureDir = Join-Path $repoRoot "tests/Novelist.IntegrationTests/Fixtures/corpus-driven-writing"
$goldenPath = Join-Path $fixtureDir "m0-500-sentence-golden.json"
$scalePath = Join-Path $repoRoot $ScaleOutput
New-Item -ItemType Directory -Force -Path $fixtureDir, (Split-Path $scalePath) | Out-Null

$families = @("syntactic", "rhythm", "sensory", "emotion", "rhetoric", "narrative_function", "perspective", "character_dynamics", "action_chain", "commercial_mechanics")
$templates = @(
 "雨声压在窗沿，{0}没有回头，只把指节慢慢收紧。",
 "门外脚步停了半拍，{0}抬眼时，屋里的人同时安静下来。",
 "灯火晃过杯沿，{0}把那句解释咽了回去。",
 "风从长街尽头卷来，{0}侧身让开一步，却没有让路。",
 "纸页翻到最后，{0}才发现被遮住的名字一直在那里。"
)

$sentences = for ($i = 0; $i -lt $GoldenSentenceCount; $i++) {
 $sourceIndex = $i % 5
 $chapter = [math]::Floor($i / 25) + 1
 $text = $templates[$i % $templates.Count] -f ("角色{0:D2}" -f ($i % 17))
 $bytes = [Text.Encoding]::UTF8.GetBytes($text)
 $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
 [ordered]@{
 node_id = "golden-source-$sourceIndex-sentence-$i"
 source_id = "golden-source-$sourceIndex"
 library_id = "golden-library-$($sourceIndex % 2)"
 chapter_index = $chapter
 sequence_index = $i
 node_type = "sentence"
 text = $text
 text_hash = $hash
 license_state = "authorized"
 reuse_policy = "adapted_only"
 feature_family = $families[$i % $families.Count]
 review_state = if ($i % 11 -eq 0) { "confirmed" } else { "unverified" }
 expected_evidence = [ordered]@{ start = 0; end = $text.Length }
 }
}

$golden = [ordered]@{
 schema_version = "corpus-driven-writing-500-sentence-golden-v1"
 generated_by = "scripts/corpus-driven-writing/generate-fixtures.ps1"
 sentence_count = $GoldenSentenceCount
 libraries = @(
 [ordered]@{ library_id = "golden-library-0"; enabled = $true },
 [ordered]@{ library_id = "golden-library-1"; enabled = $true }
 )
 sentences = $sentences
}
$golden | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $goldenPath -Encoding utf8

$writer = [IO.StreamWriter]::new($scalePath, $false, [Text.UTF8Encoding]::new($false))
try {
 $characters = 0
 $ordinal = 0
 while ($characters -lt $ScaleCharacterCount) {
 $sourceIndex = $ordinal % 12
 $text = $templates[$ordinal % $templates.Count] -f ("人物{0:D3}" -f ($ordinal % 113))
 $record = [ordered]@{
 source_id = "scale-source-$sourceIndex"
 library_id = "scale-library-$($sourceIndex % 4)"
 chapter_index = [int]([math]::Floor($ordinal / 40) + 1)
 sequence_index = $ordinal
 text = $text
 license_state = "authorized"
 }
 $writer.WriteLine(($record | ConvertTo-Json -Compress))
 $characters += $text.Length
 $ordinal++
 }
}
finally {
 $writer.Dispose()
}

Write-Output "golden=$goldenPath sentences=$GoldenSentenceCount"
Write-Output "scale=$scalePath characters>=$ScaleCharacterCount"
