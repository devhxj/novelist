using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record PlatformPayload(
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("defaultPath")] string DefaultPath);
