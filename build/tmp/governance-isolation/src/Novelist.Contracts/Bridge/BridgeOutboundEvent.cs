using System.Text.Json.Serialization;

namespace Novelist.Contracts.Bridge;

public sealed record BridgeOutboundEvent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("payload")] object? Payload)
{
    public static BridgeOutboundEvent Create(string name, object? payload)
    {
        return new BridgeOutboundEvent(BridgeMessageKinds.Event, name, payload);
    }
}
