using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

public sealed record CopyableDiagnosticPayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("task_id")]
    string? TaskId,
    [property: JsonPropertyName("run_id")]
    string? RunId,
    [property: JsonPropertyName("bridge_method")]
    string? BridgeMethod,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp);
