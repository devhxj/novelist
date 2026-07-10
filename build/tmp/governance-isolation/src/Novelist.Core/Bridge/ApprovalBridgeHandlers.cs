using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ApprovalBridgeHandlers
{
    public static BridgeDispatcher RegisterApprovalHandlers(
        this BridgeDispatcher dispatcher,
        IApprovalCoordinator approvals)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(approvals);

        dispatcher.Register("ApproveTool", async (context, cancellationToken) =>
            await approvals.CompleteAsync(
                new ToolApprovalDecisionPayload(
                    ReadStringArg(context.Payload, 0, "toolId", allowEmpty: false),
                    ReadBoolArg(context.Payload, 1, "approved"),
                    ReadStringArg(context.Payload, 2, "feedback", allowEmpty: true)),
                cancellationToken));

        return dispatcher;
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

    private static bool ReadBoolArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind is JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind is JsonValueKind.False)
        {
            return false;
        }

        throw Invalid(argumentName, "Value must be a boolean.");
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
