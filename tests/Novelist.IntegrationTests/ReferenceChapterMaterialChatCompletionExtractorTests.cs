using System.Runtime.CompilerServices;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceChapterMaterialChatCompletionExtractorTests
{
    [Fact]
    public async Task ExtractAsyncSendsTheWholeChapterAndReturnsCrossParagraphSourceMaterial()
    {
        const string chapter = "雨声压住窗沿。\n\n“你还是来了？”\n\n“我答应过你。”\n\n她把门彻底推开。";
        var chat = new RecordingChatCompletionClient(
        [
            ToolCall(
                """
                {"materials":[{"material_type":"dialogue","text":"“你还是来了？”\n\n“我答应过你。”","description":"用简短应答兑现先前承诺。","tags":["dialogue","promise"]}]}
                """)
        ]);
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(chat);

        var result = await extractor.ExtractAsync(
            new ReferenceChapterMaterialExtractionRequest(
                new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                AnchorId: 17,
                ChapterIndex: 3,
                ChapterTitle: "第三章 赴约",
                ChapterText: chapter),
            CancellationToken.None);

        var material = Assert.Single(result.Materials);
        Assert.Equal("dialogue", material.MaterialType);
        Assert.Equal("“你还是来了？”\n\n“我答应过你。”", material.Text);
        Assert.Equal("用简短应答兑现先前承诺。", material.Description);
        Assert.Equal(["dialogue", "promise"], material.Tags);

        Assert.Equal("qwen", chat.LastRequest?.ProviderName);
        Assert.Equal("qwen-plus", chat.LastRequest?.ModelId);
        Assert.Equal("high", chat.LastRequest?.ReasoningEffort);
        Assert.Equal(0, chat.LastRequest?.TemperatureOverride);
        using var requestJson = JsonDocument.Parse(chat.LastRequest!.Messages[1].Content);
        Assert.Equal(chapter, requestJson.RootElement.GetProperty("chapter_text").GetString());
        Assert.Equal("第三章 赴约", requestJson.RootElement.GetProperty("chapter_title").GetString());
        var tool = Assert.Single(chat.LastRequest!.Tools!);
        Assert.Equal("submit_reference_chapter_materials", tool.Name);
        Assert.True(tool.Strict);
    }

    [Fact]
    public async Task ExtractAsyncDoesNotTruncateOrWindowLongChapters()
    {
        var chapter = "第一段。\n\n" + new string('甲', 200_000) + "\n\n结尾证据。";
        var chat = new RecordingChatCompletionClient(
        [
            ToolCall(
                """
                {"materials":[{"material_type":"ending","text":"结尾证据。","description":"章节收束。","tags":["ending"]}]}
                """)
        ]);
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(chat);

        var result = await extractor.ExtractAsync(
            new ReferenceChapterMaterialExtractionRequest(
                new ReferenceMaterializationLlmSelection("openai", "gpt-test", "medium"),
                AnchorId: 4,
                ChapterIndex: 1,
                ChapterTitle: "第一章",
                ChapterText: chapter),
            CancellationToken.None);

        Assert.Equal("结尾证据。", Assert.Single(result.Materials).Text);
        using var requestJson = JsonDocument.Parse(chat.LastRequest!.Messages[1].Content);
        Assert.Equal(chapter, requestJson.RootElement.GetProperty("chapter_text").GetString());
    }

    [Theory]
    [InlineData("{\"materials\":[]}", ReferenceMaterializationErrorCodes.NoMaterials)]
    [InlineData("{\"materials\":[{\"material_type\":\"dialogue\",\"text\":\"被模型改写的句子。\",\"description\":\"改写。\",\"tags\":[\"dialogue\"]}]}", ReferenceMaterializationErrorCodes.SourceTextMismatch)]
    [InlineData("{\"materials\":[{\"material_type\":\"dialogue\",\"text\":\"原文证据。\",\"description\":\"有效。\",\"tags\":[\"dialogue\"]},{\"material_type\":\"dialogue\",\"text\":\"不存在的证据。\",\"description\":\"无效。\",\"tags\":[\"dialogue\"]}]}", ReferenceMaterializationErrorCodes.SourceTextMismatch)]
    [InlineData("{\"materials\":[{\"material_type\":\"dialogue\",\"text\":\"原文证据。\",\"description\":\"有效。\",\"tags\":[\"dialogue\"]},{\"material_type\":\"dialogue\",\"text\":\"原文证据。\",\"description\":\"重复。\",\"tags\":[\"dialogue\"]}]}", ReferenceMaterializationErrorCodes.LlmOutputInvalid)]
    public async Task ExtractAsyncRejectsTheWholeResultWhenAnyMaterialIsInvalid(
        string response,
        string expectedErrorCode)
    {
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(
            new RecordingChatCompletionClient([ToolCall(response)]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await extractor.ExtractAsync(
                new ReferenceChapterMaterialExtractionRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    AnchorId: 9,
                    ChapterIndex: 1,
                    ChapterTitle: "第一章",
                    ChapterText: "原文证据。"),
                CancellationToken.None));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
    }

    [Fact]
    public async Task ExtractAsyncRequiresTheStrictToolCall()
    {
        var extractor = new ReferenceChapterMaterialChatCompletionExtractor(
            new RecordingChatCompletionClient(
            [
                new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Content,
                    "{\"materials\":[]}")
            ]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await extractor.ExtractAsync(
                new ReferenceChapterMaterialExtractionRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    AnchorId: 9,
                    ChapterIndex: 1,
                    ChapterTitle: "第一章",
                    ChapterText: "原文证据。"),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
    }

    private static ChatCompletionStreamEvent ToolCall(string argumentsJson) =>
        new(
            ChatCompletionStreamEventKind.ToolCall,
            ToolCall: new ChatToolCall(
                "call-materials",
                "submit_reference_chapter_materials",
                argumentsJson));

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
}
