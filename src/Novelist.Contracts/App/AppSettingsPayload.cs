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
    [property: JsonPropertyName("user_name")] string UserName,
    [property: JsonPropertyName("git_author_name")] string GitAuthorName = "",
    [property: JsonPropertyName("git_author_email")] string GitAuthorEmail = "",
    [property: JsonPropertyName("update_check_enabled")] bool UpdateCheckEnabled = false,
    [property: JsonPropertyName("update_check_endpoint_url")] string UpdateCheckEndpointUrl = "",
    [property: JsonPropertyName("update_check_dismissed_version")] string UpdateCheckDismissedVersion = "",
    [property: JsonPropertyName("update_check_last_checked_at")]
    DateTimeOffset? UpdateCheckLastCheckedAt = null,
    [property: JsonPropertyName("sidebar_width")] int SidebarWidth = 280,
    [property: JsonPropertyName("metadata_panel_width")] int MetadataPanelWidth = 320,
    [property: JsonPropertyName("window_x")]
    int? WindowX = null,
    [property: JsonPropertyName("window_y")]
    int? WindowY = null,
    [property: JsonPropertyName("window_width")] int WindowWidth = 1280,
    [property: JsonPropertyName("window_height")] int WindowHeight = 840,
    [property: JsonPropertyName("window_maximized")] bool WindowMaximized = false);
