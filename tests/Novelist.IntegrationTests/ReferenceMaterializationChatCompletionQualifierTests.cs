using System.Runtime.CompilerServices;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationChatCompletionQualifierTests
{
    static ReferenceMaterializationChatCompletionQualifierTests()
    {
        // 测试不等待材料化的 30 秒请求间隔（静态共享，全部测试统一写 0）。
        ReferenceMaterializationChatCompletionQualifier.MinRequestGap = TimeSpan.Zero;
    }

    [Fact]
    public async Task QualifyAsyncUsesFrozenModelAndReturnsOneValidatedDecisionForEveryCandidate()
    {
        var chat = new RecordingChatCompletionClient(
        [
            new ChatCompletionStreamEvent(
                ChatCompletionStreamEventKind.ToolCall,
                ToolCall: new ChatToolCall(
                    "call-qualification",
                    "submit_materialization_qualification",
                    """
                {"decisions":[{"candidate_id":"candidate-a","decision":"accept","source_spans":[{"node_id":"node-a","start":0,"end":7}],"scores":{"semantic_completeness":0.91,"information_density":0.72,"narrative_value":0.83,"transferability":0.61,"context_independence":0.75,"technique_distinctiveness":0.69},"tags":{"narrative_functions":["reveal"],"emotion_mechanics":["escalation"],"pov":["close_third"],"techniques":["subtext"],"scene_beat_roles":["turn_beat"],"character_relations":["mistrust"],"causal_information_roles":["reveal"]},"confidence":0.84,"reason_codes":["complete_exchange"]},{"candidate_id":"candidate-b","decision":"reject","source_spans":[{"node_id":"node-b","start":0,"end":5}],"scores":{"semantic_completeness":0.15,"information_density":0.12,"narrative_value":0.09,"transferability":0.03,"context_independence":0.18,"technique_distinctiveness":0.04},"tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"confidence":0.93,"reason_codes":["context_dependent"]}]}
                """))
        ]);
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await qualifier.QualifyAsync(
            new ReferenceMaterializationQualificationRequest(
                new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                [
                    Candidate("candidate-a", "node-a", "他说出了真相。\n他没有回头。"),
                    Candidate("candidate-b", "node-b", "他点了头。")
                ]),
            CancellationToken.None);

        Assert.Equal(2, result.Decisions.Count);
        Assert.Equal(ReferenceMaterializationCandidateDecisions.Accepted, result.Decisions[0].Decision);
        Assert.Equal("node-a", Assert.Single(result.Decisions[0].SourceSpans).NodeId);
        Assert.Equal(["turn_beat"], result.Decisions[0].Tags.SceneBeatRoles);
        Assert.Equal(["mistrust"], result.Decisions[0].Tags.CharacterRelations);
        Assert.Equal(["reveal"], result.Decisions[0].Tags.CausalInformationRoles);
        Assert.Equal(ReferenceMaterializationCandidateDecisions.Rejected, result.Decisions[1].Decision);
        Assert.Equal("qwen", chat.LastRequest?.ProviderName);
        Assert.Equal("qwen-plus", chat.LastRequest?.ModelId);
        Assert.Equal("high", chat.LastRequest?.ReasoningEffort);
        Assert.Equal(0, chat.LastRequest?.TemperatureOverride);
        Assert.Equal(40_960, chat.LastRequest?.MaxOutputTokens);
        Assert.True(chat.LastRequest?.RequireToolCall);
        var tool = Assert.Single(chat.LastRequest!.Tools!);
        Assert.Equal("submit_materialization_qualification", tool.Name);
        Assert.True(tool.Strict);
        Assert.Contains("Call submit_materialization_qualification exactly once", chat.LastRequest?.Messages[0].Content, StringComparison.Ordinal);
        Assert.Contains("candidate-a", chat.LastRequest?.Messages[1].Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"decisions\":[{\"candidate_id\":\"candidate-a\",\"decision\":\"approve\",\"source_spans\":[{\"node_id\":\"node-a\",\"start\":0,\"end\":7}],\"scores\":{\"semantic_completeness\":0.5,\"information_density\":0.5,\"narrative_value\":0.5,\"transferability\":0.5,\"context_independence\":0.5,\"technique_distinctiveness\":0.5},\"tags\":{\"narrative_functions\":[],\"emotion_mechanics\":[],\"pov\":[],\"techniques\":[]},\"confidence\":0.5,\"reason_codes\":[\"complete_exchange\"]}]}")]
    [InlineData("{\"decisions\":[{\"candidate_id\":\"candidate-a\",\"decision\":\"accept\",\"source_spans\":[{\"node_id\":\"node-b\",\"start\":0,\"end\":7}],\"scores\":{\"semantic_completeness\":0.5,\"information_density\":0.5,\"narrative_value\":0.5,\"transferability\":0.5,\"context_independence\":0.5,\"technique_distinctiveness\":0.5},\"tags\":{\"narrative_functions\":[],\"emotion_mechanics\":[],\"pov\":[],\"techniques\":[]},\"confidence\":0.5,\"reason_codes\":[\"complete_exchange\"]}]}")]
    [InlineData("{\"decisions\":[{\"candidate_id\":\"candidate-a\",\"decision\":\"accept\",\"source_spans\":[{\"node_id\":\"node-a\",\"start\":0,\"end\":99}],\"scores\":{\"semantic_completeness\":0.5,\"information_density\":0.5,\"narrative_value\":0.5,\"transferability\":0.5,\"context_independence\":0.5,\"technique_distinctiveness\":0.5},\"tags\":{\"narrative_functions\":[],\"emotion_mechanics\":[],\"pov\":[],\"techniques\":[]},\"confidence\":0.5,\"reason_codes\":[\"complete_exchange\"]}]}")]
    [InlineData("{\"decisions\":[{\"candidate_id\":\"candidate-a\",\"decision\":\"accept\",\"source_spans\":[{\"node_id\":\"node-a\",\"start\":0,\"end\":7}],\"scores\":{\"semantic_completeness\":0.5,\"information_density\":0.5,\"narrative_value\":0.5,\"transferability\":0.5,\"context_independence\":0.5,\"technique_distinctiveness\":0.5},\"tags\":{\"narrative_functions\":[],\"emotion_mechanics\":[],\"pov\":[],\"techniques\":[]},\"confidence\":0.5,\"reason_codes\":[\"complete_exchange\"],\"new_text\":\"invented\"}]}")]
    public async Task QualifyAsyncRejectsInvalidOrUngroundedOutput(string toolArguments)
    {
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(
            new RecordingChatCompletionClient([ToolCall(toolArguments)]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await qualifier.QualifyAsync(
                new ReferenceMaterializationQualificationRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    [Candidate("candidate-a", "node-a", "他说出了真相。")]),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
    }

    [Fact]
    public async Task QualifyAsyncRejectsPartialDecisionSets()
    {
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(
            new RecordingChatCompletionClient([ToolCall("""
                {"decisions":[{"candidate_id":"candidate-a","decision":"accept","source_spans":[{"node_id":"node-a","start":0,"end":7}],"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[]},"confidence":0.5,"reason_codes":["complete_exchange"]}]}
                """)]));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await qualifier.QualifyAsync(
                new ReferenceMaterializationQualificationRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    [Candidate("candidate-a", "node-a", "他说出了真相。"), Candidate("candidate-b", "node-b", "他点了头。")]),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
    }

    [Fact]
    public async Task QualifyAsyncDropsUnknownExtendedTagValues()
    {
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(
            new RecordingChatCompletionClient([ToolCall("""
                {"decisions":[{"candidate_id":"candidate-a","decision":"accept","source_spans":[{"node_id":"node-a","start":0,"end":7}],"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":["invented_beat"],"character_relations":[],"causal_information_roles":[]},"confidence":0.5,"reason_codes":["complete_exchange"]}]}
                """)]));

        var result = await qualifier.QualifyAsync(
            new ReferenceMaterializationQualificationRequest(
                new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                [Candidate("candidate-a", "node-a", "他说出了真相。")]),
            CancellationToken.None);

        // 未知扩展标签值被丢弃，不再让整章材料化失败。
        var decision = Assert.Single(result.Decisions);
        Assert.Empty(decision.Tags.SceneBeatRoles);
    }

    [Fact]
    public async Task QualifyAsyncIgnoresTextDeltasBeforeTheToolCall()
    {
        var chat = new RecordingChatCompletionClient(
        [
            new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "先输出一段说明文本。"),
            ToolCall("""
                {"decisions":[{"candidate_id":"candidate-a","decision":"accept","source_spans":[{"node_id":"node-a","start":0,"end":7}],"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"tags":{"narrative_functions":["reveal"],"emotion_mechanics":["escalation"],"pov":["close_third"],"techniques":["subtext"],"scene_beat_roles":["turn_beat"],"character_relations":["mistrust"],"causal_information_roles":["reveal"]},"confidence":0.5,"reason_codes":["complete_exchange"]}]}
                """)
        ]);
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await qualifier.QualifyAsync(
            new ReferenceMaterializationQualificationRequest(
                new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                [Candidate("candidate-a", "node-a", "他说出了真相。")]),
            CancellationToken.None);

        Assert.Single(result.Decisions);
    }

    [Fact]
    public async Task QualifyAsyncDropsUnknownTagValuesAndKeepsTheDecision()
    {
        // 2026-09-07 回归：deepseek-v4-flash 偶尔把标签本地化/发明清单外的值，
        // 此前一个未知值就废掉整章；现在未知值丢弃，决策与评分保留。
        var chat = new RecordingChatCompletionClient(
        [
            ToolCall("""
                {"decisions":[{"candidate_id":"candidate-a","decision":"accept","source_spans":[{"node_id":"node-a","start":0,"end":7}],"scores":{"semantic_completeness":0.9,"information_density":0.7,"narrative_value":0.8,"transferability":0.6,"context_independence":0.7,"technique_distinctiveness":0.6},"tags":{"narrative_functions":["reveal","情绪张力"],"emotion_mechanics":["escalation","未知机制"],"pov":["第三人称"],"techniques":["subtext"],"scene_beat_roles":["turn_beat"],"character_relations":["mistrust"],"causal_information_roles":["reveal"]},"confidence":0.9,"reason_codes":["complete_exchange","自造原因"]}]}
                """)
        ]);
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await qualifier.QualifyAsync(
            new ReferenceMaterializationQualificationRequest(
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high"),
                [Candidate("candidate-a", "node-a", "他说出了真相。")]),
            CancellationToken.None);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(ReferenceMaterializationCandidateDecisions.Accepted, decision.Decision);
        Assert.Equal(["reveal"], decision.Tags.NarrativeFunctions);
        Assert.Equal(["escalation"], decision.Tags.EmotionMechanics);
        Assert.Empty(decision.Tags.Pov);
        Assert.Equal(["subtext"], decision.Tags.Techniques);
        Assert.Equal(["turn_beat"], decision.Tags.SceneBeatRoles);
        Assert.Equal(["complete_exchange"], decision.ReasonCodes);
    }

    [Fact]
    public async Task QualifyAsyncRepairsUnusableSourceSpansInsteadOfFailingTheChapter()
    {
        // 2026-09-11 回归：模型偶尔给出越界偏移、未知节点或整项漏掉的证据区间，
        // 此前一个坏区间就废掉整章。区间只是复核用元数据——候选文本由候选自身的
        // 节点证据拼出（晋升读 reference_material_candidate_nodes）——所以丢坏项、
        // 用节点自身区间补齐、保留决策与评分。
        var chat = new RecordingChatCompletionClient(
        [
            ToolCall("""
                {"decisions":[{"candidate_id":"candidate-a","decision":"accept","source_spans":[{"node_id":"node-a","start":0,"end":999},{"node_id":"node-ghost","start":0,"end":3}],"scores":{"semantic_completeness":0.9,"information_density":0.7,"narrative_value":0.8,"transferability":0.6,"context_independence":0.7,"technique_distinctiveness":0.6},"tags":{"narrative_functions":["reveal"],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"confidence":0.9,"reason_codes":["standalone_reveal"]},{"candidate_id":"candidate-b","decision":"accept","source_spans":[],"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"confidence":0.5,"reason_codes":["complete_exchange"]}]}
                """)
        ]);
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(chat);

        var result = await qualifier.QualifyAsync(
            new ReferenceMaterializationQualificationRequest(
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high"),
                [Candidate("candidate-a", "node-a", "他说出了真相。"), Candidate("candidate-b", "node-b", "他点了头。")]),
            CancellationToken.None);

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision => Assert.Equal(ReferenceMaterializationCandidateDecisions.Accepted, decision.Decision));
        var repaired = Assert.Single(result.Decisions[0].SourceSpans);
        Assert.Equal("node-a", repaired.NodeId);
        // 越界偏移被丢弃、未知节点被忽略后，回落到该节点自身的完整区间。
        Assert.Equal(0, repaired.Start);
        Assert.Equal("他说出了真相。".Length, repaired.End);
        var filled = Assert.Single(result.Decisions[1].SourceSpans);
        Assert.Equal("node-b", filled.NodeId);
        Assert.Equal("他点了头。".Length, filled.End);
    }

    [Fact]
    public async Task QualifyAsyncRejectsStructurallyInvalidDecisions()
    {
        // 决策字段本身（accept/reject/review_required 之外）仍然是硬约束。
        var chat = new RecordingChatCompletionClient(
        [
            ToolCall("""
                {"decisions":[{"candidate_id":"candidate-a","decision":"approve","source_spans":[{"node_id":"node-a","start":0,"end":7}],"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[]},"confidence":0.5,"reason_codes":["complete_exchange"]}]}
                """)
        ]);
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(chat);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await qualifier.QualifyAsync(
                new ReferenceMaterializationQualificationRequest(
                    new ReferenceMaterializationLlmSelection("qwen", "qwen-plus", "high"),
                    [Candidate("candidate-a", "node-a", "他说出了真相。")]),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.LlmOutputInvalid, exception.ErrorCode);
    }

    private static ChatCompletionStreamEvent ToolCall(string argumentsJson) => new(
        ChatCompletionStreamEventKind.ToolCall,
        ToolCall: new ChatToolCall(
            "call-qualification",
            "submit_materialization_qualification",
            argumentsJson));

    [Fact]
    public async Task QualifyAsyncRejectsRequestsThatExceedTheCandidateModelBatch()
    {
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(
            new RecordingChatCompletionClient([]));
        var candidates = Enumerable.Range(1, ReferenceMaterializationChatCompletionQualifier.MaxCandidatesPerRequest + 1)
            .Select(index => Candidate($"candidate-{index}", $"node-{index}", "他说出了真相。"))
            .ToArray();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await qualifier.QualifyAsync(
                new ReferenceMaterializationQualificationRequest(
                    new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-pro", "high"),
                    candidates),
                CancellationToken.None));
    }

    [Fact]
    public async Task QualifyAsyncSplitsTheBatchWhenItKeepsGettingCut()
    {
        // 整批恒定被掐：把候选对半拆开分别判定。拆的是批量大小，不是候选内容——
        // 每条候选的正文与溯源节点完整保留，判定标准与 schema 都不变，质量不动。
        var chat = new BatchSizeFlakyChatCompletionClient(maxCandidates: 2);
        var qualifier = new ReferenceMaterializationChatCompletionQualifier(chat);
        var candidates = Enumerable.Range(1, 4)
            .Select(index => Candidate($"candidate-{index}", $"node-{index}", "他说出了真相。"))
            .ToArray();

        var result = await qualifier.QualifyAsync(
            new ReferenceMaterializationQualificationRequest(
                new ReferenceMaterializationLlmSelection("deepseek", "deepseek-v4-flash", "high"),
                candidates),
            CancellationToken.None);

        // 4 条全部拿到判定；拆批后每次请求的候选数被压到阈值以下。
        Assert.Equal(4, result.Decisions.Count);
        Assert.Contains(chat.Requests, request => ReadCandidateCount(request) == 2);
        Assert.All(chat.Requests, request => Assert.True(ReadCandidateCount(request) <= 4));
    }

    private static int ReadCandidateCount(ChatCompletionRequest request)
    {
        using var document = JsonDocument.Parse(request.Messages[1].Content ?? "{}");
        return document.RootElement.GetProperty("candidates").GetArrayLength();
    }

    private static ReferenceMaterializationQualificationCandidate Candidate(string candidateId, string nodeId, string text)
    {
        return new ReferenceMaterializationQualificationCandidate(
            candidateId,
            "dialogue_exchange",
            text,
            [new ReferenceMaterializationQualificationSourceNode(nodeId, text)]);
    }

    // 只在"整批候选数"偏大时掐流：批量拆小后正常给判定。
    private sealed class BatchSizeFlakyChatCompletionClient(int maxCandidates) : IChatCompletionClient
    {
        public List<ChatCompletionRequest> Requests { get; } = [];

        public ValueTask<string> GenerateTextAsync(ChatCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            using var document = JsonDocument.Parse(request.Messages[1].Content ?? "{}");
            var candidates = document.RootElement.GetProperty("candidates");
            await Task.CompletedTask;
            if (candidates.GetArrayLength() > maxCandidates)
            {
                throw new HttpRequestException("The response ended prematurely. (ResponseEnded)");
            }

            var decisions = candidates.EnumerateArray().Select(candidate =>
                Decision(
                    candidate.GetProperty("candidate_id").GetString() ?? string.Empty,
                    candidate.GetProperty("source_nodes")[0].GetProperty("node_id").GetString() ?? string.Empty));
            yield return new ChatCompletionStreamEvent(
                ChatCompletionStreamEventKind.ToolCall,
                ToolCall: new ChatToolCall(
                    "call-qualification",
                    "submit_materialization_qualification",
                    $"{{\"decisions\":[{string.Join(',', decisions)}]}}"));
        }

        private static string Decision(string candidateId, string nodeId) => $$"""
            {"candidate_id":"{{candidateId}}","decision":"accept","source_spans":[{"node_id":"{{nodeId}}","start":0,"end":7}],"scores":{"semantic_completeness":0.5,"information_density":0.5,"narrative_value":0.5,"transferability":0.5,"context_independence":0.5,"technique_distinctiveness":0.5},"tags":{"narrative_functions":[],"emotion_mechanics":[],"pov":[],"techniques":[],"scene_beat_roles":[],"character_relations":[],"causal_information_roles":[]},"confidence":0.5,"reason_codes":["complete_exchange"]}
            """;
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
}
