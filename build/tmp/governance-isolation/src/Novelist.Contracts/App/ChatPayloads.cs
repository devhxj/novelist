using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record ChatInputPayload(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("reasoning_effort")] string ReasoningEffort);

public sealed record ChatResultPayload(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("turn_id")] int TurnId,
    [property: JsonPropertyName("final_text")] string FinalText);

public sealed record CompressInputPayload(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("model_id")] string ModelId);

public sealed record CompressResultPayload(
    [property: JsonPropertyName("turn_id")] int TurnId);

public sealed record GetSessionsPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("search")] string Search);

public sealed record SessionMetaPayload(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

public sealed record SessionDetailPayload(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("reasoning_effort")] string ReasoningEffort,
    [property: JsonPropertyName("active_version")] int ActiveVersion,
    [property: JsonPropertyName("last_turn_id")] int LastTurnId,
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? Usage,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt);

public sealed record SessionMessagePayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("turn_id")] int TurnId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("thinking_content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ThinkingContent,
    [property: JsonPropertyName("token_count")] int TokenCount,
    [property: JsonPropertyName("extra_metadata")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ExtraMetadata,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("to_api")] bool ToApi,
    [property: JsonPropertyName("to_frontend")] bool ToFrontend,
    [property: JsonPropertyName("event_type")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EventType,
    [property: JsonPropertyName("agent_type")] string AgentType,
    [property: JsonPropertyName("sub_task_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SubTaskId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record PageResultPayload<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("total_pages")] int TotalPages,
    [property: JsonPropertyName("next_cursor")] string? NextCursor = null,
    [property: JsonPropertyName("has_more")] bool HasMore = false,
    [property: JsonPropertyName("total_estimate")] int? TotalEstimate = null);

public sealed record PageRequestPayload(
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("page_size")] int PageSize,
    [property: JsonPropertyName("sort_by")] string SortBy,
    [property: JsonPropertyName("sort_dir")] string SortDir,
    [property: JsonPropertyName("filters")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Filters = null);

public sealed class AgentEventPayload
{
    [JsonPropertyName("turn_id")]
    public int TurnId { get; init; }

    [JsonPropertyName("sub_task_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubTaskId { get; init; }

    [JsonPropertyName("seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seq { get; init; }

    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; init; }

    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolId { get; init; }

    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phase { get; init; }

    [JsonPropertyName("tool_args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ToolArgs { get; init; }

    [JsonPropertyName("success")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Success { get; init; }

    [JsonPropertyName("display_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayText { get; init; }

    [JsonPropertyName("activity_kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActivityKind { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Usage { get; init; }

    [JsonPropertyName("compression_phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompressionPhase { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }
}
