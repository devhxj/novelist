using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

// 高级写作素材（L2）：物理落点为统一外壳表 reference_advanced_materials。
// 三层 × 五类，每书一套；evidence_refs 强制非空，机理层 boundary 必填。
// 词表冻结（设计文档 docs/reference-anchor-implementation/advanced-materials-plan.md §2.2）。

public static class ReferenceAdvancedMaterialLayers
{
    public const string Observation = "observation";
    public const string Specimen = "specimen";
    public const string Strategy = "strategy";

    public static IReadOnlyList<string> All { get; } = [Observation, Specimen, Strategy];

    public static bool IsSupported(string? layer) => layer is not null && All.Contains(layer, StringComparer.Ordinal);
}

public static class ReferenceAdvancedMaterialFamilies
{
    public const string World = "world";
    public const string Style = "style";
    public const string Craft = "craft";
    public const string Technique = "technique";
    public const string Structure = "structure";

    public static IReadOnlyList<string> All { get; } = [World, Style, Craft, Technique, Structure];

    public static bool IsSupported(string? family) => family is not null && All.Contains(family, StringComparer.Ordinal);
}

public static class ReferenceAdvancedMaterialReviewStates
{
    public const string Unverified = "unverified";
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";

    public static IReadOnlyList<string> All { get; } = [Unverified, Confirmed, Rejected];
}

public static class ReferenceAdvancedMaterialValidityStates
{
    public const string Active = "active";
    public const string Superseded = "superseded";

    public static IReadOnlyList<string> All { get; } = [Active, Superseded];
}

public static class ReferenceAdvancedMaterialReviewDecisions
{
    public const string Confirm = "confirm";
    public const string Reject = "reject";

    public static IReadOnlyList<string> All { get; } = [Confirm, Reject];
}

public sealed record ListReferenceAdvancedMaterialsPayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("family")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Family = null,
    [property: JsonPropertyName("layer")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Layer = null,
    [property: JsonPropertyName("review_state")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewState = null,
    [property: JsonPropertyName("include_superseded")] bool IncludeSuperseded = false,
    [property: JsonPropertyName("page_request")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PageRequestPayload? PageRequest = null);

public sealed record ReferenceAdvancedMaterialSummaryPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("layer")] string Layer,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("value_text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueText,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("review_state")] string ReviewState,
    [property: JsonPropertyName("validity_state")] string ValidityState,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record GetReferenceAdvancedMaterialDetailPayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("material_id")] string MaterialId);

public sealed record ReferenceAdvancedMaterialEvidencePayload(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("material_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MaterialId,
    [property: JsonPropertyName("start_offset")] int StartOffset,
    [property: JsonPropertyName("end_offset")] int EndOffset,
    [property: JsonPropertyName("text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text);

public sealed record ReferenceAdvancedMaterialDetailPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("layer")] string Layer,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("source_ref")] string SourceRef,
    [property: JsonPropertyName("value_text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueText,
    [property: JsonPropertyName("value_json")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueJson,
    [property: JsonPropertyName("rationale_json")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RationaleJson,
    [property: JsonPropertyName("boundary_json")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BoundaryJson,
    [property: JsonPropertyName("transfer_template")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TransferTemplate,
    [property: JsonPropertyName("transfer_slots_json")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TransferSlotsJson,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("review_state")] string ReviewState,
    [property: JsonPropertyName("validity_state")] string ValidityState,
    [property: JsonPropertyName("extractor_version")] string ExtractorVersion,
    [property: JsonPropertyName("evidence")] IReadOnlyList<ReferenceAdvancedMaterialEvidencePayload> Evidence,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record ReviewReferenceAdvancedMaterialPayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("note")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null);

public sealed record ReferenceAdvancedMaterialReviewResultPayload(
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("review_state")] string ReviewState,
    [property: JsonPropertyName("reviewed_at")] DateTimeOffset ReviewedAt);
