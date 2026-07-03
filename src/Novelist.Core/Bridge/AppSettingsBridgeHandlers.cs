using System.Text.Json;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class AppSettingsBridgeHandlers
{
    public static BridgeDispatcher RegisterAppSettingsHandlers(
        this BridgeDispatcher dispatcher,
        IAppSettingsService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("GetSettings", async (_, cancellationToken) =>
            await service.GetSettingsAsync(cancellationToken));

        dispatcher.Register("SaveSettings", async (_, cancellationToken) =>
        {
            await service.SaveSettingsAsync(cancellationToken);
            return null;
        });

        dispatcher.Register("SetSelectedModel", async (context, cancellationToken) =>
        {
            await service.SetSelectedModelAsync(
                ReadStringArg(context.Payload, 0, "selectedModelKey", allowEmpty: false),
                ReadStringArg(context.Payload, 1, "reasoningEffort", allowEmpty: true),
                cancellationToken);
            return null;
        });

        dispatcher.Register("SetReasoningEffort", async (context, cancellationToken) =>
        {
            await service.SetReasoningEffortAsync(
                ReadStringArg(context.Payload, 0, "reasoningEffort", allowEmpty: true),
                cancellationToken);
            return null;
        });

        dispatcher.Register("SetChatPanelWidth", async (context, cancellationToken) =>
        {
            await service.SetChatPanelWidthAsync(ReadIntArg(context.Payload, 0, "width"), cancellationToken);
            return null;
        });

        dispatcher.Register("SetLastSession", async (context, cancellationToken) =>
        {
            await service.SetLastSessionAsync(
                ReadStringArg(context.Payload, 0, "sessionId", allowEmpty: true),
                cancellationToken);
            return null;
        });

        dispatcher.Register("SetApprovalMode", async (context, cancellationToken) =>
        {
            await service.SetApprovalModeAsync(
                ReadStringArg(context.Payload, 0, "approvalMode", allowEmpty: false),
                cancellationToken);
            return null;
        });

        dispatcher.Register("SaveUserName", async (context, cancellationToken) =>
        {
            await service.SaveUserNameAsync(
                ReadStringArg(context.Payload, 0, "userName", allowEmpty: true),
                cancellationToken);
            return null;
        });

        dispatcher.Register("SaveAvatar", async (context, cancellationToken) =>
        {
            await service.SaveAvatarAsync(ReadByteArrayArg(context.Payload, 0, "avatar"), cancellationToken);
            return null;
        });

        return dispatcher;
    }

    private static string ReadStringArg(JsonElement? payload, int index, string argumentName, bool allowEmpty)
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

    private static int ReadIntArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw Invalid(argumentName, "Value must be an integer.");
        }

        return number;
    }

    private static byte[] ReadByteArrayArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(argumentName, "Value must be an array of bytes.");
        }

        var data = new byte[value.GetArrayLength()];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number) || number is < 0 or > 255)
            {
                throw Invalid(argumentName, "Every byte must be an integer from 0 to 255.");
            }

            data[i++] = (byte)number;
        }

        return data;
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
