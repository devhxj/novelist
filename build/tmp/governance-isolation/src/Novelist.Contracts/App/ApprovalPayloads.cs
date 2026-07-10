using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record ToolApprovalRequestPayload(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("turn_id")] int TurnId,
    [property: JsonPropertyName("tool_id")] string ToolId,
    [property: JsonPropertyName("tool_name")] string ToolName,
    [property: JsonPropertyName("approval_type")] string ApprovalType,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("display_text")] string DisplayText,
    [property: JsonPropertyName("activity_kind")] string ActivityKind);

public sealed record ToolApprovalDecisionPayload(
    [property: JsonPropertyName("tool_id")] string ToolId,
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("feedback")] string Feedback);

public sealed record ToolApprovalResultPayload(
    [property: JsonPropertyName("tool_id")] string ToolId,
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("feedback")] string Feedback);
