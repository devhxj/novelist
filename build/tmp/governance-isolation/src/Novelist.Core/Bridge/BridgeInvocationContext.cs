using System.Text.Json;

namespace Novelist.Core.Bridge;

public sealed record BridgeInvocationContext(
    string Id,
    string Method,
    JsonElement? Payload,
    TimeSpan? Deadline);
