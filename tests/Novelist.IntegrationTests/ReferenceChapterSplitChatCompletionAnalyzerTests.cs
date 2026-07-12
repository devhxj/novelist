using System.Runtime.CompilerServices;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceChapterSplitChatCompletionAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsyncUsesTheSelectedModelAndReturnsOnlySchemaLockedChapterMetadata()
    {
        var chat = new RecordingChatCompletionClient(
        [
            new ChatCompletionStreamEvent(
                ChatCompletionStreamEventKind.Content,
                """
                {"pattern_kind":"markdown_heading","delimiter_template":"# {title}","confidence":0.9,"evidence_offsets":[0,16]}
                """)
        ]);
        var analyzer = new ReferenceChapterSplitChatCompletionAnalyzer(
            new FixedAppSettingsService("qwen/qwen-plus", "high"),
            chat);

        var result = await analyzer.AnalyzeAsync(
            new ReferenceChapterSplitModelRequest(
                99,
                "source-hash",
                "# 第一章\n\n雨声压住窗沿。\n\n# 第二章\n\n门外响起第三次敲门。"),
            CancellationToken.None);

        Assert.Equal("markdown_heading", result.PatternKind);
        Assert.Equal("# {title}", result.DelimiterTemplate);
        Assert.Equal([0, 16], result.EvidenceOffsets);
        Assert.Equal("qwen", result.ProviderName);
        Assert.Equal("qwen-plus", result.ModelId);
        Assert.NotNull(chat.LastRequest);
        Assert.Equal("qwen", chat.LastRequest.ProviderName);
        Assert.Equal("qwen-plus", chat.LastRequest.ModelId);
        Assert.Equal("high", chat.LastRequest.ReasoningEffort);
        Assert.Contains("Return strict JSON only", chat.LastRequest.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("normalized_source_sample", chat.LastRequest.Messages[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("source-hash", chat.LastRequest.Messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsyncRejectsUnexpectedModelOutputFields()
    {
        var chat = new RecordingChatCompletionClient(
        [
            new ChatCompletionStreamEvent(
                ChatCompletionStreamEventKind.Content,
                """
                {"pattern_kind":"markdown_heading","delimiter_template":"# {title}","confidence":0.9,"evidence_offsets":[0],"source_text":"leak"}
                """)
        ]);
        var analyzer = new ReferenceChapterSplitChatCompletionAnalyzer(
            new FixedAppSettingsService("qwen/qwen-plus", "high"),
            chat);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await analyzer.AnalyzeAsync(
                new ReferenceChapterSplitModelRequest(99, "source-hash", "# 第一章\n\n正文。"),
                CancellationToken.None));

        Assert.Contains("invalid structured output", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingChatCompletionClient(IReadOnlyList<ChatCompletionStreamEvent> events) : IChatCompletionClient
    {
        public ChatCompletionRequest? LastRequest { get; private set; }

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            await Task.CompletedTask;
            foreach (var item in events)
            {
                yield return item;
            }
        }
    }

    private sealed class FixedAppSettingsService(string selectedModelKey, string reasoningEffort) : IAppSettingsService
    {
        public ValueTask<AppSettingsPayload> GetSettingsAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new AppSettingsPayload(1, 0, selectedModelKey, reasoningEffort, "manual", 360, string.Empty, string.Empty));
        }

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
}
