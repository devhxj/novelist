using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record ModelInfoPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("context_window")] int ContextWindow,
    [property: JsonPropertyName("max_output_tokens")] int MaxOutputTokens,
    [property: JsonPropertyName("supports_thinking")] bool SupportsThinking,
    [property: JsonPropertyName("reasoning_levels")] IReadOnlyList<string>? ReasoningLevels,
    [property: JsonPropertyName("supports_vision")] bool SupportsVision);

public sealed record ProviderViewPayload(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("chat_url")] string ChatUrl,
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("platform_url")] string PlatformUrl,
    [property: JsonPropertyName("help_text")] string HelpText,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("builtin_models")] IReadOnlyList<ModelInfoPayload> BuiltinModels,
    [property: JsonPropertyName("custom_models")] IReadOnlyList<ModelInfoPayload> CustomModels);

public sealed record LlmConfigViewPayload(
    [property: JsonPropertyName("providers")] IReadOnlyList<ProviderViewPayload> Providers);

public sealed record AvailableModelPayload(
    [property: JsonPropertyName("Key")] string Key,
    [property: JsonPropertyName("ProviderName")] string ProviderName,
    [property: JsonPropertyName("ModelName")] string ModelName,
    [property: JsonPropertyName("ContextWindow")] int ContextWindow,
    [property: JsonPropertyName("MaxOutputTokens")] int MaxOutputTokens,
    [property: JsonPropertyName("SupportsThinking")] bool SupportsThinking,
    [property: JsonPropertyName("ReasoningLevels")] IReadOnlyList<string> ReasoningLevels,
    [property: JsonPropertyName("SupportsVision")] bool SupportsVision);

public sealed record TestConnectionPayload(
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("chat_url")] string ChatUrl,
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("model_id")] string ModelId);
