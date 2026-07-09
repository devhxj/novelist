using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record StartReferenceCorpusFeatureAnalysisPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("token_budget")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TokenBudget = null,
    [property: JsonPropertyName("resume")] bool Resume = false,
    [property: JsonPropertyName("run_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RunId = null);

public sealed record GetReferenceCorpusFeatureAnalysisRunPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("run_id")] string RunId);

public sealed record ReferenceCorpusFeatureAnalysisRunPayload(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("families")] IReadOnlyList<string> Families,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("token_budget")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? TokenBudget,
    [property: JsonPropertyName("tokens_spent")] int TokensSpent,
    [property: JsonPropertyName("resume_cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResumeCursor,
    [property: JsonPropertyName("observation_count")] int ObservationCount,
    [property: JsonPropertyName("processed_work_items")] int ProcessedWorkItems,
    [property: JsonPropertyName("analyzer_version")] string AnalyzerVersion,
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("model_provider")] string ModelProvider,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);
