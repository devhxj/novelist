using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class GitHistoryBridgeHandlers
{
    public static BridgeDispatcher RegisterGitHistoryHandlers(
        this BridgeDispatcher dispatcher,
        IVersionControlService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GetGitCommits", async (context, cancellationToken) =>
            await InvokeGitAsync(
                context,
                service.GetCommitSummariesAsync(
                    ReadObjectArg<GetGitCommitsPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("GetGitCommitFiles", async (context, cancellationToken) =>
            await InvokeGitAsync(
                context,
                service.GetCommitFilesAsync(
                    ReadObjectArg<GetGitCommitFilesPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("GetGitFileDiff", async (context, cancellationToken) =>
            await InvokeGitAsync(
                context,
                service.GetFileDiffAsync(
                    ReadObjectArg<GetGitFileDiffPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        return dispatcher;
    }

    private static async ValueTask<object?> InvokeGitAsync<T>(
        BridgeInvocationContext context,
        ValueTask<T> operation)
    {
        try
        {
            return await operation;
        }
        catch (VersionControlException ex)
        {
            throw new BridgeRequestException(
                BridgeErrorCodes.VersionControlError,
                ex.Message,
                new { method = context.Method },
                retryable: true);
        }
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
