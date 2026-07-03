using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class PlanningBridgeHandlers
{
    public static BridgeDispatcher RegisterPlanningHandlers(
        this BridgeDispatcher dispatcher,
        IPlanningService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GetChapterPlans", async (context, cancellationToken) =>
            await service.GetChapterPlansAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken));

        dispatcher.Register("UpdateChapterPlan", async (context, cancellationToken) =>
        {
            await service.UpdateChapterPlanAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadObjectArg<UpdateChapterPlanPayload>(context.Payload, 1, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("GetTimelineEntries", async (context, cancellationToken) =>
            await service.GetTimelineEntriesAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadIntArg(context.Payload, 1, "fromChapter"),
                ReadIntArg(context.Payload, 2, "toChapter"),
                cancellationToken));

        dispatcher.Register("CreateTimelineEntry", async (context, cancellationToken) =>
            await service.CreateTimelineEntryAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadObjectArg<CreateTimelineEntryPayload>(context.Payload, 1, "input"),
                cancellationToken));

        dispatcher.Register("UpdateTimelineEntry", async (context, cancellationToken) =>
        {
            await service.UpdateTimelineEntryAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "entryId"),
                ReadObjectArg<UpdateTimelineEntryPayload>(context.Payload, 2, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteTimelineEntry", async (context, cancellationToken) =>
        {
            await service.DeleteTimelineEntryAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "entryId"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("GetStoryArcs", async (context, cancellationToken) =>
            await service.GetStoryArcsAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken));

        dispatcher.Register("CreateStoryArc", async (context, cancellationToken) =>
            await service.CreateStoryArcAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadObjectArg<CreateStoryArcPayload>(context.Payload, 1, "input"),
                cancellationToken));

        dispatcher.Register("UpdateStoryArc", async (context, cancellationToken) =>
        {
            await service.UpdateStoryArcAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "arcId"),
                ReadObjectArg<UpdateStoryArcPayload>(context.Payload, 2, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteStoryArc", async (context, cancellationToken) =>
        {
            await service.DeleteStoryArcAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "arcId"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("GetArcNodes", async (context, cancellationToken) =>
            await service.GetArcNodesAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadIntArg(context.Payload, 1, "fromChapter"),
                ReadIntArg(context.Payload, 2, "toChapter"),
                cancellationToken));

        dispatcher.Register("CreateArcNode", async (context, cancellationToken) =>
            await service.CreateArcNodeAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadObjectArg<CreateArcNodePayload>(context.Payload, 1, "input"),
                cancellationToken));

        dispatcher.Register("UpdateArcNode", async (context, cancellationToken) =>
        {
            await service.UpdateArcNodeAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "nodeId"),
                ReadObjectArg<UpdateArcNodePayload>(context.Payload, 2, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteArcNode", async (context, cancellationToken) =>
        {
            await service.DeleteArcNodeAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "nodeId"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("GetReaderPerspectives", async (context, cancellationToken) =>
            await service.GetReaderPerspectivesAsync(ReadLongArg(context.Payload, 0, "novelId"), cancellationToken));

        dispatcher.Register("CreateReaderPerspective", async (context, cancellationToken) =>
            await service.CreateReaderPerspectiveAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadObjectArg<CreateReaderPerspectivePayload>(context.Payload, 1, "input"),
                cancellationToken));

        dispatcher.Register("UpdateReaderPerspective", async (context, cancellationToken) =>
        {
            await service.UpdateReaderPerspectiveAsync(
                ReadLongArg(context.Payload, 1, "novelId"),
                ReadLongArg(context.Payload, 0, "perspectiveId"),
                ReadObjectArg<UpdateReaderPerspectivePayload>(context.Payload, 2, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteReaderPerspective", async (context, cancellationToken) =>
        {
            await service.DeleteReaderPerspectiveAsync(
                ReadLongArg(context.Payload, 1, "novelId"),
                ReadLongArg(context.Payload, 0, "perspectiveId"),
                cancellationToken);
            return null;
        });

        return dispatcher;
    }

    private static T ReadObjectArg<T>(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(argumentName, "Value must be an object.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value.GetRawText(), BridgeJson.SerializerOptions)
                ?? throw Invalid(argumentName, "Value must not be null.");
        }
        catch (JsonException)
        {
            throw Invalid(argumentName, "Value must match the expected object shape.");
        }
    }

    private static long ReadLongArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw Invalid(argumentName, "Value must be an integer.");
        }

        return number;
    }

    private static int ReadIntArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Invalid(argumentName, "Value must be an integer.");
        }

        return number;
    }

    private static JsonElement ReadArg(JsonElement? payload, int index, string argumentName)
    {
        if (payload is null ||
            payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("args", out var args) ||
            args.ValueKind != JsonValueKind.Array ||
            args.GetArrayLength() <= index)
        {
            throw Invalid(argumentName, $"Argument at index {index} is required.");
        }

        return args[index];
    }

    private static BridgeValidationException Invalid(string argumentName, string message)
    {
        return new BridgeValidationException(
            $"Invalid argument '{argumentName}'.",
            new Dictionary<string, string> { [argumentName] = message });
    }
}
