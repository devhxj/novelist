using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceChapterSplitModes
{
    public const string Auto = "auto";
    public const string Manual = "manual";

    public static IReadOnlyList<string> All { get; } = [Auto, Manual];
}

public static class ReferenceChapterSplitProfileStates
{
    public const string Draft = "draft";
    public const string Validated = "validated";
    public const string Confirmed = "confirmed";
    public const string Stale = "stale";

    public static IReadOnlyList<string> All { get; } = [Draft, Validated, Confirmed, Stale];
}

public sealed record AnalyzeReferenceChapterSplitPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId);

public sealed record PreviewReferenceChapterSplitPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("delimiter_template")] string DelimiterTemplate);

public sealed record ConfirmReferenceChapterSplitPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("split_profile_id")] string SplitProfileId);

public sealed record ReferenceChapterSplitBoundaryPayload(
    [property: JsonPropertyName("chapter_index")] int ChapterIndex,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("heading_start")] int HeadingStart,
    [property: JsonPropertyName("content_start")] int ContentStart,
    [property: JsonPropertyName("content_end")] int ContentEnd,
    [property: JsonPropertyName("text_hash")] string TextHash);

public sealed record ReferenceChapterSplitProfilePayload(
    [property: JsonPropertyName("split_profile_id")] string SplitProfileId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("source_hash")] string SourceHash,
    [property: JsonPropertyName("split_mode")] string SplitMode,
    [property: JsonPropertyName("pattern_kind")] string PatternKind,
    [property: JsonPropertyName("delimiter_template")] string DelimiterTemplate,
    [property: JsonPropertyName("sample_char_count")] int SampleCharCount,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("chapter_count")] int ChapterCount,
    [property: JsonPropertyName("boundaries")] IReadOnlyList<ReferenceChapterSplitBoundaryPayload> Boundaries,
    [property: JsonPropertyName("model_provider")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ModelProvider = null,
    [property: JsonPropertyName("model_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ModelId = null,
    [property: JsonPropertyName("confidence")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Confidence = null);
