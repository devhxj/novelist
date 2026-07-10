using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceCorpusNodeTypes
{
    public const string Chapter = "chapter";
    public const string Scene = "scene";
    public const string Passage = "passage";
    public const string Sentence = "sentence";
    public const string Clause = "clause";

    public static IReadOnlyList<string> All { get; } =
    [
        Chapter,
        Scene,
        Passage,
        Sentence,
        Clause
    ];
}

public static class ReferenceCorpusLicenseStates
{
    public const string Unknown = "unknown";
    public const string PublicDomain = "public_domain";
    public const string CreativeCommons = "cc";
    public const string Authorized = "authorized";
    public const string Restricted = "restricted";
    public const string Forbidden = "forbidden";

    public static IReadOnlyList<string> All { get; } =
    [
        Unknown,
        PublicDomain,
        CreativeCommons,
        Authorized,
        Restricted,
        Forbidden
    ];
}

public static class ReferenceCorpusReusePolicies
{
    public const string VerbatimOk = "verbatim_ok";
    public const string AdaptedOnly = "adapted_only";
    public const string ReferenceOnly = "reference_only";
    public const string Forbidden = "forbidden";

    public static IReadOnlyList<string> All { get; } =
    [
        VerbatimOk,
        AdaptedOnly,
        ReferenceOnly,
        Forbidden
    ];
}

public sealed record CharacterStateSnapshotPayload(
    [property: JsonPropertyName("character")] string Character,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("allowed_knowledge")] IReadOnlyList<string> AllowedKnowledge,
    [property: JsonPropertyName("forbidden_knowledge")] IReadOnlyList<string> ForbiddenKnowledge);

public sealed record CurrentChapterContextPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("current_draft_text")] string? CurrentDraftText,
    [property: JsonPropertyName("insertion_offset")] int InsertionOffset,
    [property: JsonPropertyName("previous_chapter_summary")] string? PreviousChapterSummary,
    [property: JsonPropertyName("character_snapshots")] IReadOnlyList<CharacterStateSnapshotPayload> CharacterSnapshots);

public sealed record ReferenceCorpusScopePayload(
    [property: JsonPropertyName("library_ids")] IReadOnlyList<string> LibraryIds,
    [property: JsonPropertyName("reuse_policies")] IReadOnlyList<string> ReusePolicies,
    [property: JsonPropertyName("include_anchor_ids")] IReadOnlyList<long> IncludeAnchorIds,
    [property: JsonPropertyName("exclude_anchor_ids")] IReadOnlyList<long> ExcludeAnchorIds,
    [property: JsonPropertyName("session_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionId = null);

public sealed record ReferenceCorpusQueryContextPayload(
    [property: JsonPropertyName("scene_type")] string SceneType,
    [property: JsonPropertyName("emotion_target")] string EmotionTarget,
    [property: JsonPropertyName("pacing_target")] string PacingTarget,
    [property: JsonPropertyName("narrative_position")] string NarrativePosition,
    [property: JsonPropertyName("commercial_mechanic")] string CommercialMechanic,
    [property: JsonPropertyName("character_states")] IReadOnlyList<string> CharacterStates,
    [property: JsonPropertyName("required_narrative_functions")] IReadOnlyList<string> RequiredNarrativeFunctions,
    [property: JsonPropertyName("chapter_context")] CurrentChapterContextPayload ChapterContext,
    [property: JsonPropertyName("scope")] ReferenceCorpusScopePayload Scope);

public sealed record SearchReferenceCorpusCandidatesPayload(
    [property: JsonPropertyName("query_context")] ReferenceCorpusQueryContextPayload QueryContext,
    [property: JsonPropertyName("page_request")] PageRequestPayload PageRequest);

public static class ReferenceCorpusTechniqueVectorIndexBackfillStatuses
{
    public const string Ready = "ready";
    public const string Empty = "empty";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public sealed record BackfillReferenceCorpusTechniqueVectorIndexPayload(
    [property: JsonPropertyName("query_context")] ReferenceCorpusQueryContextPayload QueryContext,
    [property: JsonPropertyName("node_type")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NodeType = null);

public sealed record ReferenceCorpusTechniqueVectorIndexBackfillPayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("index_scope_key")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IndexScopeKey,
    [property: JsonPropertyName("table_name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TableName,
    [property: JsonPropertyName("provider_key")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderKey,
    [property: JsonPropertyName("model_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModelId,
    [property: JsonPropertyName("dimensions")] int Dimensions,
    [property: JsonPropertyName("source_count")] int SourceCount,
    [property: JsonPropertyName("vector_count")] int VectorCount,
    [property: JsonPropertyName("skipped_vector_count")] int SkippedVectorCount,
    [property: JsonPropertyName("rebuilt")] bool Rebuilt,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);

public sealed record ReferenceCorpusCandidateEvidencePayload(
    [property: JsonPropertyName("observation_id")] string ObservationId,
    [property: JsonPropertyName("feature_family")] string FeatureFamily,
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("confidence")] double Confidence);

public sealed record ReferenceCorpusCandidatePayload(
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("library_id")] string LibraryId,
    [property: JsonPropertyName("node_type")] string NodeType,
    [property: JsonPropertyName("text_preview")] string TextPreview,
    [property: JsonPropertyName("text_hash")] string TextHash,
    [property: JsonPropertyName("license_state")] string LicenseState,
    [property: JsonPropertyName("reuse_policy")] string ReusePolicy,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("score_components")] IReadOnlyDictionary<string, double> ScoreComponents,
    [property: JsonPropertyName("fit_explanation")] string FitExplanation,
    [property: JsonPropertyName("evidence")] IReadOnlyList<ReferenceCorpusCandidateEvidencePayload> Evidence);
