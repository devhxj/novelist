using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record CheckForUpdatesPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("manual")] bool Manual);

public sealed record UpdateCheckConfigurationPayload(
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("default_enabled")] bool DefaultEnabled,
    [property: JsonPropertyName("timeout_ms")] int TimeoutMs);

public sealed record UpdateCheckResultPayload(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("latest_version")]
    string? LatestVersion,
    [property: JsonPropertyName("release_url")]
    string? ReleaseUrl,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("error_code")]
    string? ErrorCode,
    [property: JsonPropertyName("error_message")]
    string? ErrorMessage);

public sealed record UpdateCheckSettingsPayload(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("dismissed_version")] string DismissedVersion,
    [property: JsonPropertyName("last_checked_at")]
    DateTimeOffset? LastCheckedAt);

public sealed record SaveUpdateCheckSettingsPayload(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("dismissed_version")] string DismissedVersion);

public sealed record LayoutSettingsPayload(
    [property: JsonPropertyName("sidebar_width")] int SidebarWidth,
    [property: JsonPropertyName("chat_panel_width")] int ChatPanelWidth,
    [property: JsonPropertyName("metadata_panel_width")] int MetadataPanelWidth);

public sealed record SaveLayoutSettingsPayload(
    [property: JsonPropertyName("sidebar_width")] int SidebarWidth,
    [property: JsonPropertyName("chat_panel_width")] int ChatPanelWidth,
    [property: JsonPropertyName("metadata_panel_width")] int MetadataPanelWidth);

public sealed record WindowSettingsPayload(
    [property: JsonPropertyName("x")]
    int? X,
    [property: JsonPropertyName("y")]
    int? Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("maximized")] bool Maximized);

public sealed record SaveWindowSettingsPayload(
    [property: JsonPropertyName("x")]
    int? X,
    [property: JsonPropertyName("y")]
    int? Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("maximized")] bool Maximized);
