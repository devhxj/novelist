using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record EmbeddingConfigPayload(
    [property: JsonPropertyName("provider_key")] string ProviderKey,
    [property: JsonPropertyName("endpoint_url")] string EndpointUrl,
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("dimensions")] int? Dimensions,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("provider_type")] string ProviderType = "",
    [property: JsonPropertyName("onnx_model_path")] string OnnxModelPath = "",
    [property: JsonPropertyName("onnx_vocab_path")] string OnnxVocabPath = "",
    [property: JsonPropertyName("max_sequence_length")] int? MaxSequenceLength = null,
    [property: JsonPropertyName("normalize_embeddings")] bool NormalizeEmbeddings = true);

public sealed record SqliteVecStatusPayload(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("runtime_identifier")] string RuntimeIdentifier,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("error")] string Error);
