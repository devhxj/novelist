namespace Novelist.Core.Bridge;

public sealed record BridgeDispatchResult(string? OutboundJson, string? CancelRequestId)
{
    public static BridgeDispatchResult Outbound(string json)
    {
        return new BridgeDispatchResult(json, null);
    }

    public static BridgeDispatchResult Cancel(string requestId)
    {
        return new BridgeDispatchResult(null, requestId);
    }
}
