using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record AppSettingsPayload(
    [property: JsonPropertyName("ID")] int Id,
    [property: JsonPropertyName("last_novel_id")] long LastNovelId,
    [property: JsonPropertyName("selected_model_key")] string SelectedModelKey,
    [property: JsonPropertyName("reasoning_effort")] string ReasoningEffort,
    [property: JsonPropertyName("approval_mode")] string ApprovalMode,
    [property: JsonPropertyName("chat_panel_width")] int ChatPanelWidth,
    [property: JsonPropertyName("last_session_id")] string LastSessionId,
    [property: JsonPropertyName("user_name")] string UserName);
