using System.Runtime.CompilerServices;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceChapterMaterialExtractorTests
{
    static ReferenceChapterMaterialExtractorTests()
    {
        // 测试不等待材料化的 30 秒请求间隔（静态共享，全部测试统一写 0）。
        ReferenceMaterializationChatCompletionQualifier.MinRequestGap = TimeSpan.Zero;
        // 中断重试的退避同理：真实值 10/20 秒，用例只验证重试发生与次数上限。
        ReferenceMaterializationChatCompletionQualifier.RoundRetryBackoff = TimeSpan.FromMilliseconds(1);
    }

    [Fact]
    public async Task ExtractChapterRoundParsesVerbatimMaterialsAndDefaultsUnknownTypes()
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

        var result = await extractor.ExtractChapterRoundAsync(
            new ReferenceChapterExtractionRequest(
                1,
                1,
                "第一章",
                "他推门而入，屋里安静得能听见雨声。第二段摘录材料。",
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            new ReferenceChapterExtractionRound(
                ReferenceMaterializationCandidateTypes.All.Where(type => type is "passage" or "dialogue_exchange" or "action_reaction" or "emotion" or "hook" or "payoff").ToArray(),
                "all six kinds"),
            CancellationToken.None);

        Assert.Single(chat.Requests);
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
    public async Task PlanningParsesTypePartitionAndRejectsBrokenCoverage()
    {
        var chat = new ScriptedChatCompletionClient(
        [
            // 首次：遗漏 emotion 类型 → 拒绝；二次：完整划分 → 通过。
            [ReferenceChapterMaterialExtractorTests.PlanningToolCall("""{"passes":[{"material_types":["passage"],"focus":"descriptive"},{"material_types":["hook","payoff"],"focus":"structural"}]}""")],
            [ReferenceChapterMaterialExtractorTests.PlanningToolCall("""{"passes":[{"material_types":["dialogue_exchange","action_reaction"],"focus":"interactive"},{"material_types":["emotion"],"focus":"feelings"},{"material_types":["hook","payoff"],"focus":"structural"},{"material_types":["passage"],"focus":"descriptive"}]}""")],
        ]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var plan = await extractor.PlanChapterExtractionAsync(
            new ReferenceChapterExtractionRequest(1, 1, "第一章", new string('文', 2_000),
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            CancellationToken.None);

        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(4, plan.Count);
        // 类型划分不重不漏，趟顺序保留模型给的顺序。
        Assert.Equal(["dialogue_exchange", "action_reaction"], plan[0].MaterialTypes);
        Assert.Equal(["emotion"], plan[1].MaterialTypes);
        Assert.Equal(["hook", "payoff"], plan[2].MaterialTypes);
        Assert.Equal(["passage"], plan[3].MaterialTypes);
    }

    [Fact]
    public async Task RoundExtractionDropsMaterialsOutsideThePassTypes()
    {
        // 模型越界返回非本趟类型：代码强制丢弃，只剩本趟类型。
        var materialsJson = """
            {"materials":[
              {"excerpt":"他推门而入，屋里安静得能听见雨声。","material_type":"dialogue_exchange","tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"scores":{"semantic_completeness":0.9,"information_density":0.7,"narrative_value":0.8,"transferability":0.6,"context_independence":0.7,"technique_distinctiveness":0.6},"confidence":0.9,"reason_codes":[]},
              {"excerpt":"雨声压住窗沿，他想起昨夜的电话。","material_type":"passage","tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"confidence":0.5,"reason_codes":[]}
            ]}
            """;
        var chat = new ScriptedChatCompletionClient([[ReferenceChapterMaterialExtractorTests.ToolCall(materialsJson)]]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);
        var chapterText = "他推门而入，屋里安静得能听见雨声。雨声压住窗沿，他想起昨夜的电话。";

        var result = await extractor.ExtractChapterRoundAsync(
            new ReferenceChapterExtractionRequest(1, 1, "第一章", chapterText,
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            new ReferenceChapterExtractionRound(["dialogue_exchange"], "interactive beats"),
            CancellationToken.None);

        var material = Assert.Single(result.Materials);
        Assert.Equal("dialogue_exchange", material.MaterialType);
    }

    [Fact]
    public async Task PlanningFallsBackToFixedGroupingAfterTwoBrokenAttempts()
    {
        // 两次计划都不合格（重复类型 + 遗漏类型）：固定分组兜底，覆盖全部六种类型。
        var chat = new ScriptedChatCompletionClient(
        [
            [ReferenceChapterMaterialExtractorTests.PlanningToolCall("""{"passes":[{"material_types":["passage","passage"],"focus":"dup"},{"material_types":["hook","payoff"],"focus":"structural"}]}""")],
            [ReferenceChapterMaterialExtractorTests.PlanningToolCall("""{"passes":[{"material_types":["emotion"],"focus":"feelings"}]}""")],
        ]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var plan = await extractor.PlanChapterExtractionAsync(
            new ReferenceChapterExtractionRequest(1, 1, "第一章", new string('文', 2_000),
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            CancellationToken.None);

        Assert.Equal(2, chat.Requests.Count);
        var allTypes = plan.SelectMany(round => round.MaterialTypes).ToArray();
        Assert.Equal(
            new[]
            {
                ReferenceMaterializationCandidateTypes.Passage,
                ReferenceMaterializationCandidateTypes.DialogueExchange,
                ReferenceMaterializationCandidateTypes.ActionReaction,
                ReferenceMaterializationCandidateTypes.Emotion,
                ReferenceMaterializationCandidateTypes.Hook,
                ReferenceMaterializationCandidateTypes.Payoff
            }.OrderBy(type => type, StringComparer.Ordinal).ToArray(),
            allTypes.OrderBy(type => type, StringComparer.Ordinal).ToArray());
        Assert.Equal(allTypes.Length, allTypes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task RoundExtractionRetriesTransportInterruptionOnce()
    {
        // 趟路径同样受传输掐流影响：掐流一次、重试成功，不应让整章失败。
        var chat = new TransportFlakyChatCompletionClient(BuildBatchMaterialsJson(3));
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await extractor.ExtractChapterRoundAsync(
            new ReferenceChapterExtractionRequest(1, 1, "第一章", new string('文', 2_000),
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            new ReferenceChapterExtractionRound(ReferenceMaterializationCandidateTypes.All, "all kinds"),
            CancellationToken.None);

        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(3, result.Materials.Count);
    }

    [Fact]
    public async Task RoundExtractionRetriesTruncatedToolArguments()
    {
        // 掐流的另一种表现：流"正常"结束但工具实参只写了一半。同属可重试的传输类故障，
        // 换一次请求就能拿到完整载荷，不该把整章判死。
        var chat = new ScriptedChatCompletionClient(
        [
            [ReferenceChapterMaterialExtractorTests.ToolCall("""{"materials":[{"excerpt":"他推门而入，屋里安静得能听见雨声。","material_type":"passage","tags":{"narrative_functions":""")],
            [ReferenceChapterMaterialExtractorTests.ToolCall(BuildBatchMaterialsJson(3))],
        ]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await extractor.ExtractChapterRoundAsync(
            new ReferenceChapterExtractionRequest(1, 1, "第一章", new string('文', 2_000),
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            new ReferenceChapterExtractionRound(["passage"], "passages"),
            CancellationToken.None);

        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal(3, result.Materials.Count);
    }

    [Fact]
    public async Task RoundExtractionStopsRetryingAfterMaxAttempts()
    {
        // 每次都被截断：重试有上限，超限后如实报错并保留底层 JSON 异常供诊断。
        var chat = new ScriptedChatCompletionClient(
        [
            [ReferenceChapterMaterialExtractorTests.ToolCall("{\"materials\":[{\"excerpt\":\"他推门而入")],
        ]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await extractor.ExtractChapterRoundAsync(
                new ReferenceChapterExtractionRequest(1, 1, "第一章", new string('文', 2_000),
                    new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
                new ReferenceChapterExtractionRound(["passage"], "passages"),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
        Assert.Equal(
            ReferenceMaterializationChatCompletionQualifier.MaxRoundAttempts,
            chat.Requests.Count);
    }

    [Fact]
    public async Task RoundExtractionDoesNotRetryProviderBurstProtection()
    {
        // 限流/突发保护是冷却窗口型失败：机器重试会延长窗口，必须一次即失败。
        var chat = new BurstProtectedChatCompletionClient();
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await extractor.ExtractChapterRoundAsync(
                new ReferenceChapterExtractionRequest(1, 1, "第一章", new string('文', 2_000),
                    new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
                new ReferenceChapterExtractionRound(["passage"], "passages"),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmRequestFailed, exception.ErrorCode);
        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task RoundExtractionKeepsOnlyTheStrongestMaterialsWithinPassBudget()
    {
        // 单趟输出预算双重封顶：条数上限 10，摘录总字数上限 6,000。
        var inScopePassages = string.Join(',', Enumerable.Range(0, 8).Select(i =>
            MaterialJson($"材料摘录编号{i:D4}")));
        var outOfScope = string.Join(',', Enumerable.Range(0, 6).Select(i =>
            MaterialJsonOfType("emotion", $"情绪素材编号{i:D4}")));
        var longExcerpts = string.Join(',', Enumerable.Range(0, 10).Select(i =>
            MaterialJson($"{i:D3}" + new string('文', 697))));
        var chat = new ScriptedChatCompletionClient(
        [
            [ReferenceChapterMaterialExtractorTests.ToolCall($"{{\"materials\":[{outOfScope},{inScopePassages}]}}")],
            [ReferenceChapterMaterialExtractorTests.ToolCall($"{{\"materials\":[{longExcerpts}]}}")],
        ]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        var capped = await extractor.ExtractChapterRoundAsync(
            new ReferenceChapterExtractionRequest(1, 1, "第一章", new string('文', 2_000),
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            new ReferenceChapterExtractionRound(["passage"], "passages"),
            CancellationToken.None);
        var volumeCapped = await extractor.ExtractChapterRoundAsync(
            new ReferenceChapterExtractionRequest(1, 1, "第一章", new string('文', 2_000),
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            new ReferenceChapterExtractionRound(["passage"], "passages"),
            CancellationToken.None);

        // 载荷共 14 条（6 条 emotion 越界 + 8 条 passage）：越界项若先占额度，本趟只剩 4 条。
        Assert.Equal(8, capped.Materials.Count);
        Assert.All(capped.Materials, material => Assert.Equal("passage", material.MaterialType));
        // 10 条 700 字摘录共 7,000 字：第 9 条会越过 6,000 字预算，取前 8 条。
        Assert.Equal(8, volumeCapped.Materials.Count);
        Assert.Equal(700, volumeCapped.Materials[0].Excerpt.Length);
        // 提示词里的额度必须是被代码执行的那一个。
        Assert.Contains("at most 10 materials", chat.Requests[0].Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("6000 characters of excerpt text", chat.Requests[0].Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoundExtractionPromptHasNoPaginationSemantics()
    {
        // 趟提示词不能携带分页语义（extracted_excerpts / 不足额即终止）：
        // 趟请求的载荷没有这些字段，模型按分页语义会错误收敛或凑数。
        var chat = new ScriptedChatCompletionClient(
            [[ReferenceChapterMaterialExtractorTests.ToolCall("""{"materials":[]}""")]]);
        var extractor = new ReferenceMaterializationChatCompletionQualifier(chat);

        await extractor.ExtractChapterRoundAsync(
            new ReferenceChapterExtractionRequest(1, 1, "第一章", new string('文', 2_000),
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
            new ReferenceChapterExtractionRound(["emotion"], "feelings"),
            CancellationToken.None);

        var systemPrompt = chat.Requests[0].Messages[0].Content;
        Assert.DoesNotContain("extracted_excerpts", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("paginated", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("pass_material_types", systemPrompt, StringComparison.Ordinal);
        // 载荷只带趟类型与镜头，不带分页字段。
        using var payload = JsonDocument.Parse(chat.Requests[0].Messages[1].Content);
        Assert.Equal("emotion", payload.RootElement.GetProperty("pass_material_types")[0].GetString());
        Assert.False(payload.RootElement.TryGetProperty("extracted_excerpts", out _));
        Assert.False(payload.RootElement.TryGetProperty("material_offset", out _));
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
                await extractor.ExtractChapterRoundAsync(
                    new ReferenceChapterExtractionRequest(
                        1,
                        1,
                        "第一章",
                        new string('文', 2_000),
                        new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high")),
                    new ReferenceChapterExtractionRound(["emotion"], "feelings"),
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

    private static ChatCompletionStreamEvent PlanningToolCall(string argumentsJson) => new(
        ChatCompletionStreamEventKind.ToolCall,
        ToolCall: new ChatToolCall(
            "call-planning",
            "plan_chapter_extraction",
            argumentsJson));

    private static string MaterialJson(string excerpt) => MaterialJsonOfType("passage", excerpt);

    private static string MaterialJsonOfType(string materialType, string excerpt) => $$"""
        {"excerpt":"{{excerpt}}","material_type":"{{materialType}}","tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"confidence":0.5,"reason_codes":[]}
        """;

    // 供应商突发保护：请求被服务端拒绝（冷却窗口型失败），材料化必须一次即失败。
    private sealed class BurstProtectedChatCompletionClient : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            // 供应商收到请求即拒绝（冷却窗口型失败），流根本不产生事件。
            throw new BridgeRequestException(
                BridgeErrorCodes.LlmProviderError,
                "System protection triggered by request burst. Please slow down traffic growth.",
                retryable: true);
        }
    }

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
}
