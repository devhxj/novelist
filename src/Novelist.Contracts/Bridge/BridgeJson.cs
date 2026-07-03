using System.Text.Json;

namespace Novelist.Contracts.Bridge;

public static class BridgeJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}
