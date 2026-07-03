using System.Text.Json;

namespace Novelist.Core.App;

public interface IChatCompletionClient
{
    IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken);

    ValueTask<string> GenerateTextAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken);
}

public sealed record ChatCompletionRequest(
    string ProviderName,
    string ModelId,
    string ReasoningEffort,
    IReadOnlyList<ChatCompletionMessage> Messages,
    IReadOnlyList<ChatToolDefinition>? Tools = null);

public sealed record ChatCompletionMessage(
    string Role,
    string Content,
    string? ThinkingContent = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ToolName = null);

public sealed record ChatToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersSchema);

public sealed record ChatToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

public sealed record ChatToolExecutionContext(
    long NovelId,
    string SessionId,
    int TurnId);

public sealed record ChatToolExecutionResult(
    bool Success,
    JsonElement? Data,
    string Error)
{
    public static ChatToolExecutionResult Succeeded(JsonElement data)
    {
        return new ChatToolExecutionResult(true, data.Clone(), string.Empty);
    }

    public static ChatToolExecutionResult Failure(string error)
    {
        return new ChatToolExecutionResult(false, null, error);
    }
}

public interface IChatToolExecutor
{
    IReadOnlyList<ChatToolDefinition> GetToolDefinitions(long novelId);

    ValueTask<ChatToolExecutionResult> ExecuteAsync(
        ChatToolExecutionContext context,
        ChatToolCall call,
        CancellationToken cancellationToken);
}

public enum ChatCompletionStreamEventKind
{
    Thinking,
    Content,
    Usage,
    ToolCall
}

public sealed record ChatCompletionStreamEvent(
    ChatCompletionStreamEventKind Kind,
    string Data = "",
    JsonElement? Usage = null,
    ChatToolCall? ToolCall = null);
