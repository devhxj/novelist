using System.Runtime.CompilerServices;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceChapterMaterialExtractorTests
{
    static ReferenceChapterMaterialExtractorTests()
    {
        // 测试不等待材料化的 30 秒请求间隔（静态共享，全部测试统一写 0）。
        ReferenceMaterializationChatCompletionQualifier.MinRequestGap = TimeSpan.Zero;
    }

    [Fact]
    public async Task ExtractChapterMaterialsParsesVerbatimMaterialsAndDefaultsUnknownTypes()
    {
        var chat = new ScriptedChatCompletionClient(
        [
            [ReferenceChapterMaterialExtractorTests.ToolCall("""
                {"materials":[
                  {"excerpt":"他推门而入，屋里安静得能听见雨声。","material_type":"dialogue_exchange","tags":{"narrative_functions":["reveal","情绪张力"],"emotion_mechanics":[],"pov":[],"techniques":["subtext"],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"scores":{"semantic_completeness":0.9,"information_density":0.7,"narrative_value":0.8,"transferability":0.6,"context_independence":0.7,"technique_distinctiveness":0.6},"confidence":0.9,"reason_codes":["complete_exchange","自造原因"]},
                  {"excerpt":"第二段摘录材料。","material_type":"未知类型","tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"confidence":0.4,"reason_codes":[]}
                ]}
                """)]
        ]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await extractor.ExtractChapterMaterialsAsync(
            new ReferenceChapterExtractionRequest(
                1,
                1,
                "第一章",
                "他推门而入，屋里安静得能听见雨声。",
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            CancellationToken.None);

        // 首批不足额（2 < 12）即视为章节提取完毕：只发一次请求。
        Assert.Equal(1, chat.Requests.Count);
        Assert.Equal(2, result.Materials.Count);
        Assert.Equal("dialogue_exchange", result.Materials[0].MaterialType);
        Assert.Equal("他推门而入，屋里安静得能听见雨声。", result.Materials[0].Excerpt);
        Assert.Equal(["reveal"], result.Materials[0].Tags.NarrativeFunctions);
        Assert.Equal(["subtext"], result.Materials[0].Tags.Techniques);
        Assert.Equal(["complete_exchange"], result.Materials[0].ReasonCodes);
        // 未知材料类型回退为 passage；未知标签/原因值被丢弃。
        Assert.Equal("passage", result.Materials[1].MaterialType);
        Assert.Empty(result.Materials[1].Tags.NarrativeFunctions);
        Assert.Empty(result.Materials[1].ReasonCodes);
    }

    [Fact]
    public async Task ExtractionPaginatesOutputAcrossBatchesUntilExhausted()
    {
        // 每批最多 24 条：材料池 30 条时，首批满额、第二批只剩 6 条（不足额）即终止。
        var chapterText = new string('文', 5_000);
        var chat = new PaginatingChatCompletionClient(poolSize: 30, perBatch: 16);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);
        var heartbeatCount = 0;

        var result = await extractor.ExtractChapterMaterialsAsync(
            new ReferenceChapterExtractionRequest(
                1,
                1,
                "第一章",
                chapterText,
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            CancellationToken.None,
            _ => { heartbeatCount++; return ValueTask.CompletedTask; });

        Assert.Equal(2, chat.Requests.Count);
        // 每完成一页触发一次活性心跳。
        Assert.Equal(chat.Requests.Count, heartbeatCount);
        Assert.Equal(30, result.Materials.Count);
        for (var i = 0; i < chat.Requests.Count; i++)
        {
            using var payload = JsonDocument.Parse(chat.Requests[i].Messages[1].Content);
            var root = payload.RootElement;
            // 整章始终全量输入，分页的是输出：载荷携带已提取摘录与偏移。
            Assert.Equal(chapterText, root.GetProperty("chapter_text").GetString());
            Assert.Equal(i * 16, root.GetProperty("material_offset").GetInt32());
            Assert.Equal(i * 16, root.GetProperty("extracted_excerpts").GetArrayLength());
        }

        Assert.Equal(30, result.Materials.Select(material => material.Excerpt).Distinct().Count());
    }

    [Fact]
    public async Task TransportInterruptionIsRetriedOnceAndSucceeds()
    {
        // 供应商掐流（ResponseEnded）是传输层错误：同页重试一次应救回；
        // 供应商侧拒绝（如限流）不适用此路径。
        var chat = new TransportFlakyChatCompletionClient(
            BuildBatchMaterialsJson(3));
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await extractor.ExtractChapterMaterialsAsync(
            new ReferenceChapterExtractionRequest(
                1,
                1,
                "第一章",
                new string('文', 2_000),
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            CancellationToken.None);

        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(3, result.Materials.Count);
    }

    [Fact]
    public async Task MergedMaterialsAreCappedAtTheChapterLimit()
    {
        var chapterText = new string('文', 5_000);
        var chat = new PaginatingChatCompletionClient(poolSize: 999, perBatch: 16);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await extractor.ExtractChapterMaterialsAsync(
            new ReferenceChapterExtractionRequest(
                1,
                1,
                "第一章",
                chapterText,
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            CancellationToken.None);

        // 达到全局 40 条上限后停止继续分页。
        Assert.Equal(3, chat.Requests.Count);
        Assert.Equal(40, result.Materials.Count);
    }

    [Fact]
    public async Task FullyDuplicateBatchTerminatesPagination()
    {
        var chat = new ScriptedChatCompletionClient(
        [
            [ReferenceChapterMaterialExtractorTests.ToolCall(BuildBatchMaterialsJson(16))],
            [ReferenceChapterMaterialExtractorTests.ToolCall(BuildBatchMaterialsJson(16))],
        ]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await extractor.ExtractChapterMaterialsAsync(
            new ReferenceChapterExtractionRequest(
                1,
                1,
                "第一章",
                new string('文', 2_000),
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            CancellationToken.None);

        // 第二批全部与已提取重复：立即终止，不会无限分页。
        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(16, result.Materials.Count);
    }

    [Fact]
    public async Task HungModelStreamFailsAtRequestDeadlineInsteadOfHangingForever()
    {
        // 无人值守的材料化：活着但永不完成的模型流必须在请求总时限处失败，
        // 而不是把整批章节永久挂在 llm_qualifying。
        var chat = new HangingChatCompletionClient();
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);
        var original = ReferenceMaterializationChatCompletionQualifier.RequestDeadline;
        ReferenceMaterializationChatCompletionQualifier.RequestDeadline = TimeSpan.FromMilliseconds(200);
        try
        {
            var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
                await extractor.ExtractChapterMaterialsAsync(
                    new ReferenceChapterExtractionRequest(
                        1,
                        1,
                        "第一章",
                        new string('文', 2_000),
                        new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
                    CancellationToken.None));

            Assert.Equal(ReferenceMaterializationErrorCodes.LlmRequestFailed, exception.ErrorCode);
            Assert.Contains("未完成，已中止", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            ReferenceMaterializationChatCompletionQualifier.RequestDeadline = original;
        }
    }

    private static string BuildBatchMaterialsJson(int count)
    {
        var materials = string.Join(',', Enumerable.Range(0, count).Select(i => MaterialJson($"材料摘录编号{i:D4}")));
        return $"{{\"materials\":[{materials}]}}";
    }

    // 首次流式调用模拟供应商掐流（ResponseEnded），第二次正常返回。
    private sealed class TransportFlakyChatCompletionClient(string argumentsJson) : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            if (Requests.Count == 1)
            {
                throw new HttpRequestException("The response ended prematurely. (ResponseEnded)");
            }

            yield return ReferenceChapterMaterialExtractorTests.ToolCall(argumentsJson);
        }
    }

    private static ChatCompletionStreamEvent ToolCall(string argumentsJson) => new(
        ChatCompletionStreamEventKind.ToolCall,
        ToolCall: new ChatToolCall(
            "call-extraction",
            "submit_chapter_materials",
            argumentsJson));

    private static string MaterialJson(string excerpt) => $$"""
        {"excerpt":"{{excerpt}}","material_type":"passage","tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"confidence":0.5,"reason_codes":[]}
        """;

    // 脚本化假客户端：外层列表的每一项是一次请求的响应事件序列，按调用次序消费。
    // 永不产出的模型流：只在取消令牌触发时结束，模拟活着但永不完成的生成。
    private sealed class HangingChatCompletionClient : IChatCompletionClient
    {
        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
    }

    private sealed class ScriptedChatCompletionClient(
        IReadOnlyList<IReadOnlyList<ChatCompletionStreamEvent>> responsesPerCall) : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            await Task.CompletedTask;
            foreach (var item in responsesPerCall[Math.Min(index, responsesPerCall.Count - 1)])
            {
                yield return item;
            }
        }
    }

    // 按请求动态应答：读载荷中的已提取摘录数，返回接下来的新一批材料，
    // 材料池耗尽后返回空数组（"没有更多"）。
    private sealed class PaginatingChatCompletionClient(int poolSize, int perBatch) : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            using var payload = JsonDocument.Parse(request.Messages[1].Content);
            var alreadyExtracted = payload.RootElement.GetProperty("extracted_excerpts").GetArrayLength();
            var count = Math.Min(perBatch, Math.Max(poolSize - alreadyExtracted, 0));
            var materials = string.Join(',', Enumerable.Range(0, count)
                .Select(i => MaterialJson($"材料摘录编号{alreadyExtracted + i:D4}")));
            yield return ReferenceChapterMaterialExtractorTests.ToolCall($"{{\"materials\":[{materials}]}}");
        }
    }
}
