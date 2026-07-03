using System.Text.Json.Serialization;

namespace Novelist.Contracts.Bridge;

public sealed record BridgeResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("result")] object? Result,
    [property: JsonPropertyName("error")] BridgeError? Error)
{
    public static BridgeResponse Success(string id, object? result)
    {
        return new BridgeResponse(BridgeMessageKinds.Response, id, true, result, null);
    }

    public static BridgeResponse Failure(string? id, BridgeError error)
    {
        return new BridgeResponse(BridgeMessageKinds.Response, id, false, null, error);
    }
}
