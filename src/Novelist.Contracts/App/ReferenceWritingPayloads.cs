using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record GenerateReferenceBlueprintsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("requested_count")] int RequestedCount = 3);

public sealed record GetReferenceWritingSessionPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("session_id")] string SessionId);

public sealed record SelectReferenceBlueprintPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("blueprint_id")] string BlueprintId);

public sealed record GenerateReferenceDraftCandidatesPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("blueprint_id")] string BlueprintId,
    [property: JsonPropertyName("current_draft_text")] string CurrentDraftText,
    [property: JsonPropertyName("insertion_offset")] int InsertionOffset,
    [property: JsonPropertyName("slot_values")] IReadOnlyDictionary<string, string> SlotValues,
    [property: JsonPropertyName("requested_count")] int RequestedCount = 3);

public sealed record ReferenceMaterialIdentityPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("generation_id")] string GenerationId);

public sealed record ReferenceWritingBlueprintBeatPayload(
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("beat_index")] int BeatIndex,
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("narrative_function")] string NarrativeFunction,
    [property: JsonPropertyName("materials")] IReadOnlyList<ReferenceMaterialIdentityPayload> Materials);

public sealed record ReferenceWritingBlueprintPayload(
    [property: JsonPropertyName("blueprint_id")] string BlueprintId,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("beats")] IReadOnlyList<ReferenceWritingBlueprintBeatPayload> Beats);

public sealed record ReferenceWritingSessionPayload(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("chapter_number")] int ChapterNumber,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("blueprints")] IReadOnlyList<ReferenceWritingBlueprintPayload> Blueprints,
    [property: JsonPropertyName("selected_blueprint_id")] string SelectedBlueprintId,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record ReferenceWritingDraftSourcePayload(
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("generation_id")] string GenerationId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("chapter_index")] int ChapterIndex,
    [property: JsonPropertyName("text_hash")] string TextHash,
    [property: JsonPropertyName("license_state")] string LicenseState,
    [property: JsonPropertyName("reuse_policy")] string ReusePolicy);

public sealed record ReferenceWritingDraftAuditPayload(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors);

public sealed record ReferenceWritingDraftCandidatePayload(
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("blueprint_id")] string BlueprintId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("chapter_text_after_insertion")] string ChapterTextAfterInsertion,
    [property: JsonPropertyName("sources")] IReadOnlyList<ReferenceWritingDraftSourcePayload> Sources,
    [property: JsonPropertyName("audit")] ReferenceWritingDraftAuditPayload Audit);

public sealed record ReferenceWritingDraftCandidatesPayload(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("blueprint_id")] string BlueprintId,
    [property: JsonPropertyName("candidates")] IReadOnlyList<ReferenceWritingDraftCandidatePayload> Candidates);

public static class ReferenceWritingErrorCodes
{
    public const string SessionNotFound = "reference_writing_session_not_found";
    public const string BlueprintNotSelected = "reference_writing_blueprint_not_selected";
    public const string BlueprintStale = "reference_writing_blueprint_stale";
    public const string MaterialMissing = "reference_writing_material_missing";
    public const string MaterialTextMismatch = "reference_writing_material_text_mismatch";
    public const string MaterialNotInsertable = "reference_writing_material_not_insertable";
}
