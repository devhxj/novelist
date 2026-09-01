using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record GetChapterCorpusCoveragePayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ChapterNumber = null);

public sealed record ChapterCorpusBeatCoveragePayload(
    [property: JsonPropertyName("beat")] string Beat,
    [property: JsonPropertyName("covered")] bool Covered,
    [property: JsonPropertyName("anchor_title")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AnchorTitle = null,
    [property: JsonPropertyName("text_preview")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TextPreview = null);

public sealed record ChapterCorpusCoveragePayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ChapterNumber,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("beats")] IReadOnlyList<ChapterCorpusBeatCoveragePayload> Beats,
    [property: JsonPropertyName("covered_count")] int CoveredCount,
    [property: JsonPropertyName("total_count")] int TotalCount,
    [property: JsonPropertyName("coverage_ratio")] double CoverageRatio,
    [property: JsonPropertyName("sufficient")] bool Sufficient);
