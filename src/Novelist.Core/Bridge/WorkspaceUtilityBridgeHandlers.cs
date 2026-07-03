using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class WorkspaceUtilityBridgeHandlers
{
    public static BridgeDispatcher RegisterWorkspaceUtilityHandlers(
        this BridgeDispatcher dispatcher,
        ISkillCatalogService skills,
        IWorkspaceSearchService search,
        INovelExportService exports,
        IWritingStatisticsService writing,
        IStoryMemorySearchService? storyMemory = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(writing);

        dispatcher.Register("ListSkills", async (context, cancellationToken) =>
            await skills.ListSkillsAsync(
                ReadObjectArg<ListSkillsPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("DeleteSkill", async (context, cancellationToken) =>
        {
            await skills.DeleteSkillAsync(
                ReadObjectArg<DeleteSkillPayload>(context.Payload, 0, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("ListSlashCommands", async (context, cancellationToken) =>
            await skills.ListSlashCommandsAsync(
                ReadObjectArg<ListSlashCommandsPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("ExtractStyle", async (context, cancellationToken) =>
            await skills.ExtractStyleAsync(
                ReadObjectArg<ExtractStylePayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("SearchAll", async (context, cancellationToken) =>
            await search.SearchAllAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadStringArg(context.Payload, 1, "query", allowEmpty: true),
                cancellationToken));

        if (storyMemory is not null)
        {
            dispatcher.Register("SearchStoryMemory", async (context, cancellationToken) =>
                await storyMemory.SearchAsync(
                    ReadObjectArg<SearchStoryMemoryPayload>(context.Payload, 0, "input"),
                    cancellationToken));
        }

        dispatcher.Register("RebuildNovelIndex", async (context, cancellationToken) =>
        {
            await search.RebuildNovelIndexAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("ExportNovel", async (context, cancellationToken) =>
        {
            await exports.ExportNovelAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadStringArg(context.Payload, 1, "format", allowEmpty: false),
                cancellationToken);
            return null;
        });

        dispatcher.Register("GetWritingActivity", async (context, cancellationToken) =>
            await writing.GetWritingActivityAsync(
                ReadIntArg(context.Payload, 0, "months"),
                cancellationToken));

        dispatcher.Register("GetWritingStats", async (_, cancellationToken) =>
            await writing.GetWritingStatsAsync(cancellationToken));

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

    private static string ReadStringArg(
        JsonElement? payload,
        int index,
        string argumentName,
        bool allowEmpty)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(argumentName, "Value must be a string.");
        }

        var text = value.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw Invalid(argumentName, "Value must be a non-empty string.");
        }

        return text;
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
