using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceWritingBridgeHandlers
{
    public static BridgeDispatcher RegisterReferenceWritingHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceWritingService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GenerateReferenceBlueprints", async (context, cancellationToken) =>
            await ExecuteAsync(() => service.GenerateBlueprintsAsync(
                ReadObjectArg<GenerateReferenceBlueprintsPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("GetReferenceWritingSession", async (context, cancellationToken) =>
            await ExecuteAsync(() => service.GetSessionAsync(
                ReadObjectArg<GetReferenceWritingSessionPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("SelectReferenceBlueprint", async (context, cancellationToken) =>
            await ExecuteAsync(() => service.SelectBlueprintAsync(
                ReadObjectArg<SelectReferenceBlueprintPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("GenerateReferenceDraftCandidates", async (context, cancellationToken) =>
            await ExecuteAsync(() => service.GenerateDraftCandidatesAsync(
                ReadObjectArg<GenerateReferenceDraftCandidatesPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        return dispatcher;
    }

    private static async ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (ReferenceWritingException exception)
        {
            throw ToBridgeException(exception.ErrorCode, exception.Message);
        }
        catch (ReferenceMaterializationException exception)
        {
            throw ToBridgeException(exception.ErrorCode, exception.Message);
        }
    }

    private static BridgeRequestException ToBridgeException(string errorCode, string message) => new(
        errorCode,
        message,
        new { error_code = errorCode });

    private static T ReadObjectArg<T>(JsonElement? payload, int index, string argumentName)
    {
        if (payload is null ||
            payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("args", out var args) ||
            args.ValueKind != JsonValueKind.Array ||
            args.GetArrayLength() <= index ||
            args[index].ValueKind != JsonValueKind.Object)
        {
            throw Invalid(argumentName, "Value must be an object.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(args[index].GetRawText(), BridgeJson.SerializerOptions)
                ?? throw Invalid(argumentName, "Value must not be null.");
        }
        catch (JsonException)
        {
            throw Invalid(argumentName, "Value must match the expected object shape.");
        }
    }

    private static BridgeValidationException Invalid(string argumentName, string message) => new(
        $"Invalid argument '{argumentName}'.",
        new Dictionary<string, string> { [argumentName] = message });
}
