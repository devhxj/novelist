using System.Text.Json;
using Microsoft.Extensions.AI;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Agent;

public sealed class NovelistMafChatToolExecutor : IChatToolExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NovelistMafToolRegistry _registry;

    public NovelistMafChatToolExecutor(NovelistMafToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(long novelId)
    {
        return _registry
            .CreateTools(new NovelistMafToolContext(novelId))
            .Select(function => new ChatToolDefinition(
                function.Name,
                function.Description,
                function.JsonSchema.Clone()))
            .ToArray();
    }

    public async ValueTask<ChatToolExecutionResult> ExecuteAsync(
        ChatToolExecutionContext context,
        ChatToolCall call,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(call);
        cancellationToken.ThrowIfCancellationRequested();
        var tools = _registry.CreateTools(new NovelistMafToolContext(
            context.NovelId,
            context.SessionId,
            context.TurnId,
            call.Id));
        var function = tools.SingleOrDefault(item => string.Equals(item.Name, call.Name, StringComparison.Ordinal));
        if (function is null)
        {
            return ChatToolExecutionResult.Failure($"Tool '{call.Name}' is not registered.");
        }

        try
        {
            var arguments = ParseArguments(call.ArgumentsJson);
            var result = await function.InvokeAsync(arguments, cancellationToken);
            var data = result is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(result, JsonOptions);
            return ChatToolExecutionResult.Succeeded(data);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ChatToolExecutionResult.Failure(ex.Message);
        }
    }

    private static AIFunctionArguments ParseArguments(string argumentsJson)
    {
        var arguments = new AIFunctionArguments();
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return arguments;
        }

        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = ToArgumentValue(property.Value);
        }

        return arguments;
    }

    private static object? ToArgumentValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => ToArrayArgumentValue(value),
            _ => value.Clone()
        };
    }

    private static object ToArrayArgumentValue(JsonElement value)
    {
        var items = value.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            return Array.Empty<object>();
        }

        if (items.All(item => item.ValueKind == JsonValueKind.String))
        {
            return items.Select(item => item.GetString() ?? string.Empty).ToArray();
        }

        if (items.All(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _)))
        {
            return items.Select(item => item.GetInt32()).ToArray();
        }

        if (items.All(item => item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out _)))
        {
            return items.Select(item => item.GetDouble()).ToArray();
        }

        return items.Select(ToArgumentValue).ToArray();
    }
}
