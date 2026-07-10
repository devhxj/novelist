using System.Text.Json.Serialization;

namespace Novelist.Contracts.Bridge;

public sealed record BridgeError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] object? Details = null,
    [property: JsonPropertyName("retryable")] bool Retryable = false);
