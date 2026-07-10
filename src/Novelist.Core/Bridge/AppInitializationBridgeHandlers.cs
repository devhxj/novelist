using System.Text.Json;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class AppInitializationBridgeHandlers
{
    public static BridgeDispatcher RegisterAppInitializationHandlers(
        this BridgeDispatcher dispatcher,
        IAppInitializationService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("IsInitialized", async (_, cancellationToken) =>
            await service.IsInitializedAsync(cancellationToken));

        dispatcher.Register("Initialize", async (context, cancellationToken) =>
        {
            await service.InitializeAsync(ReadStringArg(context.Payload, 0, "dataDir"), cancellationToken);
            return null;
        }, BridgeOperationAccess.Exclusive);

        dispatcher.Register("GetAppConfig", async (_, cancellationToken) =>
            await service.GetAppConfigAsync(cancellationToken));

        dispatcher.Register("UpdateDataDir", async (context, cancellationToken) =>
        {
            await service.UpdateDataDirectoryAsync(ReadStringArg(context.Payload, 0, "dataDir"), cancellationToken);
            return null;
        }, BridgeOperationAccess.Exclusive);

        dispatcher.Register("GetPlatform", async (_, cancellationToken) =>
            await service.GetPlatformAsync(cancellationToken));

        return dispatcher;
    }

    private static string ReadStringArg(JsonElement? payload, int index, string argumentName)
    {
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeValidationException(
                $"Missing argument '{argumentName}'.",
                new Dictionary<string, string> { [argumentName] = "Payload must be an object with an args array." });
        }

        if (!payload.Value.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
        {
            throw new BridgeValidationException(
                $"Missing argument '{argumentName}'.",
                new Dictionary<string, string> { [argumentName] = "Payload args array is required." });
        }

        if (args.GetArrayLength() <= index)
        {
            throw new BridgeValidationException(
                $"Missing argument '{argumentName}'.",
                new Dictionary<string, string> { [argumentName] = $"Argument at index {index} is required." });
        }

        var value = args[index];
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new BridgeValidationException(
                $"Invalid argument '{argumentName}'.",
                new Dictionary<string, string> { [argumentName] = "Value must be a non-empty string." });
        }

        return value.GetString()!;
    }
}
