using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record EmbeddingConfigPayload(
    [property: JsonPropertyName("provider_key")] string ProviderKey,
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("dimensions")] int? Dimensions,
    [property: JsonPropertyName("user")] string User);

public sealed record SqliteVecStatusPayload(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("runtime_identifier")] string RuntimeIdentifier,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("error")] string Error);
