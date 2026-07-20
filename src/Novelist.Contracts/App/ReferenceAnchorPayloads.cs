using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public static class ReferenceAnchorBuildStates
{
    public const string PendingSplit = "pending_split";
    public const string PendingMaterialization = "pending_materialization";
    public const string Ready = "ready";

    public static IReadOnlyList<string> All { get; } =
        [PendingSplit, PendingMaterialization, Ready];
}

public static class ReferenceCorpusVisibilities
{
    public const string Private = "private";
    public const string Workspace = "workspace";
    public const string Restricted = "restricted";

    public static IReadOnlyList<string> All { get; } = [Private, Workspace, Restricted];
}

public static class ReferenceSourceTrustLevels
{
    public const string UserVerified = "user_verified";
    public const string Imported = "imported";
    public const string Unverified = "unverified";

    public static IReadOnlyList<string> All { get; } = [UserVerified, Imported, Unverified];
}

public static class ReferenceAnchorOwnerScopes
{
    public const string Novel = "novel";
    public const string WorkspaceCorpus = "workspace_corpus";
}

public sealed record CreateReferenceAnchorPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("license_status")] string LicenseStatus,
    [property: JsonPropertyName("visibility")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Visibility = null,
    [property: JsonPropertyName("source_trust")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceTrust = null,
    [property: JsonPropertyName("user_tags")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? UserTags = null);

public sealed record DeleteReferenceAnchorsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_ids")] IReadOnlyList<long> AnchorIds);

public sealed record UpdateReferenceAnchorMetadataPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("license_status")] string LicenseStatus,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("source_trust")] string SourceTrust,
    [property: JsonPropertyName("user_tags")] IReadOnlyList<string> UserTags);

public sealed record ReferenceAnchorPayload(
    [property: JsonPropertyName("anchor_id")] long AnchorId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("source_kind")] string SourceKind,
    [property: JsonPropertyName("license_status")] string LicenseStatus,
    [property: JsonPropertyName("source_file_hash")] string SourceFileHash,
    [property: JsonPropertyName("build_version")] string BuildVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("source_trust")] string SourceTrust,
    [property: JsonPropertyName("user_tags")] IReadOnlyList<string> UserTags)
{
    [JsonPropertyName("owner_scope")]
    public string OwnerScope { get; init; } = NovelId == 0
        ? ReferenceAnchorOwnerScopes.WorkspaceCorpus
        : ReferenceAnchorOwnerScopes.Novel;

    [JsonPropertyName("owner_novel_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OwnerNovelId { get; init; } = NovelId == 0 ? null : NovelId;

    public ReferenceAnchorPayload(
        long anchorId,
        long novelId,
        string title,
        string author,
        string sourcePath,
        string sourceKind,
        string licenseStatus,
        string sourceFileHash,
        string buildVersion,
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
        : this(
            anchorId,
            novelId,
            title,
            author,
            sourcePath,
            sourceKind,
            licenseStatus,
            sourceFileHash,
            buildVersion,
            status,
            createdAt,
            updatedAt,
            ReferenceCorpusVisibilities.Private,
            ReferenceSourceTrustLevels.UserVerified,
            [])
    {
    }
}
