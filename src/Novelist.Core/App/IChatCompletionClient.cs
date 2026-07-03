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
    IReadOnlyList<ChatCompletionMessage> Messages);

public sealed record ChatCompletionMessage(
    string Role,
    string Content,
    string? ThinkingContent = null);

public enum ChatCompletionStreamEventKind
{
    Thinking,
    Content,
    Usage
}

public sealed record ChatCompletionStreamEvent(
    ChatCompletionStreamEventKind Kind,
    string Data = "",
    JsonElement? Usage = null);
