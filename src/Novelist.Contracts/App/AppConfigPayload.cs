using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record AppConfigPayload(
    [property: JsonPropertyName("initialized")] bool Initialized,
    [property: JsonPropertyName("data_dir")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DataDir,
    [property: JsonPropertyName("update_check")] UpdateCheckConfigurationPayload UpdateCheck);
