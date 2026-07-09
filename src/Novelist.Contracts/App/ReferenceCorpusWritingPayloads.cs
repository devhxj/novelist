using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record GenerateReferenceCorpusInsertionDraftPayload(
    [property: JsonPropertyName("natural_language_goal")] string NaturalLanguageGoal,
    [property: JsonPropertyName("chapter_context")] CurrentChapterContextPayload ChapterContext,
    [property: JsonPropertyName("scope")] ReferenceCorpusScopePayload Scope,
    [property: JsonPropertyName("slot_values")] IReadOnlyDictionary<string, string> SlotValues);

public sealed record ReferenceCorpusInsertionBlueprintPayload(
    [property: JsonPropertyName("blueprint_id")] string BlueprintId,
    [property: JsonPropertyName("query_context_hash")] string QueryContextHash,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("beats")] IReadOnlyList<ReferenceCorpusInsertionBlueprintBeatPayload> Beats);

public sealed record ReferenceCorpusInsertionBlueprintBeatPayload(
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("beat_index")] int BeatIndex,
    [property: JsonPropertyName("role_in_beat")] string RoleInBeat,
    [property: JsonPropertyName("narrative_function")] string NarrativeFunction,
    [property: JsonPropertyName("node_ids")] IReadOnlyList<string> NodeIds);

public sealed record ReferenceCorpusSlotReplacementPayload(
    [property: JsonPropertyName("slot_name")] string SlotName,
    [property: JsonPropertyName("source_value")] string SourceValue,
    [property: JsonPropertyName("replacement_value")] string ReplacementValue,
    [property: JsonPropertyName("source_start")] int SourceStart,
    [property: JsonPropertyName("source_end")] int SourceEnd,
    [property: JsonPropertyName("output_start")] int OutputStart,
    [property: JsonPropertyName("output_end")] int OutputEnd);

public sealed record ReferenceCorpusInsertionPiecePayload(
    [property: JsonPropertyName("piece_id")] string PieceId,
    [property: JsonPropertyName("beat_id")] string BeatId,
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("library_id")] string LibraryId,
    [property: JsonPropertyName("text_hash")] string SourceTextHash,
    [property: JsonPropertyName("reuse_policy")] string ReusePolicy,
    [property: JsonPropertyName("license_state")] string LicenseState,
    [property: JsonPropertyName("output_text")] string OutputText,
    [property: JsonPropertyName("preserved_text_hash")] string PreservedTextHash,
    [property: JsonPropertyName("preserved_hash_matches")] bool PreservedHashMatches,
    [property: JsonPropertyName("slot_replacements")] IReadOnlyList<ReferenceCorpusSlotReplacementPayload> SlotReplacements);

public sealed record ReferenceCorpusInsertionGateViolationPayload(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("actual")] double Actual,
    [property: JsonPropertyName("threshold")] double Threshold);

public sealed record ReferenceCorpusInsertionGatePiecePayload(
    [property: JsonPropertyName("piece_id")] string PieceId,
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("should_block")] bool ShouldBlock,
    [property: JsonPropertyName("four_gram_containment_ratio")] double FourGramContainmentRatio,
    [property: JsonPropertyName("longest_common_substring_ratio")] double LongestCommonSubstringRatio,
    [property: JsonPropertyName("violations")] IReadOnlyList<ReferenceCorpusInsertionGateViolationPayload> Violations);

public sealed record ReferenceCorpusInsertionGatePayload(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("pieces")] IReadOnlyList<ReferenceCorpusInsertionGatePiecePayload> Pieces);

public sealed record ReferenceCorpusInsertionDraftPayload(
    [property: JsonPropertyName("query_context")] ReferenceCorpusQueryContextPayload QueryContext,
    [property: JsonPropertyName("blueprint")] ReferenceCorpusInsertionBlueprintPayload Blueprint,
    [property: JsonPropertyName("pieces")] IReadOnlyList<ReferenceCorpusInsertionPiecePayload> Pieces,
    [property: JsonPropertyName("slot_replacements")] IReadOnlyList<ReferenceCorpusSlotReplacementPayload> SlotReplacements,
    [property: JsonPropertyName("assembled_text")] string AssembledText,
    [property: JsonPropertyName("chapter_text_after_insertion")] string ChapterTextAfterInsertion,
    [property: JsonPropertyName("ready_for_insertion")] bool ReadyForInsertion,
    [property: JsonPropertyName("gate")] ReferenceCorpusInsertionGatePayload Gate);
