using System.Runtime.CompilerServices;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationModelPreflightTests
{
    [Fact]
    public async Task VerifyAsyncRejectsMissingSelectedLlmBeforeEmbeddingIsCalled()
    {
        var embeddings = new RecordingEmbeddingClient();
        var preflight = new ReferenceMaterializationModelPreflight(
            new FixedSettingsService(string.Empty),
            new RecordingChatCompletionClient("{\"ok\":true}"),
            new FixedEmbeddingConfiguration(new EmbeddingRequestOptions("embedding", "https://example.test", "key", "embed", 3, null)),
            embeddings);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await preflight.VerifyAsync(CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmNotConfigured, exception.ErrorCode);
        Assert.Equal(0, embeddings.CallCount);
    }

    [Fact]
    public async Task VerifyAsyncRejectsEmbeddingVectorsWithInvalidDimensions()
    {
        var preflight = new ReferenceMaterializationModelPreflight(
            new FixedSettingsService("qwen/qwen-plus"),
            new RecordingChatCompletionClient("{\"ok\":true}"),
            new FixedEmbeddingConfiguration(new EmbeddingRequestOptions("embedding", "https://example.test", "key", "embed", 3, null)),
            new RecordingEmbeddingClient(new EmbeddingBatchResult("embed", 3, [new EmbeddingItemResult(0, [1f, 2f])], new EmbeddingUsage(1, 1))));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await preflight.VerifyAsync(CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed, exception.ErrorCode);
    }

    [Fact]
    public async Task VerifyAsyncReturnsTheFrozenSelectedModelAndEmbeddingDimensions()
    {
        var chat = new RecordingChatCompletionClient("{\"ok\":true}");
        var embeddings = new RecordingEmbeddingClient(new EmbeddingBatchResult(
            "embed",
            3,
            [new EmbeddingItemResult(0, [1f, 2f, 3f])],
            new EmbeddingUsage(1, 1)));
        var preflight = new ReferenceMaterializationModelPreflight(
            new FixedSettingsService("qwen/qwen-plus"),
            chat,
            new FixedEmbeddingConfiguration(new EmbeddingRequestOptions("embedding", "https://example.test", "key", "embed", 3, null)),
            embeddings);

        var result = await preflight.VerifyAsync(CancellationToken.None);

        Assert.Equal("qwen", result.Llm.Provider);
        Assert.Equal("qwen-plus", result.Llm.ModelId);
        Assert.Equal("embedding", result.Embedding.Provider);
        Assert.Equal(3, result.Embedding.Dimensions);
        Assert.Equal(1, chat.CallCount);
        Assert.Equal(1, embeddings.CallCount);
    }

    [Fact]
    public async Task VerifyAsyncReservesOutputCapacityForThinkingModelHealthChecks()
    {
        var chat = new MinimumOutputChatCompletionClient(minimumOutputTokens: 256);
        var preflight = new ReferenceMaterializationModelPreflight(
            new FixedSettingsService("deepseek/deepseek-v4-pro"),
            chat,
            new FixedEmbeddingConfiguration(new EmbeddingRequestOptions("embedding", "https://example.test", "key", "embed", 3, null)),
            new RecordingEmbeddingClient());

        await preflight.VerifyAsync(CancellationToken.None);

        Assert.NotNull(chat.Request);
        Assert.Equal(256, chat.Request.MaxOutputTokens);
    }

    private sealed class FixedSettingsService(string selectedModelKey) : IAppSettingsService
    {
        public ValueTask<AppSettingsPayload> GetSettingsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AppSettingsPayload(1, 0, selectedModelKey, "high", "manual", 360, string.Empty, string.Empty));

        public ValueTask SaveSettingsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SaveAvatarAsync(byte[] data, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SaveUserNameAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetApprovalModeAsync(string mode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetChatPanelWidthAsync(int width, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetLastNovelAsync(long novelId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetLastSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetReasoningEffortAsync(string effort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SetSelectedModelAsync(string modelKey, string effort, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedEmbeddingConfiguration(EmbeddingRequestOptions? options) : IEmbeddingConfigurationService
    {
        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(options);
    }

    private sealed class RecordingChatCompletionClient(string content) : IChatCompletionClient
    {
        public int CallCount { get; private set; }

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.CompletedTask;
            yield return new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, content);
        }
    }

    private sealed class RecordingEmbeddingClient(EmbeddingBatchResult? result = null) : IEmbeddingClient
    {
        public int CallCount { get; private set; }

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(result ?? new EmbeddingBatchResult(
                options.ModelId,
                3,
                [new EmbeddingItemResult(0, [1f, 2f, 3f])],
                new EmbeddingUsage(1, 1)));
        }
    }

    private sealed class MinimumOutputChatCompletionClient(int minimumOutputTokens) : IChatCompletionClient
    {
        public ChatCompletionRequest? Request { get; private set; }

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Request = request;
            await Task.CompletedTask;
            if (request.MaxOutputTokens >= minimumOutputTokens)
            {
                yield return new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "{\"ok\":true}");
            }
        }
    }
}
